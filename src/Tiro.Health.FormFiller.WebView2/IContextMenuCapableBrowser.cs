using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Optional <see cref="IEmbeddedBrowser"/> capability: a browser that lets the host compose
    /// its own context menu out of the browser's native entries plus the host's. Kept off
    /// <see cref="IEmbeddedBrowser"/> so an existing implementation (a WPF or CEF host,
    /// someone's test double) keeps compiling — the viewer probes for it with <c>as</c> and a
    /// browser that doesn't implement it simply shows no host entries.
    /// </summary>
    /// <remarks>
    /// The browser renders; it decides nothing. It hands over the entries it was about to show
    /// and renders back whatever list it gets — the viewer owns the visibility tests, the
    /// exception guards and the default layout.
    /// </remarks>
    public interface IContextMenuCapableBrowser
    {
        /// <summary>
        /// Asked to compose the menu, each time one is requested. The arguments are what was
        /// clicked and the browser's own entries for this click, in its default order; the
        /// result is the menu to show, in order.
        /// <para>
        /// Null (the default) means the browser shows its own menu untouched. A null or empty
        /// result means an empty menu. The implementation must not let an exception from the
        /// builder escape into the browser's event, and must ignore any entry whose
        /// <see cref="TiroMenuEntry.IsFromBrowser"/> handle it did not supply for this click.
        /// </para>
        /// </summary>
        Func<TiroContextMenuContext, IReadOnlyList<TiroMenuEntry>, IReadOnlyList<TiroMenuEntry>> ContextMenuBuilder { get; set; }
    }
}
