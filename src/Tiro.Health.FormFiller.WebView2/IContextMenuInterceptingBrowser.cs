using System;
using System.Drawing;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Optional <see cref="IEmbeddedBrowser"/> capability: a browser that lets the host suppress
    /// its context menu and draw its own instead. A separate interface from
    /// <see cref="IContextMenuCapableBrowser"/> for the same reason that one is separate from
    /// <see cref="IEmbeddedBrowser"/> — an existing implementation keeps compiling, and the
    /// viewer probes for it with <c>as</c>.
    /// </summary>
    public interface IContextMenuInterceptingBrowser
    {
        /// <summary>
        /// Asked first on every right-click, before <see cref="IContextMenuCapableBrowser.ContextMenuBuilder"/>.
        /// The arguments are what was clicked and where, in the client coordinates of
        /// <see cref="IEmbeddedBrowser.Control"/>. True means the host has taken over: the
        /// browser shows no menu at all and the builder is not asked.
        /// <para>
        /// Null (the default) and false both mean the browser carries on as usual. The
        /// implementation must not let an exception from the interceptor escape into the
        /// browser's event.
        /// </para>
        /// </summary>
        Func<TiroContextMenuContext, Point, bool> ContextMenuInterceptor { get; set; }
    }
}
