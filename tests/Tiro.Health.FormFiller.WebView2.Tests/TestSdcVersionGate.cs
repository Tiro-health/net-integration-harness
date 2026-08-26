using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using Tiro.Health.FormSdk.Abstractions;
using Tiro.Health.SmartWebMessaging;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The SDC server version gate on the viewer (GH-62). With the web-sdk embedded (GH-60),
    /// the SDC server is the only component that can change underneath a frozen harness —
    /// customers run and upgrade their own instance. This is the check that turns a wrong
    /// pairing into a startup refusal rather than a form that renders and then misbehaves.
    /// </summary>
    [TestClass]
    public class TestSdcVersionGate
    {
        private const string SdcEndpoint = "https://sdc.example.org/fhir/r5";

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

        private static SdcVersionCheckResult TooOld()
        {
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);
            var older = patch > 0 ? $"v{major}.{minor}.{patch - 1}"
                      : minor > 0 ? $"v{major}.{minor - 1}.999"
                      : $"v{major - 1}.999.999";
            return SdcVersionCheckResult.FromReportedVersion(older, SdcVersionCheckResult.CapabilityStatementSource);
        }

        private static SdcVersionCheckResult Unknown() =>
            SdcVersionCheckResult.Unavailable("GET https://sdc.example.org/fhir/r5/metadata → timed out after 3000 ms.");

        private async Task DelayUntilBrowserInitialized()
            => await SwmTest.PollFor(() => _browser.Initialized, TimeSpan.FromSeconds(5));

        [TestMethod]
        public async Task TooOldServer_RefusesTheSession_AndNothingReachesThePage()
        {
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckToReturn = TooOld();

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));

            var ex = await Assert.ThrowsExceptionAsync<SdcServerTooOldException>(() => setContext.Within5s());
            Assert.AreEqual(SdcCompatibility.MinimumSdcVersion, ex.MinimumVersion);

            // Fail closed means the form is never configured or displayed against that server.
            Assert.IsFalse(_browser.PostedMessages.Any(m => m.Contains("sdc.configure")),
                "A refused pairing must not send sdc.configure.");
            Assert.IsFalse(_browser.PostedMessages.Any(m => m.Contains("sdc.displayQuestionnaire")),
                "A refused pairing must not display the questionnaire.");
            Assert.AreNotEqual(TiroFormViewerState.ContextSet, _viewer.State,
                "The session was refused, so the viewer must not report a context.");
        }

        [TestMethod]
        public async Task TooOldServer_IsCapturedToTelemetry_WithBothVersionsNamed()
        {
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckToReturn = TooOld();

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await Assert.ThrowsExceptionAsync<SdcServerTooOldException>(() => setContext.Within5s());

            // SetContextAsync's own catch captures it; this asserts the refusal is diagnosable
            // in the customer's Sentry (they self-host the server, so it lands in their project).
            Assert.IsTrue(_sink.CapturedExceptions.OfType<SdcServerTooOldException>().Any(),
                "The refusal must reach telemetry, not just the caller.");
            // Breadcrumbs is a value-tuple list, so an absent entry comes back as (null, null).
            var breadcrumb = _sink.Sessions[0].Breadcrumbs
                .FirstOrDefault(b => b.Category == "sdc.version");
            Assert.IsNotNull(breadcrumb.Message, "The verdict must be breadcrumbed on the session.");
            StringAssert.Contains(breadcrumb.Message, SdcCompatibility.MinimumSdcVersion);
        }

        [TestMethod]
        public async Task UnknownVersion_FailsOpen_AndTheSessionProceeds()
        {
            // A network blip, a server predating both version routes, or a dev build. None of
            // those may brick a working deployment — the check is a guard, not a gatekeeper.
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckToReturn = Unknown();

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
            Assert.IsTrue(_browser.PostedMessages.Any(m => m.Contains("sdc.displayQuestionnaire")));
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, _viewer.SdcServerVersionCheck!.Outcome);
            Assert.IsTrue(_sink.Sessions[0].Breadcrumbs.Any(b => b.Category == "sdc.version"),
                "Failing open still has to be loud: the verdict is breadcrumbed either way.");
        }

        [TestMethod]
        public async Task TheProbeTargetsTheConfiguredSdcEndpointAddress()
        {
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            // The property the page is configured from is the one that gets checked — the two
            // cannot be allowed to diverge.
            Assert.AreEqual(new Uri(SdcEndpoint), _viewer.LastSdcVersionCheckAddress);
        }

        [TestMethod]
        public async Task NoSdcEndpointAddress_SkipsTheCheckEntirely()
        {
            // The base viewer seeds no endpoint (only the closed R5/R4 bindings do). With
            // nothing configured there is no server to ask, and nothing to refuse.
            await DelayUntilBrowserInitialized();

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.AreEqual(0, _viewer.SdcVersionCheckCount);
            Assert.IsNull(_viewer.SdcServerVersionCheck);
        }

        [TestMethod]
        public async Task AnEndpointAddressThatIsNotAUri_SkipsTheCheck_RatherThanFailingTheLaunch()
        {
            // Rejecting a malformed address is not this check's job; the page will fail on it
            // with a better message. The check must not be the thing that throws.
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = "sdc.example.org/fhir/r5"; // no scheme

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.AreEqual(0, _viewer.SdcVersionCheckCount);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task ARetriedSetContext_ReusesTheFirstProbe()
        {
            // SetContextAsync is retryable after a failure (see TestNavigationRetry). The
            // version check is a startup check, so the retry must not re-ask the server.
            _browser.ThrowOnNextMapVirtualHost = new IOException("temp folder blocked");
            _viewer.SdcEndpointAddress = SdcEndpoint;

            var first = _viewer.SetContextAsync("http://example.org/q");
            await Assert.ThrowsExceptionAsync<IOException>(() => first.Within5s());

            var second = _viewer.SetContextAsync("http://example.org/q");
            await Task.Yield();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await second.Within5s();

            Assert.AreEqual(1, _viewer.SdcVersionCheckCount,
                "The probe result is cached across retries — one check per viewer, not per attempt.");
        }
    }
}
