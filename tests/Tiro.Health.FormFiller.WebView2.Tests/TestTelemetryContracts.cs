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
            Assert.IsTrue(configureSpan.Finished, "sdc.configure is fire-and-forget; the span should finish synchronously.");
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
