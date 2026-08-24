using System;
using System.Threading;
using global::Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// Adapts a Sentry <see cref="ISpan"/> (or <see cref="ITransactionTracer"/>, which derives
    /// from <c>ISpan</c>) to <see cref="ITelemetrySpan"/>.
    /// </summary>
    internal sealed class SentryTelemetrySpan : ITelemetrySpan
    {
        private readonly ISpan _span;
        private int _finished;

        public SentryTelemetrySpan(ISpan span)
        {
            _span = span ?? throw new ArgumentNullException(nameof(span));
        }

        public void SetTag(string key, string value) => _span.SetTag(key, value);

        public void SetExtra(string key, object value) => _span.SetExtra(key, value);

        public ITelemetrySpan StartChild(string operation, string description)
            => new SentryTelemetrySpan(_span.StartChild(operation, description));

        /// <summary>
        /// First finish wins, per the <see cref="ITelemetrySpan"/> contract. Sentry's
        /// <c>Finish(status)</c> assigns the status BEFORE its own already-finished guard, and
        /// the captured transaction shares the tracer's trace context by reference while the
        /// envelope is serialized lazily at flush — so without this a later <c>Finish(Ok)</c>
        /// overwrote an earlier failure status and the trace shipped green.
        /// <para>
        /// Interlocked rather than <c>IsFinished</c>: that is a non-atomic check-then-act over
        /// a plain property, and these calls genuinely race (a round trip's cancellation
        /// callback against its response handler). One wrapper owns exactly one span, so the
        /// flag is a complete record of finishes through this adapter.
        /// </para>
        /// </summary>
        public void Finish(TelemetrySpanStatus status)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _span.Finish(Map(status));
        }

        /// <summary>
        /// As <see cref="Finish(TelemetrySpanStatus)"/>, but a repeat call still reaches
        /// Sentry: <c>Finish(Exception, status)</c> binds the exception to this span before its
        /// own guard, and that binding is what links the captured error event to the span in
        /// the trace view. Re-asserting the status it already has keeps the linkage without
        /// rewriting an outcome that may already have shipped.
        /// </summary>
        public void Finish(Exception ex)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                _span.Finish(ex, _span.Status ?? SpanStatus.UnknownError);
                return;
            }
            _span.Finish(ex);
        }

        /// <summary>
        /// Scope-exit finish: completes the span with <see cref="SpanStatus.Ok"/> only if it
        /// hasn't already been finished, so an explicit <c>Finish</c> on a failure path wins.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _span.Finish(SpanStatus.Ok);
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
