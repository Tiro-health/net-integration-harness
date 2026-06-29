namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Outcome of a finished span. Mirrors the Sentry <c>SpanStatus</c> values that
    /// consumers actually emit.
    /// </summary>
    public enum TelemetrySpanStatus
    {
        Ok,
        InvalidArgument,
        Cancelled,
        DeadlineExceeded,
        InternalError
    }
}
