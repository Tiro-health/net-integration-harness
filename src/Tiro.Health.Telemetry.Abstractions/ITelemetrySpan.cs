using System;

namespace Tiro.Health.Telemetry
{
    /// <summary>
    /// A telemetry span. Used for both transactions and child spans — they share the same
    /// surface in current usage. Implementations must be safe to call after
    /// <see cref="Finish(TelemetrySpanStatus)"/> (subsequent calls are no-ops).
    /// <para>
    /// Implements <see cref="IDisposable"/> so callers can scope a span with <c>using</c>:
    /// <see cref="IDisposable.Dispose"/> finishes the span with
    /// <see cref="TelemetrySpanStatus.Ok"/> if it has not already been finished, so an
    /// explicit <c>Finish</c> on a failure path still wins.
    /// </para>
    /// </summary>
    public interface ITelemetrySpan : IDisposable
    {
        void SetTag(string key, string value);
        void SetExtra(string key, object value);
        ITelemetrySpan StartChild(string operation, string description);
        void Finish(TelemetrySpanStatus status);
        void Finish(Exception ex);
    }
}
