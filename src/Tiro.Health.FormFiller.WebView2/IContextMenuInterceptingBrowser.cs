using System;
using System.Drawing;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Optional <see cref="IEmbeddedBrowser"/> capability: a browser that lets the host suppress
    /// its context menu and draw its own instead. Kept off <see cref="IEmbeddedBrowser"/> so an
    /// existing implementation (a WPF or CEF host, someone's test double) keeps compiling — the
    /// viewer probes for it with <c>as</c>.
    /// </summary>
    public interface IContextMenuInterceptingBrowser
    {
        /// <summary>
        /// Asked on every right-click. The arguments are what was clicked and where, in the
        /// client coordinates of <see cref="IEmbeddedBrowser.Control"/>. True means the host has
        /// taken over: the browser shows no menu at all.
        /// <para>
        /// Null (the default) and false both mean the browser carries on as usual. The
        /// implementation must not let an exception from the interceptor escape into the
        /// browser's event.
        /// </para>
        /// </summary>
        Func<TiroContextMenuContext, Point, bool> ContextMenuInterceptor { get; set; }
    }
}
