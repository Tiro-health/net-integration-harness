using System;
using global::Sentry;
using Tiro.Health.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// Adapts a Sentry <see cref="ISpan"/> (or <see cref="ITransactionTracer"/>, which derives
    /// from <c>ISpan</c>) to <see cref="ITelemetrySpan"/>.
    /// </summary>
    internal sealed class SentryTelemetrySpan : ITelemetrySpan
    {
        private readonly ISpan _span;

        public SentryTelemetrySpan(ISpan span)
        {
            _span = span ?? throw new ArgumentNullException(nameof(span));
        }

        public void SetTag(string key, string value) => _span.SetTag(key, value);

        public void SetExtra(string key, object value) => _span.SetExtra(key, value);

        public ITelemetrySpan StartChild(string operation, string description)
            => new SentryTelemetrySpan(_span.StartChild(operation, description));

        public void Finish(TelemetrySpanStatus status) => _span.Finish(Map(status));

        public void Finish(Exception ex) => _span.Finish(ex);

        /// <summary>
        /// Scope-exit finish: completes the span with <see cref="SpanStatus.Ok"/> only if it
        /// hasn't already been finished, so an explicit <c>Finish</c> on a failure path wins.
        /// </summary>
        public void Dispose()
        {
            if (!_span.IsFinished) _span.Finish(SpanStatus.Ok);
        }

        private static SpanStatus Map(TelemetrySpanStatus status)
        {
            switch (status)
            {
                case TelemetrySpanStatus.Ok: return SpanStatus.Ok;
                case TelemetrySpanStatus.InvalidArgument: return SpanStatus.InvalidArgument;
                case TelemetrySpanStatus.Cancelled: return SpanStatus.Cancelled;
                case TelemetrySpanStatus.DeadlineExceeded: return SpanStatus.DeadlineExceeded;
                case TelemetrySpanStatus.InternalError: return SpanStatus.InternalError;
                default: return SpanStatus.UnknownError;
            }
        }
    }
}
