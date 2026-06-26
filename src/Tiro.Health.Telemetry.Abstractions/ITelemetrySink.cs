using System;

namespace Tiro.Health.Telemetry
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

        /// <summary>Block briefly to flush pending telemetry. Best-effort.</summary>
        void Flush(TimeSpan timeout);
    }
}
