using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// How a composed menu is turned into calls on the embedded browser's item collection —
    /// worked out as data, before a single COM call is made.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="WebView2EmbeddedBrowser"/> because the decisions here are the
    /// error-prone part (what to remove, what to reuse, which cached object a repeated label
    /// maps to) and none of them need a browser. Executing the plan is a loop with no branches
    /// worth testing; planning it is branches all the way down, and this way they are covered
    /// without a live Chromium.
    /// </remarks>
    internal sealed class ContextMenuRenderPlan
    {
        internal enum StepKind
        {
            /// <summary>Re-add one of the browser's own items, by the handle it supplied.</summary>
            BrowserItem,

            /// <summary>Add a host command, rendered from a cached item keyed by label+occurrence.</summary>
            HostCommand,

            /// <summary>Add the separator at <see cref="Step.SeparatorIndex"/> in the pool.</summary>
            Separator,
        }

        internal sealed class Step
        {
            public StepKind Kind;

            /// <summary><see cref="StepKind.BrowserItem"/>: the browser's own object.</summary>
            public object BrowserHandle;

            /// <summary><see cref="StepKind.HostCommand"/>: the menu text.</summary>
            public string Label;

            /// <summary>
            /// <see cref="StepKind.HostCommand"/>: how many earlier steps in this same menu
            /// already used <see cref="Label"/>. Together with the label it identifies the
            /// cached object to render from, so two entries sharing a label in one menu get two
            /// objects instead of one added twice.
            /// </summary>
            public int Occurrence;

            public bool IsEnabled;
            public Action Invoke;

            /// <summary><see cref="StepKind.Separator"/>: index into the separator pool.</summary>
            public int SeparatorIndex;
        }

        private ContextMenuRenderPlan(bool clearExisting, IReadOnlyList<Step> steps)
        {
            ClearExisting = clearExisting;
            Steps = steps;
        }

        /// <summary>
        /// Whether the browser's collection must be emptied first. False for the common shape —
        /// the browser's own items, untouched and in order, with host items after them — which
        /// is a pure append and leaves the default menu's rendering exactly as it was.
        /// </summary>
        public bool ClearExisting { get; }

        /// <summary>What to add, in order, after any clearing.</summary>
        public IReadOnlyList<Step> Steps { get; }

        /// <summary>
        /// Plans the render of <paramref name="composed"/> against the items the browser offered
        /// for this click.
        /// </summary>
        /// <param name="composed">The menu as the viewer composed it. Nulls are tolerated.</param>
        /// <param name="browserHandles">
        /// The handles this click supplied, in the browser's own order. A browser entry naming
        /// anything else is from a stale request and is dropped; so is a repeat of one already
        /// placed, because the same object cannot occupy two positions in one menu.
        /// </param>
        internal static ContextMenuRenderPlan Create(
            IReadOnlyList<TiroMenuEntry> composed, IReadOnlyList<object> browserHandles)
        {
            if (composed == null) composed = new List<TiroMenuEntry>();
            if (browserHandles == null) browserHandles = new List<object>();

            var offered = new HashSet<object>(browserHandles, ReferenceComparer.Instance);
            var placed = new HashSet<object>(ReferenceComparer.Instance);

            var usable = new List<TiroMenuEntry>(composed.Count);
            foreach (var entry in composed)
            {
                if (entry == null) continue;
                if (entry.IsFromBrowser)
                {
                    if (!offered.Contains(entry.BrowserHandle)) continue;
                    if (!placed.Add(entry.BrowserHandle)) continue;
                }
                usable.Add(entry);
            }

            var clearExisting = !IsAppendOnly(usable, browserHandles);
            var steps = new List<Step>(usable.Count);
            var separators = 0;
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

            // In append-only shape the browser's items are already in place, in order, so the
            // plan covers only what follows them. That prefix is browser items by definition,
            // which is why the counters below can start from zero either way.
            for (var i = clearExisting ? 0 : browserHandles.Count; i < usable.Count; i++)
            {
                var entry = usable[i];
                if (entry.IsFromBrowser)
                {
                    steps.Add(new Step { Kind = StepKind.BrowserItem, BrowserHandle = entry.BrowserHandle });
                }
                else if (entry.Kind == TiroMenuEntryKind.Separator)
                {
                    steps.Add(new Step { Kind = StepKind.Separator, SeparatorIndex = separators++ });
                }
                else
                {
                    occurrences.TryGetValue(entry.Label, out var occurrence);
                    occurrences[entry.Label] = occurrence + 1;
                    steps.Add(new Step
                    {
                        Kind = StepKind.HostCommand,
                        Label = entry.Label,
                        Occurrence = occurrence,
                        IsEnabled = entry.IsEnabled,
                        Invoke = entry.Invoke,
                    });
                }
            }

            return new ContextMenuRenderPlan(clearExisting, steps);
        }

        /// <summary>
        /// True when <paramref name="usable"/> is the browser's handles, unchanged and in order,
        /// followed only by host entries — the shape the default layout produces.
        /// </summary>
        private static bool IsAppendOnly(IReadOnlyList<TiroMenuEntry> usable, IReadOnlyList<object> browserHandles)
        {
            if (usable.Count < browserHandles.Count) return false;
            for (var i = 0; i < browserHandles.Count; i++)
            {
                var entry = usable[i];
                if (!entry.IsFromBrowser) return false;
                if (!ReferenceEquals(entry.BrowserHandle, browserHandles[i])) return false;
            }
            for (var i = browserHandles.Count; i < usable.Count; i++)
                if (usable[i].IsFromBrowser) return false;
            return true;
        }

        /// <summary>
        /// Identity comparison for handles. The browser's objects are compared by reference and
        /// nothing else — a custom <c>Equals</c> on a browser's wrapper type must not be able to
        /// make two distinct menu items look like one.
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj)
                => obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
