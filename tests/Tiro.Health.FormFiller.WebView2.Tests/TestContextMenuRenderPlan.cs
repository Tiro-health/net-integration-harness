using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plan = Tiro.Health.FormFiller.WebView2.ContextMenuRenderPlan;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The reconcile between a composed menu and the embedded browser's item collection — what
    /// gets removed, which cached object a repeated label maps to, and whether the browser's own
    /// entries can be left where they are. Previously reachable only through a live Chromium,
    /// and so untested.
    /// </summary>
    /// <remarks>
    /// Assertions are on the <em>rendered menu</em>, not on the plan's steps. In the append-only
    /// shape the browser's items are deliberately absent from the steps — they are already in
    /// place — so counting steps would measure the encoding rather than what the clinician sees.
    /// <see cref="Render"/> mirrors <c>WebView2EmbeddedBrowser.Render</c> exactly.
    /// </remarks>
    [TestClass]
    public class TestContextMenuRenderPlan
    {
        /// <summary>Stands in for one of the browser's own items, named so a menu is readable.</summary>
        private sealed class NamedHandle
        {
            private readonly string _name;
            public NamedHandle(string name) { _name = name; }
            public override string ToString() => _name;
        }

        private static object Handle(string name) => new NamedHandle(name);

        private static TiroMenuEntry Browser(object handle, string name = "copy")
            => new TiroMenuEntry(name, handle.ToString(), TiroMenuEntryKind.Command, true, handle);

        private static TiroMenuEntry Host(string label, bool isEnabled = true, Action invoke = null)
            => new TiroMenuEntry(label, invoke ?? (() => { }), isEnabled);

        /// <summary>
        /// Applies a plan the way the browser layer does: start from the items already in the
        /// collection, clear them if the plan says so, then append each step.
        /// </summary>
        private static List<string> Render(Plan plan, IReadOnlyList<object> alreadyPresent)
        {
            var menu = alreadyPresent.Select(h => h.ToString()).ToList();
            if (plan.ClearExisting) menu.Clear();

            foreach (var step in plan.Steps)
            {
                if (step.Kind == Plan.StepKind.Separator) menu.Add("---");
                else if (step.Kind == Plan.StepKind.BrowserItem) menu.Add(step.BrowserHandle.ToString());
                else menu.Add("host:" + step.Label + "#" + step.Occurrence);
            }
            return menu;
        }

        private static List<string> Menu(IReadOnlyList<TiroMenuEntry> composed, params object[] handles)
            => Render(Plan.Create(composed, handles), handles);

        // ---- The append-only fast path ---------------------------------------------------------

        [TestMethod]
        public void TheDefaultShapeNeedsNoRemovals()
        {
            // Clearing and re-adding the browser's items is the riskiest operation in the file,
            // so the common case must not reach it.
            object copy = Handle("Copy"), paste = Handle("Paste");
            var composed = new List<TiroMenuEntry>
            {
                Browser(copy), Browser(paste), TiroMenuEntry.CreateSeparator(), Host("Insert"),
            };

            var plan = Plan.Create(composed, new[] { copy, paste });

            Assert.IsFalse(plan.ClearExisting);
            CollectionAssert.AreEqual(
                new[] { "Copy", "Paste", "---", "host:Insert#0" }, Render(plan, new[] { copy, paste }));
        }

        [TestMethod]
        public void ReorderingForcesARebuild()
        {
            object copy = Handle("Copy"), paste = Handle("Paste");
            var composed = new List<TiroMenuEntry> { Host("Insert"), Browser(copy), Browser(paste) };

            var plan = Plan.Create(composed, new[] { copy, paste });

            Assert.IsTrue(plan.ClearExisting);
            CollectionAssert.AreEqual(
                new[] { "host:Insert#0", "Copy", "Paste" }, Render(plan, new[] { copy, paste }));
        }

        [TestMethod]
        public void DroppingABrowserItemRemovesItFromTheMenu()
        {
            object copy = Handle("Copy"), inspect = Handle("Inspect");

            var plan = Plan.Create(new List<TiroMenuEntry> { Browser(copy) }, new[] { copy, inspect });

            Assert.IsTrue(plan.ClearExisting, "leaving one out cannot be expressed as an append");
            CollectionAssert.AreEqual(new[] { "Copy" }, Render(plan, new[] { copy, inspect }));
        }

        [TestMethod]
        public void SwappingTwoBrowserItemsForcesARebuild()
        {
            object copy = Handle("Copy"), paste = Handle("Paste");

            var plan = Plan.Create(new List<TiroMenuEntry> { Browser(paste), Browser(copy) }, new[] { copy, paste });

            Assert.IsTrue(plan.ClearExisting);
            CollectionAssert.AreEqual(new[] { "Paste", "Copy" }, Render(plan, new[] { copy, paste }));
        }

        [TestMethod]
        public void AHostEntryBeforeTheBrowsersForcesARebuild()
        {
            // The subtle case: same count, browser item present, but not at index 0.
            object copy = Handle("Copy");

            var plan = Plan.Create(new List<TiroMenuEntry> { Host("Insert"), Browser(copy) }, new[] { copy });

            Assert.IsTrue(plan.ClearExisting);
            CollectionAssert.AreEqual(new[] { "host:Insert#0", "Copy" }, Render(plan, new[] { copy }));
        }

        // ---- Rejecting what cannot be rendered --------------------------------------------------

        [TestMethod]
        public void AnEntryFromAnEarlierClickIsDropped()
        {
            // Chromium builds its items per click; a handle held over from a previous menu
            // refers to an object it has moved on from.
            object current = Handle("Copy"), stale = Handle("Stale");

            CollectionAssert.AreEqual(
                new[] { "Copy" },
                Menu(new List<TiroMenuEntry> { Browser(current), Browser(stale) }, current));
        }

        [TestMethod]
        public void TheSameBrowserItemTwiceAppearsOnce()
        {
            // One native object cannot occupy two positions in one menu; adding it twice is not
            // a supported operation, and the throw would land in the message pump.
            object copy = Handle("Copy");
            var entry = Browser(copy);

            CollectionAssert.AreEqual(
                new[] { "Copy" }, Menu(new List<TiroMenuEntry> { entry, entry }, copy));
        }

        [TestMethod]
        public void TwoDistinctEntriesSharingOneHandleAppearOnce()
        {
            // Same trap reached a different way: two TiroMenuEntry objects, one native item.
            object copy = Handle("Copy");

            CollectionAssert.AreEqual(
                new[] { "Copy" }, Menu(new List<TiroMenuEntry> { Browser(copy), Browser(copy) }, copy));
        }

        [TestMethod]
        public void ADuplicateIsDroppedWithoutDisturbingTheRest()
        {
            object copy = Handle("Copy"), paste = Handle("Paste");

            CollectionAssert.AreEqual(
                new[] { "Copy", "Paste", "host:Insert#0" },
                Menu(new List<TiroMenuEntry> { Browser(copy), Browser(paste), Browser(copy), Host("Insert") },
                     copy, paste));
        }

        [TestMethod]
        public void NullsAreSkipped()
        {
            object copy = Handle("Copy");

            var plan = Plan.Create(new List<TiroMenuEntry> { null, Browser(copy), null }, new[] { copy });

            Assert.IsFalse(plan.ClearExisting, "a skipped null must not look like a reorder");
            CollectionAssert.AreEqual(new[] { "Copy" }, Render(plan, new[] { copy }));
        }

        [TestMethod]
        public void AnEmptyMenuClearsRatherThanLeavingTheBrowsersItems()
        {
            object copy = Handle("Copy");

            var plan = Plan.Create(new List<TiroMenuEntry>(), new[] { copy });

            Assert.IsTrue(plan.ClearExisting, "returning nothing meant nothing, not 'leave the default'");
            Assert.AreEqual(0, Render(plan, new[] { copy }).Count);
        }

        [TestMethod]
        public void NullArgumentsAreTolerated()
        {
            var plan = Plan.Create(null, null);

            Assert.IsFalse(plan.ClearExisting);
            Assert.AreEqual(0, plan.Steps.Count);
        }

        // ---- Label occurrences and separators ----------------------------------------------------

        [TestMethod]
        public void RepeatedLabelsGetDistinctOccurrences()
        {
            // The cache is keyed by label+occurrence. Without the occurrence both entries would
            // map to one cached object, which would then be added to the menu twice.
            CollectionAssert.AreEqual(
                new[] { "host:Insert#0", "host:Insert#1", "host:Other#0", "host:Insert#2" },
                Menu(new List<TiroMenuEntry> { Host("Insert"), Host("Insert"), Host("Other"), Host("Insert") }));
        }

        [TestMethod]
        public void OccurrencesRestartForEachMenu()
        {
            var composed = new List<TiroMenuEntry> { Host("Insert"), Host("Insert") };

            CollectionAssert.AreEqual(Menu(composed), Menu(composed),
                "a menu's occurrences must not depend on how many menus preceded it");
        }

        [TestMethod]
        public void SeparatorsAreNumberedInOrder()
        {
            // Each needs its own pooled object; reusing one would add it twice.
            var plan = Plan.Create(
                new List<TiroMenuEntry>
                {
                    Host("A"), TiroMenuEntry.CreateSeparator(), Host("B"), TiroMenuEntry.CreateSeparator(), Host("C"),
                },
                new object[0]);

            CollectionAssert.AreEqual(
                new[] { 0, 1 },
                plan.Steps.Where(s => s.Kind == Plan.StepKind.Separator).Select(s => s.SeparatorIndex).ToList());
        }

        [TestMethod]
        public void CountersStartFromZeroOnTheAppendPath()
        {
            // The skipped prefix is browser items only, so nothing in it consumes an occurrence
            // or a separator slot.
            object copy = Handle("Copy");
            var plan = Plan.Create(
                new List<TiroMenuEntry> { Browser(copy), TiroMenuEntry.CreateSeparator(), Host("Insert") },
                new[] { copy });

            Assert.IsFalse(plan.ClearExisting);
            Assert.AreEqual(0, plan.Steps.Single(s => s.Kind == Plan.StepKind.Separator).SeparatorIndex);
            Assert.AreEqual(0, plan.Steps.Single(s => s.Kind == Plan.StepKind.HostCommand).Occurrence);
        }

        // ---- What each step carries ---------------------------------------------------------------

        [TestMethod]
        public void AHostStepCarriesItsActionAndEnabledState()
        {
            var invoked = false;
            var plan = Plan.Create(
                new List<TiroMenuEntry> { Host("Insert", isEnabled: false, invoke: () => invoked = true) },
                new object[0]);

            var step = plan.Steps.Single();
            Assert.AreEqual("Insert", step.Label);
            Assert.IsFalse(step.IsEnabled);
            step.Invoke();
            Assert.IsTrue(invoked);
        }

        [TestMethod]
        public void HandleIdentityIsByReferenceOnly()
        {
            // A browser wrapper type that overrides Equals must not be able to make two
            // distinct menu items collapse into one.
            var x = new AlwaysEqual();
            var y = new AlwaysEqual();
            Assert.AreEqual(x, y, "precondition: these compare equal by value");

            var plan = Plan.Create(new List<TiroMenuEntry> { Browser(x), Browser(y) }, new object[] { x, y });

            Assert.IsFalse(plan.ClearExisting);
            CollectionAssert.AreEqual(new[] { "x", "y" }, Render(plan, new object[] { x, y }),
                "distinct objects stay distinct");
        }

        private sealed class AlwaysEqual
        {
            private static int _next;
            private readonly string _name = _next++ == 0 ? "x" : "y";
            public override bool Equals(object obj) => obj is AlwaysEqual;
            public override int GetHashCode() => 1;
            public override string ToString() => _name;
        }
    }
}
