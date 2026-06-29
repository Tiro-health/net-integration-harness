using System;
using System.Collections.Generic;
using global::Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// One Sentry trace shared across all transactions started within a single
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}"/> lifetime. Tags applied via
    /// <see cref="SetTag"/> are stamped on every transaction the session starts.
    /// </summary>
    internal sealed class SentryTelemetrySession : ITelemetrySession
    {
        private readonly SentryId _traceId;
        private readonly SpanId _rootSpanId;
        private readonly Dictionary<string, string> _tags = new Dictionary<string, string>();
        private readonly string _embeddedDsn;
        private readonly string _environment;
        private readonly string _release;

        /// <summary>
        /// <paramref name="embeddedDsn"/> is the DSN given to the embedded browser page (a
        /// different Sentry project than the host's), not the host's own DSN. The host's
        /// DSN was already consumed by <c>SentryTelemetrySink</c> for <c>SentrySdk.Init</c>.
        /// </summary>
        public SentryTelemetrySession(string sessionId, string embeddedDsn, string environment, string release)
        {
            _traceId = SentryId.Create();
            _rootSpanId = SpanId.Create();
            _tags["form.session.id"] = sessionId;
            _embeddedDsn = embeddedDsn;
            _environment = environment;
            _release = release;
        }

        public void SetTag(string key, string value)
        {
            _tags[key] = value;
        }

        public void AddBreadcrumb(string category, string message)
        {
            // Sentry's breadcrumbs are scope-attached; with IsGlobalModeEnabled (the SDK
            // mode the SentryTelemetrySink ctor turns on) they live on the global hub and
            // attach to whatever transaction/event is captured next.
            SentrySdk.AddBreadcrumb(message, category: category);
        }

        public ITelemetrySpan StartTransaction(string name, string operation)
        {
            // Continuing the same trace across every transaction in this session: pass a
            // SentryTraceHeader with our shared traceId + a synthetic parent spanId so
            // Sentry's trace view groups them under one timeline.
            var traceHeader = new SentryTraceHeader(_traceId, _rootSpanId, isSampled: true);
            var transaction = SentrySdk.StartTransaction(name, operation, traceHeader);
            foreach (var kv in _tags)
                transaction.SetTag(kv.Key, kv.Value);
            return new SentryTelemetrySpan(transaction);
        }

        public string GetSentryTraceHeader()
        {
            // The synthetic root span is what we expose to remote receivers as the parent
            // span; receivers (the JS Sentry SDK in the embedded page) will create their own
            // spans under this parent within our shared trace.
            return new SentryTraceHeader(_traceId, _rootSpanId, isSampled: true).ToString();
        }

        public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig()
        {
            // The host owns both DSNs: it uses its own DSN for SentrySdk.Init in this
            // process (reporting to e.g. tirohealth/dotnet-winforms), and it injects the
            // embedded-browser DSN (e.g. tirohealth/javascript) so the page's Sentry SDK
            // reports there. Same Sentry org → unified trace view across both projects.
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_embeddedDsn)) dict["dsn"] = _embeddedDsn;
            if (!string.IsNullOrEmpty(_environment)) dict["environment"] = _environment;
            if (!string.IsNullOrEmpty(_release)) dict["release"] = _release;
            dict["sentryTrace"] = GetSentryTraceHeader();
            return dict;
        }

        public void Dispose() { /* nothing to release; trace id is just an identifier */ }
    }
}
