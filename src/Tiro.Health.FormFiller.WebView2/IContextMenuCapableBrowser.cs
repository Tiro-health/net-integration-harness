using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Optional <see cref="IEmbeddedBrowser"/> capability: a browser that can show host-supplied
    /// items in its own native context menu. Kept off <see cref="IEmbeddedBrowser"/> so an
    /// existing implementation (a WPF or CEF host, someone's test double) keeps compiling — the
    /// viewer probes for it with <c>as</c> and a browser that doesn't implement it simply has no
    /// host menu items.
    /// </summary>
    /// <remarks>
    /// A provider rather than a fixed list, because the menu is built per click: the host's
    /// collection may have changed, and the items shown can depend on what was clicked.
    /// </remarks>
    public interface IContextMenuCapableBrowser
    {
        /// <summary>
        /// Asked for the items to append to the browser's context menu, each time one is
        /// requested. Null (the default) means the host wants none. The implementation must
        /// treat a null or empty result as "add nothing", and must not let an exception from
        /// the provider escape into the browser's event.
        /// </summary>
        Func<TiroContextMenuContext, IReadOnlyList<EmbeddedBrowserMenuItem>> ContextMenuItemsProvider { get; set; }
    }

    /// <summary>
    /// A resolved menu entry as the browser layer needs it: a label and something to run. The
    /// host-facing <see cref="TiroContextMenuItem"/> is reduced to this by the viewer, which
    /// owns the visibility test and the exception guard — so the browser layer stays a
    /// renderer, not a policy.
    /// </summary>
    public sealed class EmbeddedBrowserMenuItem
    {
        public EmbeddedBrowserMenuItem(string label, Action invoke)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("A menu item needs a label.", nameof(label));
            Label = label;
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        }

        public string Label { get; }

        /// <summary>Runs on the UI thread when the user picks the item. Must not throw.</summary>
        public Action Invoke { get; }
    }
}
