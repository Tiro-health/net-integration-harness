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
    /// The form's right-click menu — the EHR's own entries via <c>ContextMenuItems</c>, and full
    /// control of the menu via <c>BuildContextMenu</c>. The menu itself is Chromium's and can't
    /// be shown in a unit test, so these drive the seam the browser layer calls: the builder the
    /// viewer installs, asked for the menu one click would show.
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

        /// <summary>The builder is installed by the same init task that wires messaging.</summary>
        private async Task Initialized()
            => await PollFor(() => _browser.ContextMenuBuilder != null, TimeSpan.FromSeconds(5));

        private static List<string> Labels(IReadOnlyList<TiroMenuEntry> entries)
            => entries.Select(e => e.Kind == TiroMenuEntryKind.Separator ? "---" : e.Label).ToList();

        // ---- The simple path: ContextMenuItems ------------------------------------------------

        [TestMethod]
        public async Task ItemsAreOfferedInTheOrderTheHostAddedThem()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy patient name", () => { }));
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Copy conclusion", () => { }));

            var shown = _browser.RequestContextMenu();

            CollectionAssert.AreEqual(
                new[] { "Copy patient name", "Copy conclusion" },
                Labels(shown),
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

            // null, not empty. Empty is a deliberate "show nothing", which the render plan
            // honours by clearing the menu — stripping Copy and Paste on the way out.
            Assert.IsNull(shown, "a disposed viewer has no opinion; the browser keeps its own menu");
        }

        [TestMethod]
        public void AnItemRequiresALabelAndAnAction()
        {
            Assert.ThrowsException<ArgumentException>(() => new TiroContextMenuItem("", () => { }));
            Assert.ThrowsException<ArgumentNullException>(
                () => new TiroContextMenuItem("Copy", (Action)null));
        }

        // ---- The default layout, now that the browser's own entries are in the list ------------

        [TestMethod]
        public async Task TheDefaultLayoutPutsTheBrowsersEntriesFirstAndSeparatesTheHosts()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("paste", "&Paste"));
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { }));

            CollectionAssert.AreEqual(
                new[] { "&Copy", "&Paste", "---", "Insert conclusion" },
                Labels(_browser.RequestContextMenu()));
        }

        [TestMethod]
        public async Task NoSeparatorWhenOnlyOneSideHasEntries()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));

            CollectionAssert.AreEqual(new[] { "&Copy" }, Labels(_browser.RequestContextMenu()),
                "a separator with nothing under it is a stray line");

            _browser.BrowserItems.Clear();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { }));

            CollectionAssert.AreEqual(new[] { "Insert conclusion" }, Labels(_browser.RequestContextMenu()),
                "nor with nothing above it");
        }

        // ---- Full control: BuildContextMenu ----------------------------------------------------

        [TestMethod]
        public async Task TheBuilderCanReorderAndHideTheBrowsersOwnEntries()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("inspectElement", "I&nspect"));
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { }));

            _viewer.BuildContextMenu = menu =>
            {
                var result = new List<TiroMenuEntry>(menu.HostItems);
                result.Add(TiroMenuEntry.CreateSeparator());
                result.AddRange(menu.BrowserItems.Where(e => e.Name != "inspectElement"));
                return result;
            };

            CollectionAssert.AreEqual(
                new[] { "Insert conclusion", "---", "&Copy" },
                Labels(_browser.RequestContextMenu()),
                "the builder's list is the menu: its order, and without what it left out");
        }

        [TestMethod]
        public async Task TheBuilderSeesTheClickedContext()
        {
            await Initialized();
            TiroContextMenuContext seen = null;
            _viewer.BuildContextMenu = menu =>
            {
                seen = menu.Context;
                return new List<TiroMenuEntry>();
            };

            _browser.RequestContextMenu(isEditable: false, selectionText: "atrial fibrillation");

            Assert.IsNotNull(seen);
            Assert.IsFalse(seen.IsEditable);
            Assert.AreEqual("atrial fibrillation", seen.SelectionText);
        }

        [TestMethod]
        public async Task HostItemsReachTheBuilderAlreadyFilteredByIsVisible()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Editable only", () => { })
            {
                IsVisible = context => context.IsEditable,
            });
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Always", () => { }));

            var offered = new List<string>();
            _viewer.BuildContextMenu = menu =>
            {
                offered.Clear();
                offered.AddRange(menu.HostItems.Select(e => e.Label));
                return menu.HostItems;
            };

            _browser.RequestContextMenu(isEditable: false);

            CollectionAssert.AreEqual(new[] { "Always" }, offered,
                "the builder chooses the layout; the item's own filter still decides whether it applies");
        }

        [TestMethod]
        public async Task AnEmptyBuilderResultIsAnEmptyMenu_NotTheDefault()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _viewer.BuildContextMenu = _ => new List<TiroMenuEntry>();

            Assert.AreEqual(0, _browser.RequestContextMenu().Count,
                "a builder returning nothing meant nothing, not 'do the usual'");
        }

        [TestMethod]
        public async Task ANullBuilderResultFallsBackToTheDefaultLayout()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { }));
            _viewer.BuildContextMenu = _ => null;

            CollectionAssert.AreEqual(
                new[] { "&Copy", "---", "Insert conclusion" },
                Labels(_browser.RequestContextMenu()),
                "null is 'I have no opinion on this click'");
        }

        [TestMethod]
        public async Task AThrowingBuilderCostsTheCustomisation_NotCopyAndPaste()
        {
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _viewer.BuildContextMenu = _ => throw new InvalidOperationException("bad builder");

            CollectionAssert.AreEqual(new[] { "&Copy" }, Labels(_browser.RequestContextMenu()),
                "a clinician mid-consult needs Copy to keep working even if the EHR's layout code is broken");
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public async Task ABrowserEntryHeldOverFromAnEarlierClickIsDropped()
        {
            // Chromium builds its entries per click; one kept from a previous menu refers to an
            // object it has moved on from. Rendering it would be undefined, so it goes.
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));

            var stale = FakeEmbeddedBrowser.BrowserItem("paste", "&Paste");
            _viewer.BuildContextMenu = menu =>
            {
                var result = new List<TiroMenuEntry>(menu.BrowserItems);
                result.Add(stale);
                return result;
            };

            CollectionAssert.AreEqual(new[] { "&Copy" }, Labels(_browser.RequestContextMenu()));
            Assert.AreEqual(1, _sink.CapturedExceptions.Count,
                "silently dropping it would leave the integrator hunting a menu entry that never appears");
        }

        [TestMethod]
        public async Task ABrowserEntryReturnedTwiceAppearsOnceAndIsReported()
        {
            // One browser item can't occupy two positions in one menu. The browser layer drops
            // the repeat either way; the point of catching it here is that the host hears about
            // it rather than quietly getting a different menu than it asked for.
            await Initialized();
            _browser.BrowserItems.Add(FakeEmbeddedBrowser.BrowserItem("copy", "&Copy"));
            _viewer.BuildContextMenu = menu =>
            {
                var result = new List<TiroMenuEntry>(menu.BrowserItems);
                result.AddRange(menu.BrowserItems);
                return result;
            };

            CollectionAssert.AreEqual(new[] { "&Copy" }, Labels(_browser.RequestContextMenu()));
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public async Task TheBuilderCanWrapAnItemItHoldsItself()
        {
            await Initialized();
            var invoked = false;
            var adHoc = new TiroContextMenuItem("Insert today's date", () => invoked = true);
            _viewer.BuildContextMenu = menu => new List<TiroMenuEntry> { menu.CreateEntry(adHoc) };

            var shown = _browser.RequestContextMenu();

            Assert.AreEqual("Insert today's date", shown[0].Label);
            shown[0].Invoke();
            Assert.IsTrue(invoked, "an item built in the builder gets the same guarded invocation");
        }

        [TestMethod]
        public async Task AnAdHocItemThatThrowsIsStillCaptured()
        {
            await Initialized();
            var adHoc = new TiroContextMenuItem("Broken", () => throw new InvalidOperationException("boom"));
            _viewer.BuildContextMenu = menu => new List<TiroMenuEntry> { menu.CreateEntry(adHoc) };

            _browser.RequestContextMenu()[0].Invoke();

            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        // ---- Enabled state ---------------------------------------------------------------------

        [TestMethod]
        public async Task IsEnabledGreysAHostItemPerClick()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { })
            {
                IsEnabled = context => context.IsEditable,
            });

            Assert.IsTrue(_browser.RequestContextMenu(isEditable: true)[0].IsEnabled);

            var overReadOnly = _browser.RequestContextMenu(isEditable: false);
            Assert.AreEqual(1, overReadOnly.Count, "disabled is shown-but-greyed, not hidden");
            Assert.IsFalse(overReadOnly[0].IsEnabled);
        }

        [TestMethod]
        public async Task AHostItemIsEnabledByDefault()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { }));

            Assert.IsTrue(_browser.RequestContextMenu()[0].IsEnabled);
        }

        [TestMethod]
        public async Task AThrowingEnabledTestLeavesTheItemEnabled()
        {
            await Initialized();
            _viewer.ContextMenuItems.Add(new TiroContextMenuItem("Insert conclusion", () => { })
            {
                IsEnabled = _ => throw new InvalidOperationException("bad test"),
            });

            var shown = _browser.RequestContextMenu();

            Assert.AreEqual(1, shown.Count);
            Assert.IsTrue(shown[0].IsEnabled,
                "an item that reports its own failure beats one silently greyed out");
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public void ABrowserEntryRefusesToBeDisabled()
        {
            // WebView2 reserves IsEnabled for custom items. Hiding is the supported way to take
            // one of its entries out of a menu.
            var native = FakeEmbeddedBrowser.BrowserItem("copy", "&Copy");

            Assert.IsTrue(native.IsFromBrowser);
            Assert.ThrowsException<InvalidOperationException>(() => native.IsEnabled = false);
        }

        [TestMethod]
        public void ASeparatorIsAFreshEntryEachTime()
        {
            // Two separators in one menu must not be the same object: the browser layer renders
            // each from its own pooled native item.
            Assert.AreNotSame(TiroMenuEntry.CreateSeparator(), TiroMenuEntry.CreateSeparator());
            Assert.AreEqual(TiroMenuEntryKind.Separator, TiroMenuEntry.CreateSeparator().Kind);
        }
    }
}
