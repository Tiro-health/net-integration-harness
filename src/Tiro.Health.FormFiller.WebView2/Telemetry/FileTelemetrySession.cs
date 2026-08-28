using System;
using System.Collections.Generic;
using System.Reflection;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// One session's transcript. Records each call, then forwards it to the inner session.
    /// The two single-valued members — <see cref="GetSentryTraceHeader"/> and
    /// <see cref="GetEmbeddedBootstrapConfig"/> — pass straight through, which is the reason the
    /// decorator shape needs no precedence rule where a composite would have.
    /// </summary>
    internal sealed class FileTelemetrySession : ITelemetrySession
    {
        private readonly RollingTelemetryLog _log;
        private readonly string _sessionId;
        private readonly string _sid;
        private readonly ITelemetrySession _inner;
        private readonly Action _onEnded;
        private readonly object _gate = new object();

        private bool _ended;

        public FileTelemetrySession(RollingTelemetryLog log, string sessionId, ITelemetrySession inner, Action onEnded = null)
        {
            _log = log;
            _sessionId = sessionId;
            _sid = FileTelemetrySink.ShortId(sessionId);
            _inner = inner ?? NullTelemetrySink.NoopSession;
            _onEnded = onEnded;

            WriteSessionStart();
        }

        public void SetTag(string key, string value)
        {
            _log.Write("tag", _sid, json =>
            {
                TelemetryRecordWriter.WriteKey(json, "k", key);
                TelemetryRecordWriter.WriteValue(json, "v", value);
            });

            Guard(() => _inner.SetTag(key, value), "Session.SetTag");
        }

        public void AddBreadcrumb(string category, string message)
        {
            _log.Write("crumb", _sid, json =>
            {
                TelemetryRecordWriter.WriteKey(json, "cat", category);
                TelemetryRecordWriter.WriteValue(json, "msg", message);
            });

            Guard(() => _inner.AddBreadcrumb(category, message), "Session.AddBreadcrumb");
        }

        public ITelemetrySpan StartTransaction(string name, string operation)
        {
            var spanId = FileTelemetrySpan.NewSpanId();
            var innerSpan = Guard(
                () => _inner.StartTransaction(name, operation),
                "Session.StartTransaction",
                NullTelemetrySink.NoopSpan);

            return FileTelemetrySpan.Start(_log, _sid, spanId, parentSpanId: null, name: name, operation: operation, inner: innerSpan);
        }

        /// <inheritdoc />
        public string GetSentryTraceHeader()
            => Guard(() => _inner.GetSentryTraceHeader(), "Session.GetSentryTraceHeader", null);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig()
            => Guard(() => _inner.GetEmbeddedBootstrapConfig(), "Session.GetEmbeddedBootstrapConfig", null);

        /// <summary>
        /// Writes a <c>session.end</c> terminator, then disposes the inner session. The terminator
        /// is what tells a reader the session ended rather than being cut short by a process that
        /// died — the distinction the transcript exists to make. Idempotent: a second dispose must
        /// not write a second terminator, and <c>TiroFormViewer.Dispose</c> has no re-entry guard of
        /// its own. The file handle itself belongs to <see cref="RollingTelemetryLog"/>, shared
        /// across viewers and closed when the last sink releases it.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_ended) return;
                _ended = true;
            }

            _log.Write("session.end", _sid, null);

            // Tells the sink to stop attributing out-of-band captures to a session that has ended —
            // otherwise an error record lands after its own session.end, which a reader is entitled
            // to treat as a terminator.
            _onEnded?.Invoke();

            Guard(() => _inner.Dispose(), "Session.Dispose");
        }

        /// <summary>
        /// Opens the session in the transcript: its full id, release, environment, and the trace id
        /// the inner sink is reporting under. Schema version, host and pid belong to the file and
        /// are written once by <see cref="RollingTelemetryLog"/>, since a day-file holds many
        /// sessions.
        /// </summary>
        private void WriteSessionStart()
        {
            // Release and environment come from the inner sink's own bootstrap config, so the file
            // learns them without a new interface member. The config also carries a DSN — read the
            // two fields by name rather than looping, because a DSN is a credential and has no
            // business in a file built to be emailed.
            var config = GetEmbeddedBootstrapConfig();
            var environment = TryGet(config, "environment");
            var release = TryGet(config, "release");

            // The trace id, not the full sentry-trace header: it is what you paste into Sentry's
            // search, and it makes the file and the Sentry trace the same trace rather than two
            // things correlated by form.session.id after the fact.
            var trace = TraceIdOf(GetSentryTraceHeader());

            _log.Write("session.start", _sid, json =>
            {
                // The full form.session.id, written once per session. Every line carries the short
                // form, which is what a grep for one session out of a day's file matches.
                TelemetryRecordWriter.WriteKey(json, "session", _sessionId);
                TelemetryRecordWriter.WriteKey(json, "release", release ?? LocalRelease());
                if (environment != null) TelemetryRecordWriter.WriteKey(json, "env", environment);
                if (trace != null) TelemetryRecordWriter.WriteKey(json, "trace", trace);
            });
        }

        private static string TryGet(IReadOnlyDictionary<string, string> config, string key)
        {
            if (config == null) return null;
            string value;
            return config.TryGetValue(key, out value) && !string.IsNullOrEmpty(value) ? value : null;
        }

        /// <summary>
        /// Leading segment of a <c>sentry-trace</c> header (<c>traceId-spanId-sampled</c>), or null
        /// when there is no inner trace to name — the file-only case.
        /// </summary>
        internal static string TraceIdOf(string sentryTraceHeader)
        {
            if (string.IsNullOrEmpty(sentryTraceHeader)) return null;
            var dash = sentryTraceHeader.IndexOf('-');
            return dash > 0 ? sentryTraceHeader.Substring(0, dash) : sentryTraceHeader;
        }

        /// <summary>
        /// Release for the file-only case, where no inner sink supplies one. Same shape and same
        /// source as the Sentry adapter's default, so a transcript names the build the way a Sentry
        /// event would.
        /// </summary>
        private static string LocalRelease()
        {
            var assembly = typeof(ITelemetrySink).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString()
                       ?? "0.0.0";
            return "Tiro.Health.FormFiller.WebView2@" + version;
        }

        private void Guard(Action call, string member)
        {
            try { call(); }
            catch (Exception ex) { _log.WriteInnerError(_sid, member, ex); }
        }

        private T Guard<T>(Func<T> call, string member, T fallback)
        {
            try { return call(); }
            catch (Exception ex)
            {
                _log.WriteInnerError(_sid, member, ex);
                return fallback;
            }
        }
    }
}
