using System;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Pluggable telemetry surface for session-scoped traces and exception capture. Default
    /// implementation is <see cref="NullTelemetrySink"/> (no-op); the
    /// <c>Tiro.Health.FormFiller.WebView2.Sentry</c> package ships a Sentry-backed adapter.
    /// </summary>
    public interface ITelemetrySink : IDisposable
    {
        /// <summary>
        /// Begin a telemetry session — a logical group of transactions sharing a trace id
        /// and a correlation tag. A consumer typically opens one session per logical unit of
        /// work (e.g. a form-viewer lifetime).
        /// </summary>
        ITelemetrySession BeginSession(string sessionId);

        /// <summary>Capture an exception out-of-band of any active span.</summary>
        void CaptureException(Exception ex);

        /// <summary>
        /// Capture a warning-level message out-of-band of any active span, for a condition that
        /// is not an exception and must not be reported as one.
        /// </summary>
        /// <remarks>
        /// Exists because a breadcrumb is not a report: breadcrumbs travel only when some later
        /// event in the session is captured, so a deployment where the only thing wrong is a
        /// silently-disarmed check sends nothing at all. That is precisely the condition worth
        /// hearing about (GH-62's SDC server version check fails open, loudly, when it cannot
        /// establish a version).
        /// </remarks>
        void CaptureMessage(string message);

        /// <summary>Block briefly to flush pending telemetry. Best-effort.</summary>
        void Flush(TimeSpan timeout);
    }
}
