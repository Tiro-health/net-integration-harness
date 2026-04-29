using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
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
            // The receive transaction is finished by OnFormSubmitted with the outcome status;
            // the trailing OnBrowserMessageReceived Finish(Ok) is a no-op (idempotency).
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
