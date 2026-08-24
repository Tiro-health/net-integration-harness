using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
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

        /// <summary>The messageId of the last request the viewer posted to the page.</summary>
        private string LastPostedMessageId()
        {
            var json = _browser.PostedMessages[_browser.PostedMessages.Count - 1];
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
                ErrorResponseTo(LastPostedMessageId(), "HandlerException", "form not ready"));

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
                ""responseToMessageId"": ""{LastPostedMessageId()}"",
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

            // The display request is the last thing SetContextAsync posted.
            _browser.RaiseMessageReceived(
                ErrorResponseTo(LastPostedMessageId(), "UnknownMessageTypeException", "no handler"));

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual("sdc.displayQuestionnaire", errors[0].MessageType);
        }

        private async Task DelayUntilBrowserInitialized()
            => await PollFor(() => _browser.Initialized, TimeSpan.FromSeconds(5));

        private static async Task PollFor(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.Fail($"Condition did not become true within {timeout}.");
        }
    }
}
