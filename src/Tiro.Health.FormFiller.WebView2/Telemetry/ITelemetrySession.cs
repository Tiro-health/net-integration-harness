using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// A telemetry session — a logical grouping of related transactions and breadcrumbs that
    /// share a common trace and a stable correlation tag (e.g. one <see cref="TiroFormViewer{TResource,TQR,TOO}"/>
    /// lifetime). All transactions started via <see cref="StartTransaction"/> within this
    /// session share the same trace id, so Sentry's trace view renders them together.
    /// </summary>
    public interface ITelemetrySession : IDisposable
    {
        /// <summary>Apply a tag to every transaction started in this session.</summary>
        void SetTag(string key, string value);

        /// <summary>
        /// Record a breadcrumb (a lightweight log entry attached to errors/transactions
        /// captured in scope). Use for lifecycle events (init, handshake, dispose).
        /// </summary>
        void AddBreadcrumb(string category, string message);

        /// <summary>Start a new transaction in this session's trace.</summary>
        ITelemetrySpan StartTransaction(string name, string operation);

        /// <summary>
        /// Returns a <c>sentry-trace</c> header value (<c>"&lt;traceId&gt;-&lt;spanId&gt;-&lt;sampled&gt;"</c>)
        /// for the current session, or <c>null</c> when telemetry is disabled / the session
        /// is no-op. Used by <see cref="TiroFormViewer{TResource,TQR,TOO}"/> to propagate the
        /// trace into outbound SMART Web Messaging envelopes via <c>_meta.sentry.trace</c>.
        /// </summary>
        string GetSentryTraceHeader();

        /// <summary>
        /// Returns a JSON-serializable key/value config for an embedded browser to bootstrap
        /// its own telemetry SDK (DSN, environment, release) and inherit the current trace
        /// (sentryTrace, baggage). The host injects this as <c>window.__tiroSentryConfig</c>
        /// before page scripts run, so the embedded <c>index.html</c> doesn't have to know
        /// any DSN or trace context. Returns null when telemetry is disabled.
        /// </summary>
        IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig();
    }
}
