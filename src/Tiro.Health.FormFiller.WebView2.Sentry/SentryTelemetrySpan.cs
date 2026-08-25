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
        private SpanStatus _finalStatus;

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
        /// A lock rather than an interlocked flag: claiming the finish and recording which
        /// status won have to happen together, because <see cref="Finish(Exception)"/> reads
        /// that status back, and <see cref="SpanStatus.Ok"/> is the enum's zero value — so a
        /// torn read re-asserts green, the same overwrite in miniature. A span finishes once;
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
                _finalStatus = Map(status);
                _span.Finish(_finalStatus);
            }
        }

        /// <summary>
        /// As <see cref="Finish(TelemetrySpanStatus)"/>, except that a repeat call still
        /// reaches Sentry: binding the exception is what links the captured error event to this
        /// span in a trace view. It re-asserts the status the winning finish recorded, so the
        /// linkage is added without rewriting an outcome that may already have shipped.
        /// </summary>
        public void Finish(Exception ex)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    _span.Finish(ex, _finalStatus);
                    return;
                }

                _finished = true;
                // No IsFinished check here: the binding has to happen even if the SDK finished
                // the span behind us, and there is no bind-only API. The cost is that such a
                // span's status is rewritten — the one case this adapter cannot honour.
                _span.Finish(ex);
                // Read back inside the lock, where no other finish can be in flight: whatever
                // status Sentry derived from the exception is what a later repeat must
                // re-assert, and hard-coding one here would silently diverge from it.
                _finalStatus = _span.Status ?? SpanStatus.UnknownError;
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
                _finalStatus = SpanStatus.Ok;
                _span.Finish(_finalStatus);
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
