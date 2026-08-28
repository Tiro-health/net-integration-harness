using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// The rolling transcript behind <see cref="FileTelemetrySink"/>: decides which file records
    /// go to, when to move to the next one, and what to delete. One instance per directory per
    /// process, shared by every sink pointing at that directory and closed when the last one
    /// releases it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One file per day, not one per session.</b> A file per session reads nicely — one file,
    /// one ticket — but it loses the bugs that span sessions, and in this codebase those are real:
    /// <c>SentrySdk.Init</c> is process-global and first-init-wins, so the viewer that caused a
    /// problem need not be the one that shows it. It also left sink-level captures, which are
    /// out-of-band of any session, with no file to belong to. Every record carries its
    /// <c>sid</c>, so pulling one session back out of a day is a <c>grep</c>.
    /// </para>
    /// <para>
    /// <b>Rolling discards the oldest records, never the newest.</b> A full file rolls to the next
    /// index rather than stopping, and retention deletes whole old files. The opposite — stopping
    /// when full — throws away the records closest to whatever went wrong.
    /// </para>
    /// <para>
    /// <b>Retention is sized to the support loop, not to disk.</b> A clinician hits a problem on
    /// Friday, IT raises a ticket on Monday, someone asks for the file on Tuesday: a window
    /// measured in hours is empty by the time anyone looks. Two bounds that do not multiply — an
    /// age and a total byte budget — rather than a per-file cap times a file count.
    /// </para>
    /// </remarks>
    internal sealed class RollingTelemetryLog : IDisposable
    {
        private const int SchemaVersion = 1;

        /// <summary>Inner-sink failures recorded per log before the rest are dropped.</summary>
        private const int MaxInnerErrorsLogged = 10;

        /// <summary>At most one sweep per hour per process, whoever asks.</summary>
        private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// Clock seam. The midnight roll and the retention cutoff are both date arithmetic that
        /// cannot be exercised against the real clock, and a silently broken day-roll would write
        /// a whole run into a file named for the wrong day.
        /// </summary>
        internal static Func<DateTime> UtcNowProvider = () => DateTime.UtcNow;

        private static readonly Dictionary<string, RollingTelemetryLog> Shared =
            new Dictionary<string, RollingTelemetryLog>(StringComparer.OrdinalIgnoreCase);

        private static readonly object SharedGate = new object();

        private readonly string _directory;
        private readonly string _key;
        private readonly long _maxBytesPerFile;
        private readonly long _maxTotalBytes;
        private readonly int _retentionDays;
        private readonly object _gate = new object();

        /// <summary>
        /// Paths that have refused a record for size. Without this, rolling reopens the file it
        /// just rolled away from: a file stops filling a little short of the cap — whatever the
        /// next record needed — so the plain <c>Bytes &gt;= cap</c> test says it still has room,
        /// the retry is refused again, and the log silently stops recording for the rest of the
        /// day. Cleared when the day turns, since the names change with it.
        /// </summary>
        private readonly HashSet<string> _full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private TelemetryRecordWriter _writer;
        private DateTime _fileDateUtc;
        private DateTime _lastSweepUtc;
        private int _refCount;
        private int _innerErrorsLogged;

        private RollingTelemetryLog(string directory, long maxBytesPerFile, long maxTotalBytes, int retentionDays)
        {
            _directory = directory;
            _key = Normalize(directory);
            _maxBytesPerFile = maxBytesPerFile;
            _maxTotalBytes = maxTotalBytes;
            _retentionDays = retentionDays;
        }

        /// <summary>
        /// The log for <paramref name="directory"/>, creating it on first use and reference-counting
        /// afterwards. Sharing one writer per directory is what makes a single day-file safe:
        /// concurrent viewers in one process are the common case, and each opening its own handle
        /// on the same path would mean all but the first silently losing its transcript, since the
        /// share mode has to deny other writers.
        /// </summary>
        public static RollingTelemetryLog Acquire(string directory, long maxBytesPerFile, long maxTotalBytes, int retentionDays)
        {
            var key = Normalize(directory);

            lock (SharedGate)
            {
                RollingTelemetryLog log;
                if (!Shared.TryGetValue(key, out log))
                {
                    log = new RollingTelemetryLog(directory, maxBytesPerFile, maxTotalBytes, retentionDays);
                    Shared[key] = log;
                }

                log._refCount++;
                return log;
            }
        }

        /// <summary>
        /// Drops one reference; the last one out closes the file. Not <see cref="Dispose"/>'s job
        /// alone, because several sinks share this and the first viewer to close must not take the
        /// transcript from the ones still open.
        /// </summary>
        public void Release()
        {
            lock (SharedGate)
            {
                if (--_refCount > 0) return;
                Shared.Remove(_key);
            }

            Dispose();
        }

        /// <summary>Writes a record, rolling first if the day has turned or the file is full.</summary>
        public void Write(string type, string sessionId, Action<Utf8JsonWriter> fields)
        {
            lock (_gate)
            {
                EnsureWriter();
                if (_writer == null) return;

                if (_writer.TryWrite(type, sessionId, fields)) return;

                // Refused for size. Nothing was written, so rolling and retrying loses no record —
                // which is the point: a full file must cost the oldest records, never this one.
                _full.Add(_writer.FilePath);
                Roll();
                if (_writer != null) _writer.TryWrite(type, sessionId, fields);
            }
        }

        /// <summary>
        /// Records that a wrapped sink threw. Capped per log, because a backend that throws once
        /// per call would otherwise fill the transcript with its own failure and bury the sessions
        /// it was opened to record.
        /// </summary>
        public void WriteInnerError(string sessionId, string member, Exception ex)
        {
            lock (_gate)
            {
                if (_innerErrorsLogged >= MaxInnerErrorsLogged) return;
                _innerErrorsLogged++;
            }

            Write("inner.error", sessionId, json =>
            {
                json.WriteString("member", member);
                json.WriteString("exc", ex.GetType().FullName);
                TelemetryRecordWriter.WriteValue(json, "msg", ex.Message);
            });
        }

        public void Flush()
        {
            lock (_gate)
            {
                _writer?.Flush();
            }
        }

        /// <summary>Path currently being written, for tests and diagnostics. Null if none opened.</summary>
        public string CurrentFilePath
        {
            get { lock (_gate) { return _writer?.FilePath; } }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private void EnsureWriter()
        {
            var today = UtcNowProvider().Date;

            if (_writer == null)
            {
                MaybeSweep();
                Open(today);
                return;
            }

            // A viewer left open across midnight keeps writing into yesterday's file otherwise,
            // which is the one case where a date in the name would start lying.
            if (_fileDateUtc != today)
            {
                _full.Clear();
                Roll();
            }
        }

        private void Roll()
        {
            _writer?.Dispose();
            _writer = null;
            MaybeSweep();
            Open(UtcNowProvider().Date);
        }

        /// <summary>
        /// Opens the day's file, or the first alternative that will accept a writer.
        /// <para>
        /// The plain <c>yyyyMMdd.jsonl</c> is tried first, so the ordinary case — one process, one
        /// day — is one obviously-named file. A suffix is added only when it has to be: the file is
        /// already at its size cap, or another process holds it (the share mode denies a second
        /// writer, and that refusal is the detection mechanism). Naming every file with a process
        /// id up front would fragment the common case to insure against the rare one.
        /// </para>
        /// </summary>
        private void Open(DateTime dateUtc)
        {
            var stem = dateUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var pid = Process.GetCurrentProcess().Id;

            foreach (var candidate in CandidateNames(stem, pid))
            {
                var path = Path.Combine(_directory, candidate);
                if (_full.Contains(path)) continue;        // already refused a record — try the next

                var writer = TelemetryRecordWriter.TryOpen(path, _maxBytesPerFile);
                if (writer == null) continue;              // held by another writer — try the next
                if (writer.Bytes >= _maxBytesPerFile)      // full from an earlier run — try the next
                {
                    writer.Dispose();
                    continue;
                }

                var isNewFile = writer.Bytes == 0;
                _writer = writer;
                _fileDateUtc = dateUtc;
                if (isNewFile) WriteFileHeader(pid);
                return;
            }
        }

        private static IEnumerable<string> CandidateNames(string stem, int pid)
        {
            yield return stem + ".jsonl";
            for (var i = 2; i <= 50; i++) yield return stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".jsonl";

            // Still nowhere to write: every plain name is full or taken. Fall back to this
            // process's own series so two processes on one account cannot deadlock each other out
            // of a log entirely.
            yield return stem + "-p" + pid.ToString(CultureInfo.InvariantCulture) + ".jsonl";
            for (var i = 2; i <= 50; i++)
                yield return stem + "-p" + pid.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture) + ".jsonl";
        }

        /// <summary>
        /// File-level metadata, once per file: what the format is and which process wrote it.
        /// Session-level facts (release, environment, trace) belong to
        /// <see cref="FileTelemetrySession"/>'s <c>session.start</c>, since a day-file holds many.
        /// </summary>
        private void WriteFileHeader(int pid)
        {
            _writer.TryWrite("header", "process", json =>
            {
                json.WriteNumber("v", SchemaVersion);
                json.WriteString("file_schema", "tiro-formfiller-telemetry-jsonl");
                json.WriteString("host", Environment.MachineName);
                json.WriteNumber("pid", pid);
            });
        }

        private void MaybeSweep()
        {
            var now = UtcNowProvider();
            if (_lastSweepUtc != default(DateTime) && now - _lastSweepUtc < SweepInterval) return;
            _lastSweepUtc = now;

            Sweep(_directory, _retentionDays, _maxTotalBytes, _writer?.FilePath);
        }

        /// <summary>
        /// Deletes transcripts past <paramref name="retentionDays"/>, then the oldest remaining
        /// until the directory fits <paramref name="maxTotalBytes"/>. Best-effort and silent: a
        /// sweep that cannot run is not a reason to fail a clinician's form. The file currently
        /// open is never a candidate.
        /// </summary>
        internal static void Sweep(string directory, int retentionDays, long maxTotalBytes, string keep = null)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                var cutoff = UtcNowProvider().AddDays(-retentionDays);
                var files = new DirectoryInfo(directory)
                    .GetFiles("*.jsonl")
                    .Where(f => keep == null || !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                var total = 0L;
                foreach (var file in files) total += file.Length;

                // Newest first, so the walk deletes from the old end in both passes.
                for (var i = files.Count - 1; i >= 0; i--)
                {
                    var file = files[i];
                    var tooOld = file.LastWriteTimeUtc < cutoff;
                    var overBudget = maxTotalBytes > 0 && total > maxTotalBytes;
                    if (!tooOld && !overBudget) continue;

                    var size = file.Length;
                    try
                    {
                        file.Delete();
                        total -= size;
                    }
                    catch
                    {
                        // In use, or not ours to delete.
                    }
                }
            }
            catch
            {
                // Best-effort by contract.
            }
        }

        private static string Normalize(string directory)
        {
            try { return Path.GetFullPath(directory); }
            catch { return directory; }
        }

        /// <summary>Test hook: forgets the shared instances so a test starts from a clean process.</summary>
        internal static void ResetForTests()
        {
            lock (SharedGate)
            {
                foreach (var log in Shared.Values) log.Dispose();
                Shared.Clear();
            }

            UtcNowProvider = () => DateTime.UtcNow;
        }
    }
}
