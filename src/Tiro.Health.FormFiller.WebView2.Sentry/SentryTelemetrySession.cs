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

        public SentryTelemetrySession(string sessionId)
        {
            _traceId = SentryId.Create();
            _rootSpanId = SpanId.Create();
            _tags["form.session.id"] = sessionId;
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

        public void Dispose() { /* nothing to release; trace id is just an identifier */ }
    }
}
