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
        private readonly TelemetryRecordWriter _writer;
        private readonly string _sessionId;
        private readonly ITelemetrySession _inner;

        public FileTelemetrySession(TelemetryRecordWriter writer, string sessionId, ITelemetrySession inner)
        {
            _writer = writer;
            _sessionId = sessionId;
            _inner = inner ?? NullTelemetrySink.NoopSession;

            WriteHeader();
        }

        public void SetTag(string key, string value)
        {
            _writer.Write("tag", json =>
            {
                json.WriteString("k", key);
                TelemetryRecordWriter.WriteValue(json, "v", value);
            });

            Guard(() => _inner.SetTag(key, value), "Session.SetTag");
        }

        public void AddBreadcrumb(string category, string message)
        {
            _writer.Write("crumb", json =>
            {
                json.WriteString("cat", category);
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

            return FileTelemetrySpan.Start(_writer, spanId, parentSpanId: null, name: name, operation: operation, inner: innerSpan);
        }

        /// <inheritdoc />
        public string GetSentryTraceHeader()
            => Guard(() => _inner.GetSentryTraceHeader(), "Session.GetSentryTraceHeader", null);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig()
            => Guard(() => _inner.GetEmbeddedBootstrapConfig(), "Session.GetEmbeddedBootstrapConfig", null);

        /// <summary>
        /// Writes a <c>session.end</c> terminator, then disposes the inner session. The terminator
        /// is what tells a reader the file is complete rather than cut short by a process that
        /// died — the distinction the transcript exists to make. The file handle itself belongs to
        /// <see cref="FileTelemetrySink"/>, which the viewer disposes after flushing.
        /// </summary>
        public void Dispose()
        {
            _writer.Write("session.end", null);
            Guard(() => _inner.Dispose(), "Session.Dispose");
        }

        /// <summary>
        /// The first record: schema version, release, environment, machine, and the trace id the
        /// inner sink is reporting under.
        /// </summary>
        private void WriteHeader()
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

            _writer.Write("header", json =>
            {
                json.WriteNumber("v", 1);
                // The full form.session.id, written once. Every other line carries the short form.
                json.WriteString("session", _sessionId);
                json.WriteString("release", release ?? LocalRelease());
                if (environment != null) json.WriteString("env", environment);
                if (trace != null) json.WriteString("trace", trace);
                json.WriteString("host", Environment.MachineName);
                json.WriteString("file_schema", "tiro-formfiller-telemetry-jsonl");
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
            catch (Exception ex) { _writer.WriteInnerError(member, ex); }
        }

        private T Guard<T>(Func<T> call, string member, T fallback)
        {
            try { return call(); }
            catch (Exception ex)
            {
                _writer.WriteInnerError(member, ex);
                return fallback;
            }
        }
    }
}
