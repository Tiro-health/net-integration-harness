using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using Tiro.Health.SmartWebMessaging;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// A page that rejects a host request used to be invisible: a send completes once the
    /// message is posted, and nothing inspected the response, so the request looked
    /// successful and the failure lived only in the WebView console.
    /// </summary>
    [TestClass]
    public class TestPageErrors
    {
        private FakeEmbeddedBrowser _browser = null!;
        private FakeTelemetrySink _sink = null!;
        private TestableTiroFormViewer _viewer = null!;

        [TestInitialize]
        public void Init()
        {
            _browser = new FakeEmbeddedBrowser();
            _sink = new FakeTelemetrySink();
            _viewer = new TestableTiroFormViewer(_browser, new R5.SmartMessageHandler(), _sink);
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { _viewer.Dispose(); } catch { /* not under test */ }
        }

        /// <summary>The error envelope the bridge sends when a page handler throws.</summary>
        private static string ErrorResponseTo(string requestMessageId, string errorType, string message) => $@"{{
            ""messageId"": ""resp-{requestMessageId}"",
            ""responseToMessageId"": ""{requestMessageId}"",
            ""additionalResponsesExpected"": false,
            ""payload"": {{
                ""$type"": ""error"",
                ""errorType"": ""{errorType}"",
                ""errorMessage"": ""{message}""
            }}
        }}";

        /// <summary>
        /// The messageId of the last request of a given type the viewer posted. Indexing
        /// positionally would race: SetContextAsync's post-handshake continuation runs on a
        /// pool thread while OnBrowserMessageReceived posts the handshake ack, so "the last
        /// message" is not deterministic.
        /// </summary>
        private string PostedMessageId(string messageType)
        {
            var json = _browser.PostedMessages.FindLast(m => m.Contains($"\"messageType\":\"{messageType}\""));
            Assert.IsNotNull(json, $"no {messageType} was posted; saw {_browser.PostedMessages.Count} messages");
            return JsonProbe.ExtractStringField(json, "messageId");
        }

        [TestMethod]
        public async Task PageRejectsRequestSubmit_RaisesPageError_AndCapturesIt()
        {
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            await _viewer.SendFormRequestSubmitAsync().Within5s();
            _browser.RaiseMessageReceived(
                ErrorResponseTo(PostedMessageId("ui.form.requestSubmit"), "HandlerException", "form not ready"));

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual("ui.form.requestSubmit", errors[0].MessageType);
            Assert.AreEqual("HandlerException", errors[0].ErrorType);
            Assert.AreEqual("form not ready", errors[0].ErrorMessage);

            // Support sees it even when the integrator subscribes to nothing.
            Assert.IsTrue(_sink.CapturedExceptions.Exists(ex => ex is PageOperationException),
                "a page rejection must reach telemetry, not just the event");
        }

        [TestMethod]
        public async Task PageAcksNormally_RaisesNoPageError()
        {
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            await _viewer.SendFormRequestSubmitAsync().Within5s();
            _browser.RaiseMessageReceived($@"{{
                ""messageId"": ""resp-ok"",
                ""responseToMessageId"": ""{PostedMessageId("ui.form.requestSubmit")}"",
                ""additionalResponsesExpected"": false,
                ""payload"": {{ ""$type"": ""base"" }}
            }}");

            await Task.Delay(200);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public async Task PageRejectsDisplayQuestionnaire_ReportsTheFailingMessageType()
        {
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            _browser.RaiseMessageReceived(
                ErrorResponseTo(PostedMessageId("sdc.displayQuestionnaire"), "UnknownMessageTypeException", "no handler"));

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual("sdc.displayQuestionnaire", errors[0].MessageType);
        }

        [TestMethod]
        public async Task PageRejectsSdcConfigure_RaisesPageError()
        {
            // sdc.configure carries readOnly / sdcServer / dataServer. Nothing used to
            // register a response handler for it, so a rejection was invisible — and a
            // silently refused configure means a read-only launch painting an editable form.
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);

            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = "https://sdc.example/fhir/r5";
            _viewer.ReadOnly = true;
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            _browser.RaiseMessageReceived(
                ErrorResponseTo(PostedMessageId("sdc.configure"), "HandlerException", "bad config"));

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual("sdc.configure", errors[0].MessageType);
        }

        [TestMethod]
        public async Task AThrowingSubscriber_IsCapturedNotSwallowed()
        {
            // The invoke sits on the inbound listener path, whose exceptions are logged into
            // NullLogger by default. A reporting mechanism must not be able to lose a failure.
            _viewer.PageError += (_, _) => throw new InvalidOperationException("subscriber blew up");

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            await _viewer.SendFormRequestSubmitAsync().Within5s();
            _browser.RaiseMessageReceived(
                ErrorResponseTo(PostedMessageId("ui.form.requestSubmit"), "HandlerException", "nope"));

            await PollFor(
                () => _sink.CapturedExceptions.Exists(ex => ex.Message.Contains("subscriber blew up")),
                TimeSpan.FromSeconds(5));
        }

        [TestMethod]
        public async Task PageError_CarriesTheRejectedMessageId()
        {
            // A host that issued a save-draft and a finalize must be able to tell which failed.
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);

            await DelayUntilBrowserInitialized();
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            await _viewer.SendFormRequestSubmitAsync("save-draft").Within5s();
            var rejected = PostedMessageId("ui.form.requestSubmit");
            _browser.RaiseMessageReceived(ErrorResponseTo(rejected, "HandlerException", "not ready"));

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual(rejected, errors[0].MessageId);
        }

        private async Task DelayUntilBrowserInitialized()
            => await PollFor(() => _browser.Initialized, TimeSpan.FromSeconds(5));

    }
}
