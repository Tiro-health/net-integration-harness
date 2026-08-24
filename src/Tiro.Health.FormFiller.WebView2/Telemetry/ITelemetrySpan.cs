using System;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// A telemetry span. Used for both transactions and child spans — they share the same
    /// surface in current usage. Implementations must be safe to call after
    /// <see cref="Finish(TelemetrySpanStatus)"/>, and the <b>first finish wins</b>: a
    /// subsequent call must not change the status or end time the first one recorded, nor
    /// emit a second event. "No-op" is not enough — an implementation that merely skips
    /// re-emitting while still mutating state the backend serializes later silently rewrites
    /// the outcome.
    /// <para>
    /// One carve-out: a repeat <see cref="Finish(Exception)"/> may <i>associate</i> its
    /// exception with the span, because that association is what links a captured error to
    /// the span in a trace view, and dropping it loses the connection between the failure and
    /// where it happened. It must still leave the recorded status and end time alone.
    /// </para>
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
