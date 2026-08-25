using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;
// global::, because the enclosing namespace chain also contains a "Sentry".
using global::Sentry;
using global::Sentry.Extensibility;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The adapter against the real SDK. Every other telemetry test asserts through
    /// FakeTelemetrySink, which by construction cannot see the behaviour the adapter exists to
    /// contain: Sentry assigns a span's status BEFORE its own already-finished guard, and the
    /// captured transaction shares the tracer's trace context by reference, so a later Finish
    /// rewrites an outcome whose envelope is already queued.
    /// <para>
    /// Two kinds of test, worth not confusing. Regression tests, which fail on the unguarded
    /// adapter: the first three. And pins on SDK behaviour the adapter relies on but does not
    /// implement, which passed before the guard existed and are here to fail if a Sentry
    /// upgrade moves the ground: the last two.
    /// </para>
    /// <para>A DisabledHub tracer is a real ISpan that touches no network.</para>
    /// </summary>
    [TestClass]
    public class TestSentryTelemetrySpan
    {
        private static ITransactionTracer NewTransaction()
            => new TransactionTracer(DisabledHub.Instance, new TransactionContext("test", "test.op"));

        private static ISpan NewSpan() => NewTransaction().StartChild("child");

        [TestMethod]
        public void ASecondFinishDoesNotRewriteTheOutcome()
        {
            var span = NewSpan();
            var wrapped = new SentryTelemetrySpan(span);

            wrapped.Finish(TelemetrySpanStatus.InvalidArgument);
            wrapped.Finish(TelemetrySpanStatus.Ok);

            Assert.AreEqual(SpanStatus.InvalidArgument, span.Status,
                "the second finish overwrote a failure with Ok — the bug this adapter guards against");
        }

        // The receive and send transactions the viewer actually creates are TransactionTracers,
        // not child spans, and the transaction is where the by-reference trace context makes a
        // post-capture rewrite reach an already-queued envelope. Asserted separately rather
        // than assumed to follow from the child-span case: they are different SDK types.
        [TestMethod]
        public void ASecondFinishDoesNotRewriteATransactionOutcome()
        {
            var transaction = NewTransaction();
            var wrapped = new SentryTelemetrySpan(transaction);

            wrapped.Finish(TelemetrySpanStatus.InvalidArgument);
            wrapped.Finish(TelemetrySpanStatus.Ok);

            Assert.AreEqual(SpanStatus.InvalidArgument, transaction.Status);
        }

        [TestMethod]
        public void ARepeatExceptionFinishKeepsTheEarlierStatus()
        {
            var span = NewSpan();
            var wrapped = new SentryTelemetrySpan(span);

            wrapped.Finish(TelemetrySpanStatus.DeadlineExceeded);
            // Reaches Sentry deliberately — the exception binding is what links the captured
            // error to this span — but must re-assert the status rather than replace it.
            wrapped.Finish(new InvalidOperationException("late failure"));

            Assert.AreEqual(SpanStatus.DeadlineExceeded, span.Status);
        }

        [TestMethod]
        public void DisposeDoesNotRewriteAnExplicitFailure()
        {
            var span = NewSpan();
            using (var wrapped = new SentryTelemetrySpan(span))
            {
                wrapped.Finish(TelemetrySpanStatus.InternalError);
            }

            Assert.AreEqual(SpanStatus.InternalError, span.Status);
        }

        // A pin, not a regression: the adapter's IsFinished check exists for this, and the
        // check is only worth anything if the SDK really does leave an externally-finished
        // span's status alone.
        [TestMethod]
        public void AFinishFromOutsideTheWrapperIsNotOverwritten()
        {
            var span = NewSpan();
            var wrapped = new SentryTelemetrySpan(span);

            // What an idle-timeout transaction does: the SDK finishes the span and the wrapper
            // never hears about it, so its own flag is no guide.
            span.Finish(SpanStatus.Aborted);
            wrapped.Finish(TelemetrySpanStatus.Ok);
            wrapped.Dispose();

            Assert.AreEqual(SpanStatus.Aborted, span.Status);
        }

        // A pin: the repeat-exception carve-out re-asserts whatever status Sentry derived from
        // the exception on the first finish, rather than hard-coding one. That only works while
        // an exception finish does in fact produce a failure status.
        [TestMethod]
        public void AFirstFinishWithAnExceptionStillSetsAFailureStatus()
        {
            var span = NewSpan();
            var wrapped = new SentryTelemetrySpan(span);

            wrapped.Finish(new InvalidOperationException("boom"));

            Assert.IsNotNull(span.Status);
            Assert.AreNotEqual(SpanStatus.Ok, span.Status);
        }
    }
}
