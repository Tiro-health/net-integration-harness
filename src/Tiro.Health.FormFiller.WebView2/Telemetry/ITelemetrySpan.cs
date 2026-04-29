using System;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// A telemetry span. Used for both transactions and child spans — they share the same
    /// surface in current usage. Implementations must be safe to call after <see cref="Finish(TelemetrySpanStatus)"/>
    /// (subsequent calls are no-ops).
    /// </summary>
    public interface ITelemetrySpan
    {
        void SetTag(string key, string value);
        void SetExtra(string key, object value);
        ITelemetrySpan StartChild(string operation, string description);
        void Finish(TelemetrySpanStatus status);
        void Finish(Exception ex);
    }
}
