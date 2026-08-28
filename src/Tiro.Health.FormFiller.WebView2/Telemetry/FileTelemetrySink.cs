using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Writes a JSONL transcript of everything the viewer reports, one file per session, and
    /// forwards every call to an inner <see cref="ITelemetrySink"/>. For deployments where the
    /// hospital network will not let Sentry out — and where a blocked DSN is indistinguishable
    /// from a healthy one from inside the process, because the Sentry transport drops failures
    /// silently — this is the copy that survives locally and can be attached to a support ticket
    /// without opening a network path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A decorator, not a second backend.</b> That is the whole design. The obvious shape is a
    /// composite fanning out to N peer sinks, but the two <see cref="ITelemetrySession"/> members
    /// that return a single value — <see cref="ITelemetrySession.GetSentryTraceHeader"/> and
    /// <see cref="ITelemetrySession.GetEmbeddedBootstrapConfig"/> — have no meaningful merge (you
    /// cannot hand the embedded page two DSNs, and two trace ids decorrelate host from page), and
    /// <see cref="ITelemetrySink.Flush"/> takes one budget that would have to be divided. As a
    /// decorator both simply pass through, and the file records the trace header it saw, so log
    /// and Sentry trace share a real trace id rather than only <c>form.session.id</c>. There is
    /// also one span tree instead of N kept in step, and no second implementation of
    /// <see cref="ITelemetrySpan"/>'s first-finish-wins rule to diverge from the adapter's.
    /// </para>
    /// <para>
    /// <b>The file records caller intent.</b> A repeat <c>Finish</c> that the inner span correctly
    /// ignores under first-finish-wins still appears here, flagged <c>"repeat":true</c>. That is
    /// the useful direction: it is how you would find out why a trace shipped green.
    /// </para>
    /// <para>
    /// <b>Forwarded calls cannot throw through.</b> Every inner call is guarded, so a backend that
    /// fails cannot take the local transcript with it — and its failure is recorded rather than
    /// swallowed. This also covers the viewer's unguarded <see cref="CaptureException"/> and
    /// <see cref="CaptureMessage"/> call sites.
    /// </para>
    /// <para>
    /// PHI: the point of the file is that it leaves the hospital, so it is held to the same rule
    /// as Sentry — <b>no FHIR payloads</b>. It writes only what callers pass to
    /// <see cref="ITelemetrySink"/>, never reflects over a <c>SetExtra</c> object graph, caps
    /// every value's length, and never writes a DSN.
    /// </para>
    /// </remarks>
    public sealed class FileTelemetrySink : ITelemetrySink
    {
        /// <summary>
        /// <c>%LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry</c> — per-user and outside Program
        /// Files, so it stays writable under the restricted accounts clinical workstations run as.
        /// </summary>
        public static string DefaultDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tiro.Health", "FormFiller", "telemetry");

        /// <summary>Files older than this are swept on first construction in a process.</summary>
        public const int RetentionDays = 14;

        /// <summary>Newest files kept by the sweep regardless of age.</summary>
        public const int RetentionFileCount = 200;

        /// <summary>
        /// Cap per file. Reaching it writes a <c>trunc</c> record and stops. Generous next to a
        /// session's expected few dozen records — it is a guard against a pathological loop, not
        /// a budget anybody should hit.
        /// </summary>
        public const long MaxBytesPerFile = 8L * 1024 * 1024;

        private static int _sweptThisProcess;

        private readonly string _directory;
        private readonly ITelemetrySink _inner;
        private readonly bool _ownsInner;
        private readonly List<TelemetryRecordWriter> _writers = new List<TelemetryRecordWriter>();
        private readonly object _gate = new object();

        private TelemetryRecordWriter _current;
        private bool _disposed;

        /// <summary>Writes to <see cref="DefaultDirectory"/> with no inner sink — file only.</summary>
        public FileTelemetrySink() : this(DefaultDirectory, NullTelemetrySink.Instance) { }

        /// <summary>Writes to <paramref name="directory"/> with no inner sink — file only.</summary>
        public FileTelemetrySink(string directory) : this(directory, NullTelemetrySink.Instance) { }

        /// <summary>Writes to <see cref="DefaultDirectory"/> and forwards to <paramref name="inner"/>.</summary>
        public FileTelemetrySink(ITelemetrySink inner) : this(DefaultDirectory, inner) { }

        /// <summary>
        /// Writes to <paramref name="directory"/> and forwards to <paramref name="inner"/>.
        /// </summary>
        /// <param name="directory">Destination for the JSONL files. Created if missing.</param>
        /// <param name="inner">
        /// Sink to forward to; <see cref="NullTelemetrySink.Instance"/> for file-only. Never null.
        /// </param>
        /// <param name="ownsInner">
        /// Whether <see cref="Dispose"/> disposes <paramref name="inner"/>. True suits the usual
        /// registration, where the factory builds a fresh inner per viewer and nothing else holds
        /// it. Pass false when one inner instance is shared across viewers, or the first viewer to
        /// close will dispose telemetry the others are still using.
        /// </param>
        public FileTelemetrySink(string directory, ITelemetrySink inner, bool ownsInner = true)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Directory must not be null or empty.", nameof(directory));
            _directory = directory;
            _inner = inner ?? NullTelemetrySink.Instance;
            _ownsInner = ownsInner;

            // Once per process, not once per viewer: the factory runs on every viewer construction
            // and a form can be opened dozens of times in a shift.
            if (Interlocked.Exchange(ref _sweptThisProcess, 1) == 0) Sweep(_directory);
        }

        /// <summary>
        /// Opens <c>&lt;timestamp&gt;-&lt;session&gt;.jsonl</c> and writes the header record.
        /// One file per session is deliberate: it is the artifact support asks for — one file, one
        /// ticket — and it sidesteps reference-counting a handle shared between viewers, which
        /// <see cref="ITelemetrySink"/>'s ownership rules give no way to do safely.
        /// </summary>
        public ITelemetrySession BeginSession(string sessionId)
        {
            var id = string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString() : sessionId;
            var innerSession = Guard(() => _inner.BeginSession(id), "BeginSession", NullTelemetrySink.NoopSession);
            var writer = OpenWriter(id);

            if (writer == null) return innerSession;
            return new FileTelemetrySession(writer, id, innerSession);
        }

        /// <inheritdoc />
        public void CaptureException(Exception ex)
        {
            // Sink-level capture is out-of-band of any session, but the viewer opens its session in
            // its constructor before anything can fail, so the current writer is the right file
            // essentially always. The fallback covers a sink used without a session at all.
            var writer = CurrentOrFallbackWriter();
            writer?.Write("error", json =>
            {
                json.WriteString("exc", ex?.GetType().FullName ?? "null");
                TelemetryRecordWriter.WriteValue(json, "msg", ex?.Message);
                TelemetryRecordWriter.WriteValue(json, "stack", ex?.StackTrace);
            });

            Guard(() => _inner.CaptureException(ex), "CaptureException");
        }

        /// <inheritdoc />
        public void CaptureMessage(string message)
        {
            var writer = CurrentOrFallbackWriter();
            writer?.Write("message", json => TelemetryRecordWriter.WriteValue(json, "msg", message));

            Guard(() => _inner.CaptureMessage(message), "CaptureMessage");
        }

        /// <summary>
        /// Flushes the file first, then hands the <b>remaining</b> budget to the inner sink.
        /// Ordering matters: the file costs microseconds, whereas Sentry's flush is a network
        /// round trip that burns its entire timeout when egress is blocked — precisely the
        /// deployment this sink exists for. Cheap-first is why a decorator needs no budget
        /// arithmetic, where a composite would have had to divide one timeout N ways.
        /// </summary>
        public void Flush(TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();

            lock (_gate)
            {
                foreach (var writer in _writers) writer.Flush();
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            Guard(() => _inner.Flush(remaining), "Flush");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var writer in _writers) writer.Dispose();
                _writers.Clear();
                _current = null;
            }

            if (_ownsInner) Guard(() => _inner.Dispose(), "Dispose");
        }

        private TelemetryRecordWriter OpenWriter(string sessionId)
        {
            var name = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyyMMdd'T'HHmmss'Z'}-{1}.jsonl",
                DateTime.UtcNow,
                ShortId(sessionId));

            var writer = TelemetryRecordWriter.TryOpen(Path.Combine(_directory, name), ShortId(sessionId), MaxBytesPerFile);
            if (writer == null) return null;

            lock (_gate)
            {
                if (_disposed) { writer.Dispose(); return null; }
                _writers.Add(writer);
                _current = writer;
            }

            return writer;
        }

        private TelemetryRecordWriter CurrentOrFallbackWriter()
        {
            lock (_gate)
            {
                if (_disposed) return null;
                if (_current != null) return _current;
            }

            return OpenWriter("no-session");
        }

        /// <summary>First 8 characters of the session id — enough to pair a file with a Sentry event.</summary>
        internal static string ShortId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "unknown";
            var cleaned = new string(sessionId.Where(c => char.IsLetterOrDigit(c)).ToArray());
            if (cleaned.Length == 0) return "unknown";
            return cleaned.Length <= 8 ? cleaned : cleaned.Substring(0, 8);
        }

        /// <summary>
        /// Deletes transcripts past <see cref="RetentionDays"/>, then trims to the newest
        /// <see cref="RetentionFileCount"/>. Best-effort and silent: a sweep that cannot run is
        /// not a reason to fail a clinician's form.
        /// </summary>
        internal static void Sweep(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                var files = new DirectoryInfo(directory)
                    .GetFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                for (var i = 0; i < files.Count; i++)
                {
                    if (i >= RetentionFileCount || files[i].LastWriteTimeUtc < cutoff)
                        try { files[i].Delete(); } catch { /* in use, or not ours to delete */ }
                }
            }
            catch
            {
                // Best-effort by contract.
            }
        }

        private void Guard(Action call, string member)
        {
            try { call(); }
            catch (Exception ex) { CurrentWriterForErrors()?.WriteInnerError(member, ex); }
        }

        private T Guard<T>(Func<T> call, string member, T fallback)
        {
            try { return call(); }
            catch (Exception ex)
            {
                CurrentWriterForErrors()?.WriteInnerError(member, ex);
                return fallback;
            }
        }

        private TelemetryRecordWriter CurrentWriterForErrors()
        {
            lock (_gate) { return _current; }
        }
    }
}
