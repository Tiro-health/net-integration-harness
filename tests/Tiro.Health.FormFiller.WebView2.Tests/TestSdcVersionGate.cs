using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    /// The SDC server version check on the viewer (GH-62). With the web-sdk embedded (GH-60),
    /// the SDC server is the only component that can change underneath a frozen harness —
    /// customers run and upgrade their own instance. The check establishes its version on the
    /// path to the first form and reports it; nothing is refused yet, and the note at the end of
    /// ApplySdcVersionCheckAsync says what to add and when.
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
            return SdcVersionCheckResult.FromReportedVersion(older);
        }

        private static SdcVersionCheckResult Unknown() =>
            SdcVersionCheckResult.Unavailable("GET https://sdc.example.org/fhir/r5/metadata → timed out after 3000 ms.");

        private async Task DelayUntilBrowserInitialized()
            => await SwmTest.PollFor(() => _browser.Initialized, TimeSpan.FromSeconds(5));

        [TestMethod]
        public async Task TooOldServer_IsReportedLoudly_AndTheSessionProceeds()
        {
            // Reported, not refused. The floor is currently the first server version that can
            // answer the probe at all, so a refusal here could only ever fire on a mistake — and
            // enforcement would reach an integrator in the same release as any raised floor, so
            // fielding it early protects nobody. See the note at the end of the method.
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckToReturn = TooOld();

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
            Assert.IsTrue(_browser.PostedMessages.Any(m => m.Contains("sdc.displayQuestionnaire")),
                "A too-old server is a warning; the form still gets displayed.");
            Assert.AreEqual(SdcVersionCheckOutcome.TooOld, _viewer.SdcServerVersionCheck!.Outcome);

            // Loud where it counts: a captured message reaches the customer's own Sentry even
            // when nothing else in the session goes wrong, which is exactly this case.
            Assert.IsTrue(
                _sink.CapturedMessages.Exists(m => m.Contains("is older than the minimum")),
                "A too-old server must be captured to telemetry, and say what to do about it. Captured: "
                + string.Join(" | ", _sink.CapturedMessages));
            StringAssert.Contains(
                _sink.CapturedMessages.Find(m => m.Contains("is older than the minimum")),
                SdcCompatibility.MinimumSdcVersion,
                "Both versions have to be named, or the warning is not actionable.");

            // Breadcrumbs is a value-tuple list, so an absent entry comes back as (null, null).
            var breadcrumb = _sink.Sessions[0].Breadcrumbs
                .FirstOrDefault(b => b.Category == "sdc.version");
            Assert.IsNotNull(breadcrumb.Message, "The verdict must be breadcrumbed on the session too.");
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
            // A breadcrumb only travels if something else in the session is captured, and on a
            // healthy-but-disarmed deployment nothing ever is — so the fail-open is also captured
            // as a message, which is the channel that actually reaches the customer's Sentry.
            Assert.IsTrue(_sink.CapturedMessages.Exists(m => m.Contains("could not be established")),
                "A fail-open must reach telemetry as a message, not only as a breadcrumb. Captured: "
                + string.Join(" | ", _sink.CapturedMessages));
        }

        [TestMethod]
        public async Task AProbeThatEndsCancelled_FailsOpen_RatherThanRefusingTheLaunch()
        {
            // What the launch budget expiring looks like from inside ApplySdcVersionCheckAsync:
            // the wait ends cancelled while neither the caller nor the viewer's lifetime was
            // cancelled. That case used to rethrow a bare OperationCanceledException, which
            // SetContextAsync's own OCE filter does not match — so it fell into the generic
            // catch and threw a message-less cancellation at the host. "The SDC server is
            // unreachable" became a refused launch: the exact opposite of the documented
            // fail-open.
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                _viewer.SdcVersionCheckTaskToReturn =
                    Task.FromCanceled<SdcVersionCheckResult>(alreadyCancelled.Token);

                var setContext = _viewer.SetContextAsync("http://example.org/q");
                _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
                await setContext.Within5s();
            }

            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State,
                "A version check that never answered must not refuse the session.");
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, _viewer.SdcServerVersionCheck!.Outcome);
        }

        [TestMethod]
        public async Task AProbeThatThrows_FailsOpen_RatherThanRefusingTheLaunch()
        {
            // SdcServerVersionProbe's contract is that a server or transport problem is a
            // result, not an exception — so a throw here means the check itself is broken. It
            // still must not become a new way for a form launch to die.
            await DelayUntilBrowserInitialized();
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckTaskToReturn =
                Task.FromException<SdcVersionCheckResult>(new InvalidOperationException("probe is broken"));

            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await setContext.Within5s();

            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, _viewer.SdcServerVersionCheck!.Outcome);
            StringAssert.Contains(_viewer.SdcServerVersionCheck!.Detail, "probe is broken");
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
        public async Task ARetryAgainstADifferentEndpoint_ProbesTheNewOne()
        {
            // SetContextAsync is retryable, and a host may fix SdcEndpointAddress between
            // attempts. Caching on "have we probed at all" meant the retry applied the FIRST
            // address's verdict while sdc.configure sent the SECOND to the page — refusing a
            // launch against a good server in the name of one nobody was talking to. That is the
            // mis-attribution this whole check exists to avoid, reintroduced through the cache.
            _browser.ThrowOnNextMapVirtualHost = new IOException("temp folder blocked");
            _viewer.SdcEndpointAddress = "https://stale.example.org/fhir/r5";
            _viewer.SdcVersionCheckToReturn = TooOld();

            var first = _viewer.SetContextAsync("http://example.org/q");
            await Assert.ThrowsExceptionAsync<IOException>(() => first.Within5s());

            // Host corrects the endpoint and retries; the new server is fine.
            _viewer.SdcEndpointAddress = SdcEndpoint;
            _viewer.SdcVersionCheckToReturn = SdcVersionCheckResult.FromReportedVersion(SdcCompatibility.MinimumSdcVersion);

            var second = _viewer.SetContextAsync("http://example.org/q");
            await Task.Yield();
            _browser.RaiseMessageReceived(SwmTest.Handshake("hs-1"));
            await second.Within5s();

            Assert.AreEqual(2, _viewer.SdcVersionCheckCount,
                "A changed endpoint must be re-probed, not answered from the previous address's verdict.");
            Assert.AreEqual(new Uri(SdcEndpoint), _viewer.LastSdcVersionCheckAddress);
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task ARetriedSetContext_AgainstTheSameEndpoint_ReusesTheFirstProbe()
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
                "The probe is cached per address — one check per attempt-series, not per attempt.");
        }
    }
}
