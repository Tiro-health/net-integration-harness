using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using Tiro.Health.SmartWebMessaging;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// Locks in the telemetry surface introduced in the Sentry-decoupling /
    /// per-message-transactions arc: <see cref="TiroFormViewer{TResource,TQR,TOO}"/>
    /// opens a session at construction, drops a lifecycle breadcrumb,
    /// produces per-message transactions, sets <c>form.session.id</c> tag, etc.
    /// </summary>
    [TestClass]
    public class TestTelemetryContracts
    {
        [TestMethod]
        public void Construction_OpensExactlyOneSession_AndDropsConstructedBreadcrumb()
        {
            var sink = new FakeTelemetrySink();
            using var viewer = NewViewer(sink);

            Assert.AreEqual(1, sink.Sessions.Count);
            Assert.IsTrue(sink.Sessions[0].Breadcrumbs.Any(b =>
                b.Category == "lifecycle" && b.Message.Contains("constructed")),
                "Expected a 'lifecycle' breadcrumb noting viewer construction.");
        }

        [TestMethod]
        public void NullSink_NoOpsCleanly()
        {
            // The core lib defaults to NullTelemetrySink — verify no exceptions
            // and no crash from a viewer constructed with no-op telemetry.
            var browser = new FakeEmbeddedBrowser();
            var handler = new R5.SmartMessageHandler();
            using var viewer = new TestableTiroFormViewer(browser, handler, NullTelemetrySink.Instance);
            // No assertions — this is a "doesn't throw" test.
        }

        // -----------------------------------------------------------------------------
        // TiroFormViewerDefaults.TelemetrySinkFactory — application-wide opt-in hook
        // (the Sentry adapter sets this in TiroFormFillerSentry.UseSentry)
        // -----------------------------------------------------------------------------

        [TestCleanup]
        public void ResetFactory()
        {
            // Static state — wipe between tests so a failing test can't poison the next.
            TiroFormViewerDefaults.TelemetrySinkFactory = null;
        }

        [TestMethod]
        public void TelemetrySinkFactory_NullByDefault_FallsBackToNullTelemetrySink()
        {
            TiroFormViewerDefaults.TelemetrySinkFactory = null;

            var browser = new FakeEmbeddedBrowser();
            var handler = new R5.SmartMessageHandler();
            // Pass null sink so the DI ctor falls back to CreateTelemetrySink → factory lookup.
            using var viewer = new TestableTiroFormViewer(browser, handler, telemetry: null);
            SynchronizationContext.SetSynchronizationContext(null);

            Assert.AreSame(NullTelemetrySink.Instance, viewer.TelemetrySink);
        }

        [TestMethod]
        public void TelemetrySinkFactory_WhenRegistered_IsUsedByViewer()
        {
            var sink = new FakeTelemetrySink();
            TiroFormViewerDefaults.TelemetrySinkFactory = () => sink;

            var browser = new FakeEmbeddedBrowser();
            var handler = new R5.SmartMessageHandler();
            using var viewer = new TestableTiroFormViewer(browser, handler, telemetry: null);
            SynchronizationContext.SetSynchronizationContext(null);

            Assert.AreSame(sink, viewer.TelemetrySink,
                "Factory result should be the viewer's resolved TelemetrySink.");
            Assert.AreEqual(1, sink.Sessions.Count,
                "Eager-session ctor must open exactly one session on the factory-produced sink.");
        }

        [TestMethod]
        public void TelemetrySinkFactory_IsInvokedPerViewer()
        {
            // Different viewers should each get their own sink instance when the factory
            // returns a new one — matches the SentryTelemetrySink usage where each viewer
            // gets a fresh embedded-page DSN injection.
            var produced = new System.Collections.Generic.List<FakeTelemetrySink>();
            TiroFormViewerDefaults.TelemetrySinkFactory = () =>
            {
                var s = new FakeTelemetrySink();
                produced.Add(s);
                return s;
            };

            var browser1 = new FakeEmbeddedBrowser();
            var handler1 = new R5.SmartMessageHandler();
            using var viewer1 = new TestableTiroFormViewer(browser1, handler1, telemetry: null);

            var browser2 = new FakeEmbeddedBrowser();
            var handler2 = new R5.SmartMessageHandler();
            using var viewer2 = new TestableTiroFormViewer(browser2, handler2, telemetry: null);
            SynchronizationContext.SetSynchronizationContext(null);

            Assert.AreEqual(2, produced.Count, "Factory should be invoked once per viewer ctor.");
            Assert.AreNotSame(produced[0], produced[1], "Sanity: factory produced distinct instances.");
        }

        [TestMethod]
        public void TelemetrySinkFactory_ChangeAfterCtor_DoesNotAffectExistingViewer()
        {
            var first = new FakeTelemetrySink();
            TiroFormViewerDefaults.TelemetrySinkFactory = () => first;

            var browser = new FakeEmbeddedBrowser();
            var handler = new R5.SmartMessageHandler();
            using var viewer = new TestableTiroFormViewer(browser, handler, telemetry: null);
            SynchronizationContext.SetSynchronizationContext(null);
            Assert.AreSame(first, viewer.TelemetrySink, "Sanity: viewer captured the first sink.");

            // Reassigning the factory afterwards must not retroactively re-resolve.
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new FakeTelemetrySink();
            Assert.AreSame(first, viewer.TelemetrySink,
                "Sink is captured at ctor time; later factory swaps don't propagate to existing viewers.");
        }

        [TestMethod]
        public void Dispose_AddsDisposedBreadcrumb_AndFlushesSink()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink);
            viewer.Dispose();

            Assert.IsTrue(sink.Sessions[0].Breadcrumbs.Any(b =>
                b.Category == "lifecycle" && b.Message.Contains("disposed")),
                "Expected a 'lifecycle' breadcrumb noting viewer disposal.");
            Assert.IsTrue(sink.Flushed, "Sink should be flushed on Dispose.");
        }

        [TestMethod]
        public void Dispose_DoesNotDisposeInjectedSink()
        {
            // DI ctor with explicit telemetry: the caller owns the sink.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink);
            viewer.Dispose();

            Assert.IsFalse(sink.Disposed,
                "Injected sinks must NOT be disposed by the viewer (caller owns lifetime).");
        }

        [TestMethod]
        public async Task InitializeWebView_ProducesLifecycleTransaction()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);

            // Wait until init completes (signalled by SendMessage being wired).
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            var session = sink.Sessions[0];
            var initSpan = session.Transactions.FirstOrDefault(t =>
                t.Operation == "swm.lifecycle.init");
            Assert.IsNotNull(initSpan, "Expected a swm.lifecycle.init transaction for Initialize WebView.");
            Assert.AreEqual("Initialize WebView", initSpan.Name);
            Assert.IsTrue(initSpan.Finished);
            Assert.AreEqual(TelemetrySpanStatus.Ok, initSpan.FinalStatus);
            viewer.Dispose();
        }

        [TestMethod]
        public async Task HandshakeReceived_AddsLifecycleBreadcrumb()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));

            var session = sink.Sessions[0];
            Assert.IsTrue(session.Breadcrumbs.Any(b =>
                b.Category == "lifecycle" && b.Message.Contains("Handshake")),
                "Expected a handshake-received breadcrumb.");
            viewer.Dispose();
        }

        [TestMethod]
        public async Task SetContextAsync_StartsSendTransaction_WithMessageTypeTag()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            var setContextTask = viewer.SetContextAsync("http://example.org/my-form");
            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContextTask.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var session = sink.Sessions[0];
            var sendSpan = session.Transactions.FirstOrDefault(t =>
                t.Operation == "swm.send" && t.Name == "sdc.displayQuestionnaire");
            Assert.IsNotNull(sendSpan, "Expected an swm.send transaction named sdc.displayQuestionnaire.");
            Assert.IsTrue(sendSpan.Tags.TryGetValue("messageType", out var mt) && mt == "sdc.displayQuestionnaire",
                "Expected messageType tag on the send transaction.");
            Assert.IsTrue(sendSpan.Tags.TryGetValue("questionnaire_url", out var qu)
                && qu == "http://example.org/my-form",
                "Expected questionnaire_url tag on the send transaction.");
            viewer.Dispose();
        }

        [TestMethod]
        public async Task SetContextAsync_StartsSdcConfigureTransaction_WhenEndpointsAreSet()
        {
            // PR #14 introduced sdc.configure as the protocol-conformant way to push
            // endpoints into the page. The JS bridge already records a swm.receive span
            // for it; this asserts the .NET sender side now mirrors that with its own
            // swm.send span — so a unified trace shows both halves and Sentry users
            // can see configure activity in the .NET project, not only in JS.
            //
            // The span now closes on the page's RESPONSE, not on send. sdc.configure is a
            // routed request like any other: the bridge acks it, or answers with an error
            // payload when its handler throws. Finishing on send meant the span was always
            // Ok and a rejection went unnoticed — which matters because this message carries
            // readOnly, so a refused configure means a read-only launch painting an
            // editable form.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            viewer.SdcEndpointAddress = "https://sdc.example.test/fhir/r5";
            viewer.DataEndpointAddress = "https://data.example.test/fhir";
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            var setContextTask = viewer.SetContextAsync("http://example.org/my-form");
            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContextTask.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var session = sink.Sessions[0];
            var configureSpan = session.Transactions.FirstOrDefault(t =>
                t.Operation == "swm.send" && t.Name == "sdc.configure");
            Assert.IsNotNull(configureSpan, "Expected an swm.send transaction named sdc.configure.");
            Assert.IsFalse(configureSpan.Finished,
                "sdc.configure is a round trip now; its span must stay open until the page responds.");

            // Ack it the way the bridge does, and the span closes Ok.
            var configureId = JsonProbe.ExtractStringField(
                browser.PostedMessages.FindLast(m => m.Contains("\"messageType\":\"sdc.configure\"")), "messageId");
            browser.RaiseMessageReceived($@"{{
                ""messageId"": ""resp-cfg"",
                ""responseToMessageId"": ""{configureId}"",
                ""additionalResponsesExpected"": false,
                ""payload"": {{ ""$type"": ""base"" }}
            }}");
            await PollFor(() => configureSpan.Finished, TimeSpan.FromSeconds(5));
            Assert.AreEqual(TelemetrySpanStatus.Ok, configureSpan.FinalStatus);
            Assert.IsTrue(configureSpan.Tags.TryGetValue("messageType", out var mt) && mt == "sdc.configure");
            Assert.IsTrue(configureSpan.Tags.TryGetValue("sdc_server", out var sdc) && sdc == "https://sdc.example.test/fhir/r5",
                "Expected sdc_server tag carrying the host-configured endpoint.");
            Assert.IsTrue(configureSpan.Tags.TryGetValue("data_server", out var data) && data == "https://data.example.test/fhir",
                "Expected data_server tag carrying the host-configured endpoint.");
            viewer.Dispose();
        }

        [TestMethod]
        public async Task SetContextAsync_DoesNotEmitSdcConfigureTransaction_WhenEndpointsAreUnset()
        {
            // Mirror of the host-side suppression: with no endpoints to push, no
            // sdc.configure is sent and no swm.send span is recorded for it.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            // SdcEndpointAddress / DataEndpointAddress are null by default on the abstract
            // base — TestableTiroFormViewer doesn't set them in its ctor.
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            var setContextTask = viewer.SetContextAsync("http://example.org/my-form");
            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContextTask.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var session = sink.Sessions[0];
            Assert.IsFalse(session.Transactions.Any(t =>
                t.Operation == "swm.send" && t.Name == "sdc.configure"),
                "Expected no sdc.configure transaction when both endpoints are empty.");
            viewer.Dispose();
        }

        [TestMethod]
        public async Task InboundFormSubmit_StartsReceiveTransaction_WithOutcomeAwareStatus()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1", outcomeError: false));

            await PollFor(() => viewer.State == TiroFormViewerState.Submitted, TimeSpan.FromSeconds(5));

            var session = sink.Sessions[0];
            var receiveSpan = session.Transactions.FirstOrDefault(t =>
                t.Operation == "swm.receive" && t.Name == "form.submitted");
            Assert.IsNotNull(receiveSpan, "Expected an swm.receive transaction for form.submitted.");
            Assert.IsTrue(receiveSpan.Finished);
            Assert.AreEqual(TelemetrySpanStatus.Ok, receiveSpan.FinalStatus,
                "form.submitted with a clean OperationOutcome must finish with Ok.");
            viewer.Dispose();
        }

        [TestMethod]
        public async Task DisposingDuringAReceive_FinishesTheInFlightTransaction()
        {
            // Found in a real transcript, not by reading the code: a clinician closing the form on
            // the submit that has just landed disposed the viewer 129 ms into handling it, and the
            // receive transaction was abandoned. An unfinished span is a signal — a backend reads
            // one as work that never came back, and FileTelemetrySink's transcript documents it as
            // "the viewer was still waiting" — so a healthy session leaving one made that signal
            // mean two different things.
            //
            // Disposing from inside the FormSubmitted handler is how the field is still set: the
            // finally that nulls it has not run yet. That is also exactly the real sequence.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            viewer.FormSubmitted += (_, __) => viewer.Dispose();

            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1", outcomeError: false));

            var receiveSpan = sink.Sessions[0].Transactions.FirstOrDefault(t =>
                t.Operation == "swm.receive" && t.Name == "form.submitted");
            Assert.IsNotNull(receiveSpan, "Expected an swm.receive transaction for form.submitted.");
            Assert.IsTrue(receiveSpan.Finished,
                "the in-flight receive must be finished on dispose, not abandoned");

            // Ok, not Cancelled. The outcome is recorded before the subscriber runs, so the
            // backstop arrives second and first-finish-wins discards it. Without that ordering a
            // successful submit closed from its own handler — "save and close" — would read as
            // Cancelled, which is a worse lie than the dangling span this replaced.
            Assert.AreEqual(TelemetrySpanStatus.Ok, receiveSpan.FinalStatus,
                "the dispose backstop must not relabel an outcome that was already recorded");
            CollectionAssert.AreEqual(
                new[] { TelemetrySpanStatus.Ok, TelemetrySpanStatus.Cancelled },
                receiveSpan.FinishStatuses.Take(2).ToArray(),
                "asserting the ORDER, not just the winner: FinalStatus alone would pass even if " +
                "the backstop never ran, or if it ran first and happened to be overwritten");
        }

        [TestMethod]
        public async Task DisposingBeforeAnyOutcomeIsRecorded_FinishesTheReceiveAsCancelled()
        {
            // The other half: nothing has recorded an outcome, so the backstop's own status is what
            // lands. Cancelled rather than Ok, because the round-trip did not complete.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));

            var session = sink.Sessions[0];
            var handshake = session.Transactions.FirstOrDefault(t =>
                t.Operation == "swm.receive" && t.Name == "status.handshake");
            Assert.IsNotNull(handshake, "sanity: the handshake receive transaction exists");

            viewer.Dispose();

            // The handshake receive completed normally before the dispose, so it keeps Ok and the
            // backstop finds nothing in flight — asserted so this test cannot pass by the backstop
            // firing on an already-finished span.
            Assert.AreEqual(TelemetrySpanStatus.Ok, handshake.FinalStatus);
            Assert.IsTrue(session.Transactions.TrueForAll(t => t.Finished),
                "no span may be left dangling once the viewer is disposed: " +
                string.Join(", ", session.Transactions.Where(t => !t.Finished).Select(t => t.Name)));
        }

        [TestMethod]
        public async Task InboundFormSubmit_WithFailedOutcome_FinishesAsInvalidArgument()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-2", outcomeError: true));

            await PollFor(() => viewer.State == TiroFormViewerState.Submitted, TimeSpan.FromSeconds(5));

            var session = sink.Sessions[0];
            var receiveSpan = session.Transactions.First(t =>
                t.Operation == "swm.receive" && t.Name == "form.submitted");
            // OnFormSubmitted finishes this transaction with the outcome status, and the
            // trailing OnBrowserMessageReceived Finish(Ok) does not displace it: first finish
            // wins. It was "a no-op (idempotency)" here only because this fake always was one —
            // the real Sentry adapter was not, and shipped the trailing Ok.
            Assert.AreEqual(TelemetrySpanStatus.InvalidArgument, receiveSpan.FinalStatus,
                "form.submitted with an error-severity OperationOutcome must finish with InvalidArgument.");
        }

        [TestMethod]
        public async Task BootstrapConfig_FromSession_IsInjectedAsInitializationScript()
        {
            // The host injects window.__tiroSentryConfig as an init script before the bridge.
            // FakeTelemetrySession.GetEmbeddedBootstrapConfig returns a non-empty dictionary,
            // so the host should produce a "window.__tiroSentryConfig=..." script.
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

            Assert.IsTrue(browser.InitializationScripts.Any(s => s.Contains("__tiroSentryConfig")),
                "Expected the host to inject a __tiroSentryConfig init script.");
            // Bridge JS itself is also injected.
            Assert.IsTrue(browser.InitializationScripts.Any(s => s.Contains("SmartWebMessaging")),
                "Expected the host to inject the SMART Web Messaging bridge.");
            viewer.Dispose();
        }

        // A send transaction is finished twice on this path, deterministically and on one
        // thread: WaitForHandshakeAsync finishes it InternalError for a terminal web-sdk
        // failure and throws, and the caller's catch finishes it again with that exception.
        // Until the Sentry adapter honoured first-wins, the second call rewrote the first, and
        // a refused session was reported with whatever status Sentry derived from the
        // exception instead of the one the code chose.
        //
        // This is a PIN on the caller sequence, not a regression: FakeTelemetrySpan was always
        // first-wins, so FinalStatus was already right here — the bug lived only in the real
        // adapter (see TestSentryTelemetrySpan). What this adds is proof that the double
        // finish is real and reached, so the adapter's guard is load-bearing rather than
        // theoretical, and FinishCalls is what makes it visible.
        //
        // The handshake TIMEOUT path has the identical shape (Finish(DeadlineExceeded) then
        // Finish(TimeoutException)) and is not covered separately: HandshakeTimeoutMs is a
        // 30s const with no seam, and a 30s test is not worth the same evidence.
        [TestMethod]
        public async Task ARefusedSessionsSendTransaction_KeepsTheStatusTheCodeChose()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            try
            {
                await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));

                // A good handshake first, so the failure below arrives on an established
                // session — the one route that reaches the _webSdkFailure branch rather than
                // faulting the one-shot handshake TCS.
                var setContext = viewer.SetContextAsync("http://example.org/q");
                browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
                await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

                // A page reload that swapped the SDK: terminal, and every later send fails.
                browser.RaiseMessageReceived(SwmTest.Handshake(
                    "hs-2",
                    @"{ ""client"": { ""name"": ""tiro-web-sdk"", ""version"": ""0.2.1"", ""source"": ""collision"" } }"));

                await Assert.ThrowsExceptionAsync<WebSdkLoadException>(
                    () => viewer.SendFormRequestSubmitAsync());

                var submitSend = sink.Sessions[0].Transactions.Last(t =>
                    t.Operation == "swm.send" && t.Name == "ui.form.requestSubmit");

                // Both calls happened — that is the point — and the first one is what shipped.
                Assert.AreEqual(TelemetrySpanStatus.InternalError, submitSend.FinalStatus,
                    "the exception finish rewrote the status the refusal path chose");
                // >= 2, not == 2: ITelemetrySpan permits repeat finishes, so an exact count
                // pins the caller's structure rather than the contract and would redden on a
                // legitimate refactor. What matters is that a second finish happens at all —
                // that is what makes the adapter's guard load-bearing here.
                Assert.IsTrue(submitSend.FinishCalls.Count >= 2,
                    $"expected the documented double finish, saw {submitSend.FinishCalls.Count}; "
                    + "if this is now 1 the caller changed and this test pins a sequence that "
                    + "no longer exists");
                Assert.AreEqual(1, submitSend.LateAssociatedExceptions.Count,
                    "the repeat finish should still associate its exception for trace linkage");
            }
            finally { viewer.Dispose(); }
        }

        // A throwing FormSubmitted subscriber finishes the receive transaction with that
        // exception, then rethrows so the page gets an error ack — and the outer catch finishes
        // it again with the same exception. Asserted as first-wins rather than exactly-once:
        // ITelemetrySpan permits the repeat, so demanding one call would pin a caller detail
        // and go red on a legitimate refactor.
        [TestMethod]
        public async Task AThrowingFormSubmittedSubscriber_KeepsItsExceptionAsTheOutcome()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            try
            {
                await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));
                browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));

                var thrown = new InvalidOperationException("subscriber blew up");
                viewer.FormSubmitted += (_, __) => throw thrown;
                browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-throw", outcomeError: false));

                var receive = sink.Sessions[0].Transactions.First(t =>
                    t.Operation == "swm.receive" && t.Name == "form.submitted");

                Assert.AreSame(thrown, receive.FinalException,
                    "the subscriber's exception is the outcome, not a later Ok");
                // The trailing Ok IS called — SmartMessageHandlerBase turns the rethrow into an
                // error response, so OnBrowserMessageReceived completes normally and finishes
                // the transaction. Asserting it never happens would pin a caller design this
                // branch deliberately does not have. What must hold is that it did not become
                // the outcome, which is the contract and the adapter's job.
                CollectionAssert.Contains(receive.FinishStatuses.ToList(), TelemetrySpanStatus.Ok,
                    "expected the trailing Ok; if it is gone the caller changed and the comment "
                    + "above is stale");
                Assert.IsNull(receive.FinalStatus,
                    "the trailing Ok overwrote the exception outcome — first finish must win");
            }
            finally { viewer.Dispose(); }
        }

        // A send that throws leaves WrapForRoundTrip's cancellation sentinel registered: it is
        // created before the send and disposed only inside the RESPONSE handler, which never
        // runs. So the registration survives on _lifetimeCts, and disposing the viewer later
        // fires Finish(Cancelled) on a span the catch block already finished with the real
        // error. A REGRESSION, and a deterministic one: pre-fix, Cancelled overwrote the error
        // in Sentry, so the surviving trace of a failed send blamed a cancellation nobody
        // requested. Neither the sentinel leak nor this overwrite was noticed before.
        //
        // The leak itself is a separate defect and is deliberately not fixed here — this pins
        // that the contract contains its consequence.
        [TestMethod]
        public async Task AFailedSendThenTeardown_KeepsTheErrorNotTheCancellation()
        {
            var sink = new FakeTelemetrySink();
            var viewer = NewViewer(sink, out var browser, out var handler);
            try
            {
                await PollFor(() => handler.SendMessage != null, TimeSpan.FromSeconds(5));
                var setContext = viewer.SetContextAsync("http://example.org/q");
                browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
                await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

                var boom = new InvalidOperationException("webview torn down mid-send");
                browser.ThrowOnNextPostMessage = boom;
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => viewer.SendFormRequestSubmitAsync());

                var send = sink.Sessions[0].Transactions.Last(t =>
                    t.Operation == "swm.send" && t.Name == "ui.form.requestSubmit");
                Assert.AreSame(boom, send.FinalException, "the send's own failure is the outcome");

                // Teardown fires the leaked registration.
                viewer.Dispose();

                CollectionAssert.Contains(send.FinishStatuses.ToList(), TelemetrySpanStatus.Cancelled,
                    "expected the leaked sentinel to fire on teardown; if it no longer does, the "
                    + "leak was fixed and this test should assert that instead");
                Assert.AreSame(boom, send.FinalException,
                    "teardown's Cancelled overwrote the real error — first finish must win");
                Assert.IsNull(send.FinalStatus);
            }
            finally { viewer.Dispose(); }
        }

        // -----------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------

        private static TestableTiroFormViewer NewViewer(FakeTelemetrySink sink)
        {
            var browser = new FakeEmbeddedBrowser();
            var handler = new R5.SmartMessageHandler();
            var viewer = new TestableTiroFormViewer(browser, handler, sink);
            // Constructing a WinForms UserControl auto-installs WindowsFormsSynchronizationContext
            // on the current thread. The MSTest thread has no message pump, so any subsequent
            // `await` that captured this context would never resume. Clear it.
            SynchronizationContext.SetSynchronizationContext(null);
            return viewer;
        }

        private static TestableTiroFormViewer NewViewer(
            FakeTelemetrySink sink,
            out FakeEmbeddedBrowser browser,
            out R5.SmartMessageHandler handler)
        {
            browser = new FakeEmbeddedBrowser();
            handler = new R5.SmartMessageHandler();
            var viewer = new TestableTiroFormViewer(browser, handler, sink);
            SynchronizationContext.SetSynchronizationContext(null);
            return viewer;
        }


        // The bridge resolves window.__tiroSdkUrl at module scope, so the host must register
        // that script BEFORE the bridge. Deleting the injection would otherwise leave every
        // test green while the page silently fell back to a path the host never publishes.
        [TestMethod]
        public async Task SdkUrlIsInjectedBeforeTheBridge()
        {
            var browser = new FakeEmbeddedBrowser();
            var viewer = new TestableTiroFormViewer(browser, new R5.SmartMessageHandler(), new FakeTelemetrySink());
            try
            {
                // Wait for the two scripts themselves, not for a count: init also injects
                // __tiroSentryConfig whenever the sink supplies a bootstrap config (this fake
                // does), so a count of two is reached before the bridge is registered.
                int sdkUrlScript = -1, bridgeScript = -1;
                await PollFor(
                    () => (sdkUrlScript = browser.InitializationScripts.FindIndex(x => x.Contains("__tiroSdkUrl="))) >= 0
                        && (bridgeScript = browser.InitializationScripts.FindIndex(x => x.Contains("window.SmartWebMessaging ="))) >= 0,
                    TimeSpan.FromSeconds(5));

                Assert.IsTrue(sdkUrlScript < bridgeScript,
                    "the SDK URL must be injected before the bridge, which reads it at module scope");
                StringAssert.Contains(browser.InitializationScripts[sdkUrlScript], WebSdkAssets.BundleUrl);
            }
            finally { viewer.Dispose(); }
        }

        private static string BuildHandshakeMessage(string id) => SwmTest.Handshake(id);

        private static string BuildFormSubmitMessage(string id, bool outcomeError) =>
            outcomeError
                ? $@"{{
                    ""messageId"": ""{id}"",
                    ""messagingHandle"": ""smart-web-messaging"",
                    ""messageType"": ""form.submitted"",
                    ""payload"": {{
                        ""response"": {{ ""resourceType"": ""QuestionnaireResponse"", ""questionnaire"": ""http://example.org/q"", ""status"": ""completed"" }},
                        ""outcome"": {{
                            ""resourceType"": ""OperationOutcome"",
                            ""issue"": [{{
                                ""severity"": ""error"",
                                ""code"": ""required"",
                                ""details"": {{ ""text"": ""Missing required field"" }}
                            }}]
                        }}
                    }}
                }}"
                : $@"{{
                    ""messageId"": ""{id}"",
                    ""messagingHandle"": ""smart-web-messaging"",
                    ""messageType"": ""form.submitted"",
                    ""payload"": {{
                        ""response"": {{ ""resourceType"": ""QuestionnaireResponse"", ""questionnaire"": ""http://example.org/q"", ""status"": ""completed"" }},
                        ""outcome"": {{ ""resourceType"": ""OperationOutcome"", ""issue"": [] }}
                    }}
                }}";
    }
}
