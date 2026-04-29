using System;

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
    }
}
