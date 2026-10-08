using System;
using System.Drawing;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// A right-click in the form, raised by
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}.ContextMenuOpening"/> before the embedded
    /// browser shows its menu. Set <see cref="Handled"/> to show your own menu instead.
    /// </summary>
    public sealed class TiroContextMenuOpeningEventArgs : EventArgs
    {
        internal TiroContextMenuOpeningEventArgs(TiroContextMenuContext context, Point location)
        {
            Context = context;
            Location = location;
        }

        /// <summary>What the user right-clicked on.</summary>
        public TiroContextMenuContext Context { get; }

        /// <summary>
        /// Where the user right-clicked, in the viewer's client coordinates — ready for
        /// <c>ContextMenuStrip.Show(viewer, e.Location)</c>.
        /// </summary>
        public Point Location { get; }

        /// <summary>
        /// Set to true to suppress the browser's menu entirely: no Copy, no Paste, no spelling
        /// suggestions, and <see cref="TiroFormViewer{TResource,TQR,TOO}.BuildContextMenu"/>
        /// is not called. Leave false to let the browser show its menu as usual.
        /// </summary>
        public bool Handled { get; set; }
    }
}
