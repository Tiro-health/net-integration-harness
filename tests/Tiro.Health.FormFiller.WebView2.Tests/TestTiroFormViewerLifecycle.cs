using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message.Payload;
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

        // A save-draft round-trips status in-progress. The session must survive it: the
        // documented EhrShell flow is save a draft, keep filling, submit later. Advancing to
        // Submitted here made every later send throw "already been submitted".
        [TestMethod]
        public async Task DraftSubmit_KeepsTheSessionUsable_ThenAFinalSubmitEndsIt()
        {
            var responses = new List<HL7Model.QuestionnaireResponse>();
            _viewer.FormSubmitted += (_, args) => responses.Add(args.Response);

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContext.Within5s();

            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-draft", status: "in-progress"));
            await PollFor(() => responses.Count == 1, TimeSpan.FromSeconds(5));

            Assert.AreEqual(HL7Model.QuestionnaireResponse.QuestionnaireResponseStatus.InProgress, responses[0].Status);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State,
                "a draft must not end the session");

            // The call that used to throw: submitting after a saved draft.
            await _viewer.SendFormRequestSubmitAsync().Within5s();

            // A second draft is equally non-terminal.
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-draft-2", status: "in-progress"));
            await PollFor(() => responses.Count == 2, TimeSpan.FromSeconds(5));
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);

            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-final"));
            await PollFor(() => responses.Count == 3, TimeSpan.FromSeconds(5));
            Assert.AreEqual(TiroFormViewerState.Submitted, _viewer.State,
                "a completed response ends the session");

            // A late draft must not reopen a finished session.
            _browser.RaiseMessageReceived(BuildFormSubmitMessage("fs-late-draft", status: "in-progress"));
            await PollFor(() => responses.Count == 4, TimeSpan.FromSeconds(5));
            Assert.AreEqual(TiroFormViewerState.Submitted, _viewer.State,
                "Submitted is terminal — a late draft must not downgrade it");
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
        public async Task DirtyChangedMessage_UpdatesIsDirty_AndFiresEvent()
        {
            var fired = new TaskCompletionSource<FormDirtyChangedEventArgs>();
            _viewer.FormDirtyChanged += (_, args) => fired.TrySetResult(args);

            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            Assert.IsFalse(_viewer.IsDirty);

            _browser.RaiseMessageReceived(BuildDirtyChangedMessage("dc-1", isDirty: true));

            var args = await fired.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            Assert.IsTrue(args.IsDirty);
            Assert.IsTrue(_viewer.IsDirty);
            // Dirty-state is informational — it doesn't move the lifecycle state machine.
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
        public async Task SendFormRequestSubmitAsync_WhileInitializing_ThrowsInvalidOperation()
        {
            // No handshake yet (state Initializing) and no SetContextAsync — nothing to submit.
            await DelayUntilBrowserInitialized();
            Assert.AreEqual(TiroFormViewerState.Initializing, _viewer.State);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await _viewer.SendFormRequestSubmitAsync());
        }

        [TestMethod]
        public async Task SendFormRequestSubmitAsync_WhenReadyButNoContext_ThrowsInvalidOperation()
        {
            // Handshake received (state Ready) but SetContextAsync never called: no form is
            // displayed, so there is nothing to submit. Must fail fast with InvalidOperationException
            // rather than blocking on a handshake/submit that can't complete — with navigation now
            // deferred to SetContextAsync, a bare submit would otherwise hang until the handshake
            // timeout. Guards the tightened GuardCanSendFormRequest (Ready is no longer accepted).
            await DelayUntilBrowserInitialized();
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            Assert.AreEqual(TiroFormViewerState.Ready, _viewer.State);

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
        public async Task Navigation_IsDeferredUntilSetContext_ReadingWebContentFolderThen()
        {
            await DelayUntilBrowserInitialized();

            // Navigation must NOT happen eagerly during init. If it did, a WebContentFolder
            // set after construction (as a Form_Load handler does) would race the async init
            // and might be read before it's assigned.
            Assert.AreEqual(0, _browser.NavigatedUrls.Count,
                "Viewer must not navigate until the first SetContextAsync.");

            // Assign the folder AFTER construction, exactly as a Form_Load handler would.
            _viewer.WebContentFolder = @"C:\custom\web\content";

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            Assert.AreEqual(1, _browser.NavigatedUrls.Count, "SetContextAsync should navigate exactly once.");
            CollectionAssert.Contains(
                _browser.VirtualHostMappings.ConvertAll(m => m.Folder),
                @"C:\custom\web\content",
                "The folder assigned before SetContextAsync must be the one mapped — proving it is " +
                "read at SetContextAsync, not captured earlier during construction.");
        }

        // The page URL carries the harness version, for the reason the SDK's file name does:
        // WebView2 caches by URL and virtual-host responses carry no cache headers, so at a
        // constant URL an upgraded harness could load the previous release's page — and a
        // cached pre-GH-60 page still carries a CDN script tag that now collides.
        [TestMethod]
        public async Task Navigation_CacheBustsThePageOnTheHarnessVersion()
        {
            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContext.Within5s();

            var navigated = _browser.NavigatedUrls[0];
            var version = typeof(TiroFormViewerState).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(TiroFormViewerState).Assembly.GetName().Version?.ToString();
            StringAssert.Contains(navigated.Query, "v=");
            StringAssert.Contains(Uri.UnescapeDataString(navigated.Query), version!);
        }

        [TestMethod]
        public async Task ReadOnly_WithNoEndpointOverride_StillEmitsConfigureCarryingReadOnly()
        {
            await DelayUntilBrowserInitialized();

            // The base viewer seeds no SdcEndpointAddress (only the closed R5/R4 bindings do),
            // so this isolates read-only as the sole reason to emit sdc.configure. Before the
            // ReadOnly property existed the message was gated on the endpoints alone, and a
            // read-only launch with no endpoint override would have sent nothing at all.
            _viewer.ReadOnly = true;

            await RunSetContextToCompletion();

            var configure = SingleSdcConfigureMessage();
            StringAssert.Contains(configure, "\"configuration\":{\"readOnly\":true}",
                "ReadOnly must travel on configuration.readOnly — the protocol's renderer-specific " +
                "extension point — even when no endpoint override is set.");
        }

        [TestMethod]
        public async Task ReadOnly_DefaultFalse_EmitsNoConfigureAtAll()
        {
            await DelayUntilBrowserInitialized();

            // Default ReadOnly (false) plus no endpoint override means there is nothing to
            // configure. false is already the page-side default, so asserting it would be
            // noise on the wire.
            await RunSetContextToCompletion();

            Assert.AreEqual(0, SdcConfigureMessages().Count,
                "With no endpoints and ReadOnly left at its default, no sdc.configure should be sent.");
        }

        [TestMethod]
        public async Task ReadOnly_WithSdcEndpoint_EmitsBothKeysInOneConfiguration()
        {
            await DelayUntilBrowserInitialized();

            _viewer.SdcEndpointAddress = "http://sdc.example.org/fhir/r5";
            _viewer.ReadOnly = true;

            await RunSetContextToCompletion();

            // Both flags share the single `configuration` object — neither may displace the
            // other (the pre-existing code collapsed configuration to null whenever
            // SdcEndpointAddress was unset).
            var configure = SingleSdcConfigureMessage();
            StringAssert.Contains(configure, "\"sdcServer\":\"http://sdc.example.org/fhir/r5\"");
            StringAssert.Contains(configure, "\"readOnly\":true");
        }

        [TestMethod]
        public async Task SetContextAsync_LaunchContext_CarriesArbitraryNamedResourceAlongsidePatient()
        {
            await DelayUntilBrowserInitialized();

            var patient = new HL7Model.Patient { Id = "P1" };
            var coverage = new HL7Model.Coverage { Id = "COV1" };
            var launchContext = new List<LaunchContext<HL7Model.Resource>>
            {
                new LaunchContext<HL7Model.Resource>("coverage", contentResource: coverage)
            };

            var setContext = _viewer.SetContextAsync(
                "http://example.org/q", patient: patient, launchContext: launchContext);
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var display = SingleSdcDisplayQuestionnaireMessage();
            StringAssert.Contains(display, "\"name\":\"patient\"",
                "The patient shorthand must still be present alongside the extra launch context entry.");
            StringAssert.Contains(display, "\"resourceType\":\"Patient\",\"id\":\"P1\"");
            StringAssert.Contains(display, "\"name\":\"coverage\"",
                "An arbitrary named resource passed via launchContext must reach the wire, not just patient/encounter/author.");
            StringAssert.Contains(display, "\"resourceType\":\"Coverage\",\"id\":\"COV1\"");
        }

        [TestMethod]
        public void Dispose_ClearsPendingResponseListeners()
        {
            // Pending response listeners hold closures over caller-supplied handlers, the
            // per-send telemetry-span sentinel CTS, and whatever state the caller's
            // handler captured. After Dispose, the inbound-message guard prevents these
            // from ever firing — clear the dictionary so the closures release for GC
            // immediately rather than waiting on the handler itself to become unreachable.
            _handler.RegisterResponseListener("msg-1", _ => Task.CompletedTask);
            _handler.RegisterResponseListener("msg-2", _ => Task.CompletedTask);
            Assert.IsTrue(_handler.HasPendingResponseListener("msg-1"), "Sanity: listener registered before Dispose.");
            Assert.IsTrue(_handler.HasPendingResponseListener("msg-2"), "Sanity: listener registered before Dispose.");

            _viewer.Dispose();

            Assert.IsFalse(_handler.HasPendingResponseListener("msg-1"),
                "Dispose should clear pending response listeners so caller closures can be GC'd.");
            Assert.IsFalse(_handler.HasPendingResponseListener("msg-2"),
                "Dispose should clear pending response listeners so caller closures can be GC'd.");
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

        // Drive a full SetContextAsync round trip: start it, satisfy the handshake it waits
        // on, then await completion. Callers configure the viewer first, then inspect
        // _browser.PostedMessages for what went out.
        private async Task RunSetContextToCompletion()
        {
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(BuildHandshakeMessage("hs-1"));
            await setContext.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }

        private List<string> SdcConfigureMessages()
            => _browser.PostedMessages.FindAll(m => m.Contains("\"messageType\":\"sdc.configure\""));

        private string SingleSdcConfigureMessage()
        {
            var matches = SdcConfigureMessages();
            Assert.AreEqual(1, matches.Count, "Expected exactly one sdc.configure message.");
            return matches[0];
        }

        private List<string> SdcDisplayQuestionnaireMessages()
            => _browser.PostedMessages.FindAll(m => m.Contains("\"messageType\":\"sdc.displayQuestionnaire\""));

        private string SingleSdcDisplayQuestionnaireMessage()
        {
            var matches = SdcDisplayQuestionnaireMessages();
            Assert.AreEqual(1, matches.Count, "Expected exactly one sdc.displayQuestionnaire message.");
            return matches[0];
        }


        private static string BuildHandshakeMessage(string id) => SwmTest.Handshake(id);

        private static string BuildUiDoneMessage(string id) => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""ui.done"",
            ""payload"": {{}}
        }}";

        private static string BuildDirtyChangedMessage(string id, bool isDirty) => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""ui.form.dirtyChanged"",
            ""payload"": {{ ""isDirty"": {(isDirty ? "true" : "false")} }}
        }}";

        private static string BuildFormSubmitMessage(string id, string status = "completed") => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""form.submitted"",
            ""payload"": {{
                ""response"": {{
                    ""resourceType"": ""QuestionnaireResponse"",
                    ""questionnaire"": ""http://example.org/q"",
                    ""status"": ""{status}""
                }},
                ""outcome"": {{
                    ""resourceType"": ""OperationOutcome"",
                    ""issue"": []
                }}
            }}
        }}";
    }
}
