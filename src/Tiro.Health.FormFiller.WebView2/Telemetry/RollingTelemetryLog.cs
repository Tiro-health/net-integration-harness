using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        /// <summary>At most one sweep per hour per log, whoever asks.</summary>
        private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// How long to stop trying after a failed open. Without it, an unwritable directory costs
        /// every record a walk over every candidate name, each one a thrown-and-caught exception,
        /// on whatever thread the viewer reports from — which is the UI thread for anything driven
        /// by a browser message. Telemetry that cannot write must cost nothing, not more than
        /// telemetry that can.
        /// </summary>
        private static readonly TimeSpan OpenRetryInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Names this log writes: <c>yyyyMMdd</c>, optionally a roll index, optionally this
        /// process's own series. <b>The sweep deletes only names matching this</b>, because the
        /// directory is a caller-supplied path that may hold files belonging to someone else — a
        /// site told to keep logs off <c>%LOCALAPPDATA%</c> can easily point this at a folder that
        /// already has its own <c>.jsonl</c> exports in it, and deleting those would be
        /// unrecoverable data loss caused by a telemetry component.
        /// </summary>
        private static readonly Regex OwnFileName =
            new Regex(@"^\d{8}(-\d{1,3}|-p\d+(-\d{1,3})?)?\.jsonl$", RegexOptions.CultureInvariant);

        /// <summary>Read once: <c>Process.GetCurrentProcess()</c> allocates a handle that needs disposing.</summary>
        private static readonly int ProcessId = ReadProcessId();

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
        private DateTime _openFailedUtc;
        private int _refCount;
        private int _innerErrorsLogged;
        private bool _closed;

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
        /// Drops one reference; the last one out closes the file for good. Not
        /// <see cref="Dispose"/>'s job alone, because several sinks share this and the first viewer
        /// to close must not take the transcript from the ones still open.
        /// </summary>
        /// <remarks>
        /// Closing happens <b>inside</b> the shared lock, and sets <see cref="_closed"/> rather
        /// than merely nulling the writer. Both matter. Releasing outside the lock left a window
        /// where an <see cref="Acquire"/> missed the de-registered instance and built a second one
        /// while the first still held the file handle, pinning the newcomer to a <c>-2</c> name for
        /// the rest of the day. And a merely-nulled writer is indistinguishable from "not opened
        /// yet", so a late write — a span finished by a cancellation continuation that the WinForms
        /// pump runs after the viewer's Dispose has returned — would reopen the file on an instance
        /// that is no longer registered and can never be released again, leaking the handle and
        /// forking the transcript.
        /// </remarks>
        public void Release()
        {
            lock (SharedGate)
            {
                if (--_refCount > 0) return;
                Shared.Remove(_key);

                lock (_gate)
                {
                    _closed = true;
                    _writer?.Dispose();
                    _writer = null;
                }
            }
        }

        /// <summary>
        /// Writes a record, rolling first if the day has turned or the file is full. Silently does
        /// nothing once the log is closed: a sink outlives neither its file nor its right to write
        /// to one, and dropping a late record is the correct outcome — the alternative is
        /// resurrecting a file nobody owns.
        /// </summary>
        public void Write(string type, string sessionId, Action<Utf8JsonWriter> fields)
        {
            // Guarded at the choke point every record passes through. Everything below catches its
            // own failures, but the arithmetic and path handling in this method did not, and a
            // telemetry call that throws lands inside a viewer catch block — replacing the real
            // exception with one about logging.
            try
            {
                lock (_gate)
                {
                    if (_closed) return;

                    EnsureWriter();
                    if (_writer == null) return;

                    if (_writer.TryWrite(type, sessionId, fields)) return;

                    // Refused for size. Nothing was written, so rolling and retrying loses no
                    // record — which is the point: a full file must cost the oldest records, never
                    // this one.
                    _full.Add(_writer.FilePath);
                    Roll();
                    if (_writer != null) _writer.TryWrite(type, sessionId, fields);
                }
            }
            catch
            {
                // Best-effort by contract.
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
                if (_closed) return;
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

        /// <summary>Path currently being written, or null when nothing is open.</summary>
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
                // Back off after a failure rather than re-walking every candidate name per record.
                if (_openFailedUtc != default(DateTime) && UtcNowProvider() - _openFailedUtc < OpenRetryInterval) return;

                Open(today);
                MaybeSweep();
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
            Open(UtcNowProvider().Date);

            // After the open, not before: the sweep's whole point is to spare the file being
            // written, and it can only be told which one that is once there is one.
            MaybeSweep();
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

            foreach (var candidate in CandidateNames(stem))
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

                _writer = writer;
                _fileDateUtc = dateUtc;
                _openFailedUtc = default(DateTime);
                WriteFileHeader();
                return;
            }

            _openFailedUtc = UtcNowProvider();
        }

        private static IEnumerable<string> CandidateNames(string stem)
        {
            yield return stem + ".jsonl";
            for (var i = 2; i <= 50; i++) yield return stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".jsonl";

            // Still nowhere to write: every plain name is full or taken. Fall back to this
            // process's own series so two processes on one account cannot deadlock each other out
            // of a log entirely.
            var pid = ProcessId.ToString(CultureInfo.InvariantCulture);
            yield return stem + "-p" + pid + ".jsonl";
            for (var i = 2; i <= 50; i++) yield return stem + "-p" + pid + "-" + i.ToString(CultureInfo.InvariantCulture) + ".jsonl";
        }

        /// <summary>
        /// File-level metadata: what the format is, and which host and process wrote it. Written on
        /// every <i>open</i>, not only when the file is created, for two reasons — the README has
        /// support extract one session with a <c>findstr</c>, which would otherwise drop the only
        /// line carrying the schema version, and a package upgraded mid-day would append records in
        /// a new schema under a header claiming the old one. A repeat header is also the marker for
        /// where one process stopped writing and another started.
        /// <para>
        /// Session-level facts (release, environment, trace) belong to
        /// <see cref="FileTelemetrySession"/>'s <c>session.start</c>, since a day-file holds many.
        /// </para>
        /// </summary>
        private void WriteFileHeader()
        {
            _writer.TryWrite("header", "process", json =>
            {
                json.WriteNumber("v", SchemaVersion);
                json.WriteString("file_schema", "tiro-formfiller-telemetry-jsonl");
                // Recorded because support needs to know which workstation a transcript came from.
                // Note this is one field the file has that Sentry does not: the adapter leaves
                // SendDefaultPii off, so Sentry never receives a machine name.
                TelemetryRecordWriter.WriteValue(json, "host", Environment.MachineName);
                json.WriteNumber("pid", ProcessId);
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
        /// until the directory fits <paramref name="maxTotalBytes"/>. Only files this log could
        /// have written are candidates (see <see cref="OwnFileName"/>), and never
        /// <paramref name="keep"/>. Best-effort and silent: a sweep that cannot run is not a reason
        /// to fail a clinician's form.
        /// </summary>
        internal static void Sweep(string directory, int retentionDays, long maxTotalBytes, string keep = null)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                var cutoff = UtcNowProvider().AddDays(-retentionDays);
                var files = new DirectoryInfo(directory)
                    .GetFiles("*.jsonl")
                    .Where(f => OwnFileName.IsMatch(f.Name))
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

        private static int ReadProcessId()
        {
            try
            {
                using (var process = Process.GetCurrentProcess()) return process.Id;
            }
            catch
            {
                return 0;
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
                foreach (var log in Shared.Values)
                {
                    // Refcounts too, not just the dictionary: a sink surviving the reset would
                    // otherwise Release from 1 to 0 later and evict a newer instance created after
                    // it, silently un-sharing the log for everyone else in the next test.
                    lock (log._gate)
                    {
                        log._closed = true;
                        log._writer?.Dispose();
                        log._writer = null;
                    }
                    log._refCount = 0;
                }

                Shared.Clear();
            }

            UtcNowProvider = () => DateTime.UtcNow;
        }
    }
}
