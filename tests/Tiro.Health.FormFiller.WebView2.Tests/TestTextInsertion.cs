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
    /// <c>InsertTextAsync</c> — the host side of a snippet menu / labelled clipboard living
    /// in the shell's own UI. Unlike the other sends it waits for the page's ack, because the
    /// ack carries the one thing the caller needs: whether there was a field to type into.
    /// A host that can't tell shows a button that silently does nothing.
    /// </summary>
    [TestClass]
    public class TestTextInsertion
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
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { _viewer.Dispose(); } catch { /* not under test */ }
        }

        /// <summary>The bridge's ack for an insert, carrying its outcome as an extension field.</summary>
        private static string AckTo(string requestMessageId, bool inserted) => $@"{{
            ""messageId"": ""resp-{requestMessageId}"",
            ""responseToMessageId"": ""{requestMessageId}"",
            ""additionalResponsesExpected"": false,
            ""payload"": {{
                ""$type"": ""base"",
                ""inserted"": {(inserted ? "true" : "false")}
            }}
        }}";

        /// <summary>A page→host form.submitted envelope, which ends the session.</summary>
        private static string FormSubmitted(string id) => $@"{{
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

        private async Task DisplayForm()
        {
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();
        }

        private string PostedInsert()
        {
            var json = _browser.PostedMessages.FindLast(m => m.Contains("\"messageType\":\"ui.form.insertText\""));
            Assert.IsNotNull(json, $"no ui.form.insertText was posted; saw {_browser.PostedMessages.Count} messages");
            return json;
        }

        /// <summary>
        /// Starts an insert and answers it the way the bridge would. The send has to be in
        /// flight while the ack is raised — InsertTextAsync doesn't return until the ack
        /// arrives, so awaiting it first would deadlock the test.
        /// </summary>
        private async Task<bool> InsertAndAck(string text, bool inserted)
        {
            var insert = _viewer.InsertTextAsync(text);
            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("\"messageType\":\"ui.form.insertText\"")),
                TimeSpan.FromSeconds(5));
            _browser.RaiseMessageReceived(AckTo(JsonProbe.ExtractStringField(PostedInsert(), "messageId"), inserted));
            return await insert.Within5s();
        }

        [TestMethod]
        public async Task InsertTextAsync_PostsTheTextAsATypedPayload()
        {
            await DisplayForm();

            Assert.IsTrue(await InsertAndAck("no acute distress", inserted: true));

            var json = PostedInsert();
            StringAssert.Contains(json, "\"$type\":\"formInsertText\"",
                "the payload discriminator is how the page-side type is identified");
            StringAssert.Contains(json, "\"text\":\"no acute distress\"",
                "camelCase: the bridge reads payload.text");
        }

        [TestMethod]
        public async Task InsertTextAsync_ReturnsFalse_WhenThePageHadNoFieldToTypeInto()
        {
            await DisplayForm();

            Assert.IsFalse(await InsertAndAck("conclusion", inserted: false),
                "the clinician hasn't clicked into a field — the host needs to be able to say so");
        }

        [TestMethod]
        public async Task InsertTextAsync_WithNoText_IsANoOpAndPostsNothing()
        {
            await DisplayForm();
            var before = _browser.PostedMessages.Count;

            Assert.IsFalse(await _viewer.InsertTextAsync(null).Within5s());
            Assert.IsFalse(await _viewer.InsertTextAsync("").Within5s());

            Assert.AreEqual(before, _browser.PostedMessages.Count,
                "an empty insert must not reach the page at all");
        }

        [TestMethod]
        public async Task InsertTextAsync_BeforeAFormIsDisplayed_ThrowsInvalidOperation()
        {
            // There are no fields yet, and waiting would block on a handshake that cannot
            // arrive until SetContextAsync navigates — the same trap as submitting early.
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await _viewer.InsertTextAsync("x"));
        }

        [TestMethod]
        public async Task InsertTextAsync_AfterDispose_ThrowsObjectDisposed()
        {
            _viewer.Dispose();

            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
                async () => await _viewer.InsertTextAsync("x"));
        }

        [TestMethod]
        public async Task InsertTextAsync_AfterSubmit_ThrowsInvalidOperation()
        {
            await DisplayForm();
            _browser.RaiseMessageReceived(FormSubmitted("fs-1"));
            await PollFor(() => _viewer.State == TiroFormViewerState.Submitted, TimeSpan.FromSeconds(5));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await _viewer.InsertTextAsync("x"));
        }

        [TestMethod]
        public async Task InsertTextAsync_SurfacesAPageRejection_AsAPageError_AndReturnsFalse()
        {
            // A throwing page handler answers with an error payload instead of an ack. It must
            // reach the caller as a normal false — a rejection that hung until the 30s deadline
            // would freeze a snippet button on a page that answered immediately.
            var errors = new List<PageErrorEventArgs>();
            _viewer.PageError += (_, e) => errors.Add(e);
            await DisplayForm();

            var insert = _viewer.InsertTextAsync("x");
            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("\"messageType\":\"ui.form.insertText\"")),
                TimeSpan.FromSeconds(5));
            var messageId = JsonProbe.ExtractStringField(PostedInsert(), "messageId");
            _browser.RaiseMessageReceived($@"{{
                ""messageId"": ""resp-1"",
                ""responseToMessageId"": ""{messageId}"",
                ""additionalResponsesExpected"": false,
                ""payload"": {{
                    ""$type"": ""error"",
                    ""errorType"": ""HandlerException"",
                    ""errorMessage"": ""boom""
                }}
            }}");

            Assert.IsFalse(await insert.Within5s(), "a page-side failure is not an insertion");

            await PollFor(() => errors.Count == 1, TimeSpan.FromSeconds(5));
            Assert.AreEqual("ui.form.insertText", errors[0].MessageType);
            Assert.AreEqual("HandlerException", errors[0].ErrorType);
        }
    }
}
