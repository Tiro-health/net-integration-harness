using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using Tiro.Health.SmartWebMessaging;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// NavigateToContent must roll back its once-flag when navigation fails, so a
    /// retried SetContextAsync re-attempts and surfaces the real cause — not a
    /// misleading 30s handshake timeout.
    /// </summary>
    [TestClass]
    public class TestNavigationRetry
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

        [TestMethod]
        public async Task FailedNavigation_RetriedSetContext_NavigatesAgain()
        {
            _browser.ThrowOnNextMapVirtualHost = new IOException("temp folder blocked");

            var first = _viewer.SetContextAsync("http://example.org/q");
            await Assert.ThrowsExceptionAsync<IOException>(
                () => first.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
            Assert.AreEqual(0, _browser.NavigatedUrls.Count);

            var second = _viewer.SetContextAsync("http://example.org/q");
            await Task.Yield();
            _browser.RaiseMessageReceived(@"{
                ""messageId"": ""hs-1"",
                ""messagingHandle"": ""smart-web-messaging"",
                ""messageType"": ""status.handshake"",
                ""payload"": {}
            }");
            await second.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            Assert.AreEqual(1, _browser.NavigatedUrls.Count,
                "The retry must re-attempt navigation after the rolled-back failure.");
        }
    }
}
