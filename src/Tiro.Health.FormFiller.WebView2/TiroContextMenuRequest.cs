using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// One right-click, handed to
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}.BuildContextMenu"/>. Carries what was
    /// clicked and every entry available for this click; the builder returns the menu it wants,
    /// in order.
    /// </summary>
    /// <remarks>
    /// A request is valid only for the duration of the builder call. The browser's entries are
    /// created per click and are not reusable afterwards, so holding a request — or entries out
    /// of it — across clicks yields a menu whose stale entries are dropped.
    /// </remarks>
    public sealed class TiroContextMenuRequest
    {
        private readonly Func<TiroContextMenuItem, TiroMenuEntry> _entryFactory;

        internal TiroContextMenuRequest(
            TiroContextMenuContext context,
            IReadOnlyList<TiroMenuEntry> browserItems,
            IReadOnlyList<TiroMenuEntry> hostItems,
            Func<TiroContextMenuItem, TiroMenuEntry> entryFactory)
        {
            Context = context;
            BrowserItems = browserItems;
            HostItems = hostItems;
            _entryFactory = entryFactory;
        }

        /// <summary>What the user right-clicked on.</summary>
        public TiroContextMenuContext Context { get; }

        /// <summary>
        /// The embedded browser's own entries for this click, in the order it would have shown
        /// them: Copy, Paste, spelling suggestions, Inspect, and whatever else applies to the
        /// target. Which entries exist depends entirely on what was clicked.
        /// </summary>
        public IReadOnlyList<TiroMenuEntry> BrowserItems { get; }

        /// <summary>
        /// The viewer's <see cref="TiroFormViewer{TResource,TQR,TOO}.ContextMenuItems"/>, already
        /// reduced to entries: each one's <c>IsVisible</c> test has been applied for this click
        /// (so a filtered-out item simply isn't here) and its action is wrapped so a failure
        /// reaches telemetry instead of the message pump.
        /// </summary>
        public IReadOnlyList<TiroMenuEntry> HostItems { get; }

        /// <summary>
        /// Wraps an item the builder holds itself — one that isn't in
        /// <see cref="TiroFormViewer{TResource,TQR,TOO}.ContextMenuItems"/> — into an entry with
        /// the same guard <see cref="HostItems"/> gets. The item's <c>IsVisible</c> test is
        /// <em>not</em> applied: a builder deciding what to show is already the filter.
        /// </summary>
        public TiroMenuEntry CreateEntry(TiroContextMenuItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return _entryFactory(item);
        }
    }
}
