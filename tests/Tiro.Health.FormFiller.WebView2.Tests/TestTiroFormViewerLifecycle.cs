using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;
using HL7Model = Hl7.Fhir.Model;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// Locks in the <see cref="TiroFormViewerState"/> machine introduced in commit
    /// 544df19: Initializing → Ready → ContextSet → Submitted → Disposed, with explicit
    /// guard exceptions on invalid transitions.
    /// </summary>
    [TestClass]
    public class TestTiroFormViewerLifecycle
    {
        private FakeEmbeddedBrowser _browser = null!;
        private FakeTelemetrySink _sink = null!;
        private R5.SmartMessageHandler _handler = null!;
        private TestableTiroFormViewer _viewer = null!;

        [TestInitialize]
        public void Init()
        {
            _browser = new FakeEmbeddedBrowser();
            _sink = new FakeTelemetrySink();
            _handler = new R5.SmartMessageHandler();
            _viewer = new TestableTiroFormViewer(_browser, _handler, _sink);
            // Constructing a WinForms UserControl auto-installs WindowsFormsSynchronizationContext
            // on the current thread. The MSTest thread has no message pump, so awaits would
            // never resume. Clear the context for all subsequent test awaits.
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { _viewer.Dispose(); } catch { /* idempotency tested separately */ }
        }

        [TestMethod]
        public void InitialState_IsInitializing()
        {
            Assert.AreEqual(TiroFormViewerState.Initializing, _viewer.State);
        }

        [TestMethod]
        public async Task HandshakeMessage_TransitionsInitializingToReady()
        {
            // Wait for the runtime init task to wire up the browser message subscription.
            await DelayUntilBrowserInitialized();

            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));

            Assert.AreEqual(TiroFormViewerState.Ready, _viewer.State);
        }

        [TestMethod]
        public async Task FormSubmittedMessage_TransitionsToSubmitted_AndFiresEvent()
        {
            var fired = new TaskCompletionSource<FormSubmittedEventArgs<HL7Model.QuestionnaireResponse, HL7Model.OperationOutcome>>();
            _viewer.FormSubmitted += (_, args) => fired.TrySetResult(args);

            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1"));

            var args = await fired.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            Assert.IsNotNull(args.Response);
            Assert.IsNotNull(args.Outcome);
            Assert.AreEqual(TiroFormViewerState.Submitted, _viewer.State);
        }

        [TestMethod]
        public async Task UiDoneMessage_FiresCloseApplication_StateUnchanged()
        {
            var fired = new TaskCompletionSource<bool>();
            _viewer.CloseApplication += (_, _) => fired.TrySetResult(true);

            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            _browser.RaiseMessageReceived(BuildUiDoneMessage("uid-1"));

            await fired.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            // ui.done is informational — it doesn't move the state machine to Submitted.
            Assert.AreEqual(TiroFormViewerState.Ready, _viewer.State);
        }

        [TestMethod]
        public void Dispose_TransitionsToDisposed()
        {
            _viewer.Dispose();
            Assert.AreEqual(TiroFormViewerState.Disposed, _viewer.State);
        }

        [TestMethod]
        public async Task SetContextAsync_AfterDispose_ThrowsObjectDisposed()
        {
            _viewer.Dispose();
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
                await _viewer.SetContextAsync("http://example.org/q"));
        }

        [TestMethod]
        public async Task SendFormRequestSubmitAsync_AfterDispose_ThrowsObjectDisposed()
        {
            _viewer.Dispose();
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
                await _viewer.SendFormRequestSubmitAsync());
        }

        [TestMethod]
        public async Task SetContextAsync_AfterSubmit_ThrowsInvalidOperation()
        {
            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1"));
            // Wait for state to advance.
            await PollFor(() => _viewer.State == TiroFormViewerState.Submitted, TimeSpan.FromSeconds(5));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await _viewer.SetContextAsync("http://example.org/q"));
        }

        [TestMethod]
        public async Task SendFormRequestSubmitAsync_AfterSubmit_ThrowsInvalidOperation()
        {
            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1"));
            await PollFor(() => _viewer.State == TiroFormViewerState.Submitted, TimeSpan.FromSeconds(5));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await _viewer.SendFormRequestSubmitAsync());
        }

        [TestMethod]
        public async Task SetContextAsync_TwiceFromContextSet_ThrowsInvalidOperation()
        {
            // First call: handshake races with SetContextAsync's wait, then send completes
            // and the state advances to ContextSet.
            await DelayUntilBrowserInitialized();
            var firstSetContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await firstSetContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await _viewer.SetContextAsync("http://example.org/q-2"));
        }

        [TestMethod]
        public async Task Dispose_CancelsInFlightSetContext()
        {
            await DelayUntilBrowserInitialized();
            // Don't simulate the handshake — the SetContextAsync call should hang on
            // _handshakeReceivedSource.Task until Dispose cancels the lifetime CTS.
            var setContext = _viewer.SetContextAsync(
                "http://example.org/q",
                cancellationToken: CancellationToken.None);

            _viewer.Dispose();

            await AssertThrowsCancelled(async () =>
                await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        }

        [TestMethod]
        public async Task SetContextAsync_PreCancelledToken_Throws()
        {
            await DelayUntilBrowserInitialized();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await AssertThrowsCancelled(async () =>
                await _viewer.SetContextAsync("http://example.org/q", cancellationToken: cts.Token));
        }

        [TestMethod]
        public async Task MessageReceived_AfterDispose_IsIgnored()
        {
            await DelayUntilBrowserInitialized();
            _viewer.Dispose();

            // Should not throw, should not fire any events.
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-1"));

            // Sentry session was already finalised; no new transactions should appear after
            // disposal. (We can't strictly check "no transactions" because Initialize WebView
            // is started before disposal, but no FormSubmit transaction should exist.)
            Assert.AreEqual(TiroFormViewerState.Disposed, _viewer.State);
        }

        // -----------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------

        // Wait until InitializeBrowserAsync has wired the SendMessage delegate. Simple
        // poll — the runtime init runs on a Task started in InitializeRuntime.
        private async Task DelayUntilBrowserInitialized()
            => await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));

        // Helper: asserts the given async action throws OperationCanceledException OR any
        // subclass (TaskCanceledException). MSTest's ThrowsExceptionAsync requires exact
        // type, but cancellation is naturally signalled with the derived TaskCanceledException
        // by the polyfill — both are valid for our contract.
        private static async Task AssertThrowsCancelled(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Assert.Fail("Expected OperationCanceledException (or subclass).");
        }

        private static async Task PollFor(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return;
                await Task.Delay(10);
            }
            Assert.Fail($"Predicate did not become true within {timeout}.");
        }

        private static string BuildHandshakeMessage(string id) => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""status.handshake"",
            ""payload"": {{}}
        }}";

        private static string BuildUiDoneMessage(string id) => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""ui.done"",
            ""payload"": {{}}
        }}";

        private static string BuildFormSubmitMessage(string id) => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""form.submitted"",
            ""payload"": {{
                ""response"": {{
                    ""resourceType"": ""QuestionnaireResponse"",
                    ""questionnaire"": ""http://example.org/q"",
                    ""status"": ""completed""
                }},
                ""outcome"": {{
                    ""resourceType"": ""OperationOutcome"",
                    ""issue"": []
                }}
            }}
        }}";
    }
}
