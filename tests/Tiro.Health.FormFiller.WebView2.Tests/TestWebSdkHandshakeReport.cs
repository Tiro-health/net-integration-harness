using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The page reports at handshake how the web-sdk got there (<c>source</c>) and which
    /// version it is running. <c>source</c> is the guard: the session is refused when the
    /// page loaded its own copy, or when ours failed to load. The version is recorded for
    /// diagnostics and deliberately not asserted — the served URL carries it, so a stale
    /// bundle cannot load (see build/web-sdk/README.md).
    /// </summary>
    [TestClass]
    public class TestWebSdkHandshakeReport
    {
        private FakeEmbeddedBrowser _browser = null!;
        private TestableTiroFormViewer _viewer = null!;

        [TestInitialize]
        public void Init()
        {
            _browser = new FakeEmbeddedBrowser();
            _viewer = new TestableTiroFormViewer(_browser, new R5.SmartMessageHandler(), new FakeTelemetrySink());
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { _viewer.Dispose(); } catch { /* not under test */ }
        }

        private static string ClientPayload(string version, string source = "embedded") =>
            $@"{{ ""client"": {{ ""name"": ""tiro-web-sdk"", ""version"": {(version == null ? "null" : $"\"{version}\"")}, ""source"": ""{source}"" }} }}";

        private async Task<Task> StartSetContextAsync()
        {
            var task = _viewer.SetContextAsync("http://example.org/q");
            // Let SetContextAsync get past init and start awaiting the handshake.
            await Task.Yield();
            return task;
        }

        [TestMethod]
        public async Task EmbeddedSource_SessionProceeds_AndTheVersionIsRecorded()
        {
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.3.2")));
            await setContext.Within5s();

            Assert.AreEqual("0.3.2", _viewer.PageWebSdkVersion);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task AnyReportedVersion_IsAccepted_BecauseTheUrlCarriesTheVersion()
        {
            // A version unlike the embedded one is NOT a refusal: the URL is what prevents a
            // stale bundle, and an armed equality assert would only add a way to refuse a
            // working session.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.2.1")));
            await setContext.Within5s();

            Assert.AreEqual("0.2.1", _viewer.PageWebSdkVersion);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task LegacyEmptyPayload_SessionProceeds_WithNoVersion()
        {
            // An SDK predating the static version field (atticus-frontend#2927) reports none.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.IsNull(_viewer.PageWebSdkVersion);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task CollisionSource_RefusesTheSession()
        {
            // The page loaded its own SDK copy: the pairing is unvalidated, so refuse.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.3.2", source: "collision")));

            await Assert.ThrowsExceptionAsync<WebSdkLoadException>(() => setContext.Within5s());
            Assert.AreEqual(TiroFormViewerState.Initializing, _viewer.State,
                "a refused handshake must not advance the state machine");
        }

        [TestMethod]
        public async Task ErrorSource_RefusesTheSession()
        {
            // Our bundle never loaded, so the form can never render; fail, don't blank.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload(null, source: "error")));

            await Assert.ThrowsExceptionAsync<WebSdkLoadException>(() => setContext.Within5s());
        }

        [TestMethod]
        public async Task RefusedHandshake_AcksWithErrorResponse_NotSuccess()
        {
            // The page must see an error ack (tiro-disconnected), never tiro-connected.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.3.2", source: "collision")));
            try { await setContext.Within5s(); }
            catch (WebSdkLoadException) { /* expected */ }

            var ack = _browser.PostedMessages.Find(m => m.Contains("hs-1"));
            Assert.IsNotNull(ack, "The handshake must still be answered.");
            StringAssert.Contains(ack, "\"error\"");
        }

        [TestMethod]
        public async Task LateCollision_AfterASuccessfulHandshake_FailsSubsequentOperations()
        {
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.3.2")));
            await setContext.Within5s();

            // A page reload that swapped the SDK cannot fault the one-shot handshake TCS,
            // but must still fail everything after it.
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-2", ClientPayload("0.2.1", source: "collision")));

            await Assert.ThrowsExceptionAsync<WebSdkLoadException>(
                () => _viewer.SendFormRequestSubmitAsync());
        }
    }
}
