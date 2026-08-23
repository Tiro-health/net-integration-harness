using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// GH-61: the handshake reports the element's version and how the SDK loaded.
    /// Collision/load-error refuse the session unconditionally; a version mismatch
    /// refuses when the expected version is armed. Refusal is terminal and loud.
    /// </summary>
    [TestClass]
    public class TestWebSdkVersionAssert
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
        public async Task ArmedAndMatching_SessionProceeds()
        {
            _viewer.ExpectedWebSdkVersionOverride = "0.4.0";

            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.4.0")));
            await setContext.Within5s();

            Assert.AreEqual("0.4.0", _viewer.PageWebSdkVersion);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task ArmedAndDifferent_SetContextThrowsMismatch()
        {
            _viewer.ExpectedWebSdkVersionOverride = "0.4.0";

            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.2.1")));

            await Assert.ThrowsExceptionAsync<WebSdkVersionMismatchException>(() => setContext.Within5s());
            Assert.AreEqual(TiroFormViewerState.Initializing, _viewer.State,
                "A rejected handshake must not advance the state machine.");
        }

        [TestMethod]
        public async Task ArmedAndNoVersionReported_SetContextThrowsMismatch()
        {
            _viewer.ExpectedWebSdkVersionOverride = "0.4.0";

            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload(null)));

            await Assert.ThrowsExceptionAsync<WebSdkVersionMismatchException>(() => setContext.Within5s());
        }

        [TestMethod]
        public async Task ArmedAndLegacyEmptyPayload_SetContextThrowsMismatch()
        {
            _viewer.ExpectedWebSdkVersionOverride = "0.4.0";

            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));

            await Assert.ThrowsExceptionAsync<WebSdkVersionMismatchException>(() => setContext.Within5s());
        }

        [TestMethod]
        public async Task CollisionSource_RefusesSession_EvenUnarmed()
        {
            // Page loaded its own SDK copy — refused regardless of version arming.
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.3.2", source: "collision")));

            await Assert.ThrowsExceptionAsync<WebSdkLoadException>(() => setContext.Within5s());
        }

        [TestMethod]
        public async Task ErrorSource_RefusesSession_EvenUnarmed()
        {
            // Embedded SDK failed to load — the form can never render; fail, don't blank.
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
        public async Task LateMismatch_AfterSuccessfulHandshake_FailsSubsequentOperations()
        {
            _viewer.ExpectedWebSdkVersionOverride = "0.4.0";

            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.4.0")));
            await setContext.Within5s();

            // A second, mismatching handshake (page reload with a foreign bundle) can't
            // fault the one-shot TCS but must still fail everything after it.
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-2", ClientPayload("0.2.1")));

            await Assert.ThrowsExceptionAsync<WebSdkVersionMismatchException>(
                () => _viewer.SendFormRequestSubmitAsync());
        }

        [TestMethod]
        public async Task Unarmed_VersionIsRecordedButNotAsserted()
        {
            // Default override is null — the assert is unarmed (pinned SDK predates #2927).
            var setContext = await StartSetContextAsync();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1", ClientPayload("0.2.1")));
            await setContext.Within5s();

            Assert.AreEqual("0.2.1", _viewer.PageWebSdkVersion);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }
    }
}
