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
    /// contain: that Sentry assigns a span's status BEFORE its own already-finished guard, so a
    /// later Finish rewrites an outcome that has not been serialized yet. These tests fail on
    /// the unguarded adapter, and will fail again if a Sentry upgrade changes that behaviour.
    /// <para>
    /// A DisabledHub tracer is a real ISpan that touches no network.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestSentryTelemetrySpan
    {
        private static ISpan NewSpan()
            => new TransactionTracer(DisabledHub.Instance, new TransactionContext("test", "test.op")).StartChild("child");

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
