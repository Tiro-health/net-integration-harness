using System;
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
        private readonly object _gate = new object();
        private bool _finished;

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
        /// envelope is serialized lazily at flush — so without a guard here a later
        /// <c>Finish(Ok)</c> overwrote an earlier failure and the trace shipped green.
        /// <para>
        /// The critical section is a test-and-set, so an interlocked flag would do; a lock
        /// keeps all three finish paths reading the same way, and a span finishes once, so
        /// there is no contention to optimise.
        /// </para>
        /// </summary>
        public void Finish(TelemetrySpanStatus status)
        {
            lock (_gate)
            {
                // _span.IsFinished as well as our own flag: the SDK can finish a span behind
                // the wrapper (an idle-timeout transaction), and writing a status through to
                // one that has already gone is the same overwrite from the other direction.
                if (_finished || _span.IsFinished) return;
                _finished = true;
                _span.Finish(Map(status));
            }
        }

        /// <summary>
        /// As <see cref="Finish(TelemetrySpanStatus)"/>, except that a repeat call still does
        /// something: it binds the exception, which is what links the captured error event to
        /// this span in a trace view. Losing that link loses the connection between a failure
        /// and where it happened.
        /// <para>
        /// <c>SentrySdk.BindException</c> rather than <c>ISpan.Finish(ex, status)</c>: the
        /// latter binds and assigns a status in one operation, so honouring first-wins with it
        /// meant remembering the winning status and passing it back to be re-asserted — which
        /// left this adapter unable to keep its own promise for a span the SDK had finished
        /// behind us, and forced the winning status to be recorded atomically with the finish
        /// (<see cref="SpanStatus.Ok"/> is the enum's zero value, so a torn read re-asserted
        /// green). Binding on its own touches neither status nor end timestamp, so none of
        /// that is needed: every repeat is linkage only.
        /// </para>
        /// <para>
        /// The static façade is the right hub here because these spans are created through
        /// <c>SentrySdk.StartTransaction</c> (see <c>SentryTelemetrySession</c>), so the span's
        /// hub IS the global hub. With the SDK uninitialised it is a no-op rather than a throw.
        /// </para>
        /// </summary>
        public void Finish(Exception ex)
        {
            lock (_gate)
            {
                if (_finished || _span.IsFinished)
                {
                    SentrySdk.BindException(ex, _span);
                    return;
                }

                _finished = true;
                _span.Finish(ex);
            }
        }

        /// <summary>
        /// Scope-exit finish: completes the span with <see cref="SpanStatus.Ok"/> only if it
        /// hasn't already been finished, so an explicit <c>Finish</c> on a failure path wins.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_finished || _span.IsFinished) return;
                _finished = true;
                _span.Finish(SpanStatus.Ok);
            }
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
