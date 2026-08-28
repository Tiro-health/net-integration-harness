using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Writes a rolling JSONL transcript of everything the viewer reports to local disk, and
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
    /// The transcript rolls daily and is shared by every sink in the process pointing at the same
    /// directory — see <see cref="RollingTelemetryLog"/> for why one file per day beats one per
    /// session, and how retention is bounded.
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

        /// <summary>
        /// Days of transcripts kept. Sized to how long a support request actually takes to arrive
        /// — a clinician hits a problem on Friday, IT raises a ticket on Monday, someone asks for
        /// the file on Tuesday — not to disk pressure, of which there is none at a few dozen
        /// records per session.
        /// </summary>
        public const int RetentionDays = 7;

        /// <summary>
        /// Cap per file. Reaching it rolls to the next index rather than stopping, so a full file
        /// costs the oldest records and never the newest.
        /// </summary>
        public const long MaxBytesPerFile = 8L * 1024 * 1024;

        /// <summary>
        /// Budget for the whole directory. The second of two bounds that do not multiply: with a
        /// per-file cap and a file <i>count</i>, the product is the real ceiling and nobody reads
        /// it off the two constants.
        /// </summary>
        public const long MaxTotalBytes = 64L * 1024 * 1024;

        private readonly RollingTelemetryLog _log;
        private readonly ITelemetrySink _inner;
        private readonly bool _ownsInner;
        private readonly object _gate = new object();

        private string _currentSessionId = "process";
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
        /// <param name="directory">Destination for the transcripts. Created if missing.</param>
        /// <param name="inner">
        /// Sink to forward to; <see cref="NullTelemetrySink.Instance"/> for file-only. Never null.
        /// </param>
        /// <param name="ownsInner">
        /// Whether <see cref="Dispose"/> disposes <paramref name="inner"/>. True suits the usual
        /// registration, where the factory builds a fresh inner per viewer and nothing else holds
        /// it. Pass false when one inner instance is shared across viewers, or the first viewer to
        /// close will dispose telemetry the others are still using. (The transcript itself is
        /// shared and reference-counted regardless, so viewers never take the file from each other.)
        /// </param>
        public FileTelemetrySink(string directory, ITelemetrySink inner, bool ownsInner = true)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Directory must not be null or empty.", nameof(directory));
            _inner = inner ?? NullTelemetrySink.Instance;
            _ownsInner = ownsInner;
            _log = RollingTelemetryLog.Acquire(directory, MaxBytesPerFile, MaxTotalBytes, RetentionDays);
        }

        /// <summary>
        /// Begins a session in the shared transcript. Nothing is opened per session: sessions are
        /// delimited by their <c>session.start</c> / <c>session.end</c> records and identified by
        /// the <c>sid</c> on every line.
        /// </summary>
        public ITelemetrySession BeginSession(string sessionId)
        {
            var id = string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString() : sessionId;
            var innerSession = Guard(() => _inner.BeginSession(id), "BeginSession", NullTelemetrySink.NoopSession);

            lock (_gate)
            {
                if (_disposed) return innerSession;
                _currentSessionId = ShortId(id);
            }

            return new FileTelemetrySession(_log, id, innerSession);
        }

        /// <inheritdoc />
        public void CaptureException(Exception ex)
        {
            // Sink-level capture is out-of-band of any session. It is attributed to the session in
            // flight, since the viewer opens one in its constructor before anything can fail, and
            // falls back to "process" for a sink used without a session at all.
            _log.Write("error", CurrentSessionId(), json =>
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
            _log.Write("message", CurrentSessionId(), json => TelemetryRecordWriter.WriteValue(json, "msg", message));

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

            _log.Flush();

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
            }

            // Release, not Dispose: other viewers in this process may still be writing to the same
            // transcript, and the last one out closes it.
            _log.Release();

            if (_ownsInner) Guard(() => _inner.Dispose(), "Dispose");
        }

        /// <summary>
        /// First 8 alphanumeric characters of the session id. Short because it repeats on every
        /// line, and 36 characters of GUID per line is most of what makes a transcript unreadable;
        /// the full id is written once in <c>session.start</c>, which is what pairs the transcript
        /// with a Sentry event.
        /// </summary>
        internal static string ShortId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "unknown";
            var cleaned = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
            if (cleaned.Length == 0) return "unknown";
            return cleaned.Length <= 8 ? cleaned : cleaned.Substring(0, 8);
        }

        private string CurrentSessionId()
        {
            lock (_gate) { return _currentSessionId; }
        }

        private void Guard(Action call, string member)
        {
            try { call(); }
            catch (Exception ex) { _log.WriteInnerError(CurrentSessionId(), member, ex); }
        }

        private T Guard<T>(Func<T> call, string member, T fallback)
        {
            try { return call(); }
            catch (Exception ex)
            {
                _log.WriteInnerError(CurrentSessionId(), member, ex);
                return fallback;
            }
        }
    }
}
