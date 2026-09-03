using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <c>ContextMenuItems</c> — the EHR's own entries in the form's right-click menu. The menu
    /// itself is Chromium's and can't be shown in a unit test, so these drive the seam the
    /// browser layer calls: the provider the viewer installs, asked for the items one click
    /// would show.
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

        /// <summary>The provider is installed by the same init task that wires messaging.</summary>
        private async Task Initialized()
            => await PollFor(() => _browser.ContextMenuItemsProvider != null, TimeSpan.FromSeconds(5));

        [TestMethod]
        public async Task ItemsAreOfferedInTheOrderTheHostAddedThem()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy patient name", () => { }));
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy conclusion", () => { }));

            var shown = _browser.RequestContextMenu();

            CollectionAssert.AreEqual(
                new[] { "Copy patient name", "Copy conclusion" },
                new List<string>(shown.Select(i => i.Label)),
                "the menu is the host's list, in the host's order");
        }

        [TestMethod]
        public async Task AnEmptyListShowsNothing()
        {
            await Initialized();

            Assert.AreEqual(0, _browser.RequestContextMenu().Count,
                "a host that configured no items must not get a stray separator or empty entry");
        }

        [TestMethod]
        public async Task TheListIsReadAtMenuTime_NotAtStartup()
        {
            // The whole point of a collection over a constructor argument: the EHR fills it from
            // its own config, and refills it when the patient changes.
            await Initialized();
            Assert.AreEqual(0, _browser.RequestContextMenu().Count);

            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy patient name", () => { }));
            Assert.AreEqual(1, _browser.RequestContextMenu().Count);

            _viewer.ContextMenuItems.Clear();
            Assert.AreEqual(0, _browser.RequestContextMenu().Count);
        }

        [TestMethod]
        public async Task IsVisibleFiltersPerClick()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy conclusion", () => { })
            {
                IsVisible = context => context.IsEditable,
            });

            Assert.AreEqual(1, _browser.RequestContextMenu(isEditable: true).Count);
            Assert.AreEqual(0, _browser.RequestContextMenu(isEditable: false).Count,
                "an item filtered to editable targets must not appear over a read-only one");
        }

        [TestMethod]
        public async Task TheClickedContextReachesTheAction()
        {
            await Initialized();
            TiroContextMenuContext seen = null;
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Look up selection", context => seen = context));

            _browser.RequestContextMenu(isEditable: false, selectionText: "atrial fibrillation")[0].Invoke();

            Assert.IsNotNull(seen);
            Assert.IsFalse(seen.IsEditable);
            Assert.AreEqual("atrial fibrillation", seen.SelectionText);
        }

        [TestMethod]
        public async Task AThrowingActionIsCapturedRatherThanEscapingIntoTheMenuDispatch()
        {
            // There is no caller to report to: this runs inside the browser's own menu
            // callback, where a throw becomes an unhandled exception on the message pump.
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem(
                "Copy conclusion", () => throw new InvalidOperationException("no conclusion yet")));

            _browser.RequestContextMenu()[0].Invoke();

            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
            Assert.IsInstanceOfType(_sink.CapturedExceptions[0], typeof(InvalidOperationException));
        }

        [TestMethod]
        public async Task AThrowingVisibilityTestCostsItsOwnItem_NotTheMenu()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Broken", () => { })
            {
                IsVisible = _ => throw new InvalidOperationException("bad filter"),
            });
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy patient name", () => { }));

            var shown = _browser.RequestContextMenu();

            Assert.AreEqual(1, shown.Count);
            Assert.AreEqual("Copy patient name", shown[0].Label);
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public async Task NothingIsOfferedAfterDispose()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy patient name", () => { }));
            _viewer.Dispose();

            var shown = _browser.RequestContextMenu();

            Assert.AreEqual(0, shown == null ? 0 : shown.Count,
                "a disposed viewer must not answer a menu request");
        }

        [TestMethod]
        public async Task AFaultedAsyncActionIsCaptured_NotLeftUnobserved()
        {
            // The shape an InsertTextAsync item has: the task outlives the menu, so dropping it
            // would leave a failure with no route to telemetry at all.
            await Initialized();
            var insert = new TaskCompletionSource<bool>();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Paste conclusion", _ => insert.Task));

            _browser.RequestContextMenu()[0].Invoke();
            Assert.AreEqual(0, _sink.CapturedExceptions.Count, "nothing has failed yet");

            insert.SetException(new InvalidOperationException("the page never answered"));

            await PollFor(() => _sink.CapturedExceptions.Count == 1, TimeSpan.FromSeconds(5));
        }

        [TestMethod]
        public async Task ASucceedingAsyncActionCapturesNothing()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(
                new TiroContextMenuItem("Paste conclusion", _ => Task.FromResult(true)));

            _browser.RequestContextMenu()[0].Invoke();

            Assert.AreEqual(0, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public void AnItemRequiresALabelAndAnAction()
        {
            Assert.ThrowsException<ArgumentException>(() => new TiroContextMenuItem("", () => { }));
            Assert.ThrowsException<ArgumentNullException>(
                () => new TiroContextMenuItem("Copy", (Action)null));
            Assert.ThrowsException<ArgumentNullException>(
                () => new TiroContextMenuItem("Paste", (Func<TiroContextMenuContext, Task>)null));
            Assert.ThrowsException<ArgumentNullException>(
                () => TiroContextMenuItem.CopyToClipboard("Copy", null));
        }
    }
}
