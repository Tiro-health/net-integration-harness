using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The form's right-click menu, which the EHR takes over through <c>ContextMenuOpening</c>.
    /// Chromium's menu can't be shown in a unit test, so these drive the seam the browser layer
    /// calls: the interceptor the viewer installs, asked whether one click is taken over.
    /// </summary>
    [TestClass]
    public class TestContextMenu
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

        /// <summary>The interceptor is installed by the same init task that wires messaging.</summary>
        private async Task Initialized()
            => await PollFor(() => _browser.ContextMenuInterceptor != null, TimeSpan.FromSeconds(5));

        [TestMethod]
        public async Task WithoutAHandlerTheBrowserKeepsItsMenu()
        {
            await Initialized();

            Assert.IsFalse(_browser.InterceptContextMenu());
        }

        [TestMethod]
        public async Task AnUnhandledEventLeavesTheBrowsersMenu()
        {
            await Initialized();
            var raised = 0;
            _viewer.ContextMenuOpening += (s, e) => raised++;

            Assert.IsFalse(_browser.InterceptContextMenu());
            Assert.AreEqual(1, raised);
        }

        [TestMethod]
        public async Task AHandledEventSuppressesTheBrowsersMenu()
        {
            await Initialized();
            TiroContextMenuOpeningEventArgs seen = null;
            _viewer.ContextMenuOpening += (s, e) => { seen = e; e.Handled = true; };

            Assert.IsTrue(_browser.InterceptContextMenu(isEditable: false, selectionText: "dyspnoea", x: 40, y: 25));

            Assert.IsFalse(seen.Context.IsEditable);
            Assert.AreEqual("dyspnoea", seen.Context.SelectionText);
            Assert.AreEqual(new System.Drawing.Point(40, 25), seen.Location,
                "without window handles the browser's coordinates pass through unchanged");
        }

        [TestMethod]
        public async Task AThrowingHandlerIsReportedAndTheBrowsersMenuShown()
        {
            await Initialized();
            _viewer.ContextMenuOpening += (s, e) => throw new InvalidOperationException("broken handler");

            Assert.IsFalse(_browser.InterceptContextMenu(), "Copy and Paste survive a broken handler");
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public async Task AHandlerThatThrowsAfterHandlingKeepsItsMenu()
        {
            await Initialized();
            _viewer.ContextMenuOpening += (s, e) =>
            {
                e.Handled = true;
                throw new InvalidOperationException("failed after showing its menu");
            };

            Assert.IsTrue(_browser.InterceptContextMenu(),
                "the host's menu may already be open; the browser's must not appear on top of it");
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public async Task ADisposedViewerNeverIntercepts()
        {
            await Initialized();
            _viewer.ContextMenuOpening += (s, e) => e.Handled = true;
            var intercept = _browser.ContextMenuInterceptor;

            _viewer.Dispose();

            Assert.IsFalse(intercept(new TiroContextMenuContext(true, null), System.Drawing.Point.Empty));
        }
    }
}
