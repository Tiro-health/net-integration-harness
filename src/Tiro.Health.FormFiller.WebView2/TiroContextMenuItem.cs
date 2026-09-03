using System;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// One host-supplied entry in the form's right-click menu. Add these to
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}.ContextMenuItems"/>; the harness appends
    /// them to the embedded browser's own context menu, below its native entries, and calls
    /// <see cref="Action"/> back on the UI thread when the user picks one.
    /// </summary>
    /// <remarks>
    /// The label is the host's, and so is the data: nothing is resolved until the item is
    /// clicked, so <see cref="CopyToClipboard(string, Func{string})"/>'s provider sees the
    /// EHR's current patient, current conclusion, current everything. The collection itself is
    /// read at menu time too, so items can be added, removed or relabelled at any point —
    /// including from the EHR's own configuration, read at startup or per patient.
    /// </remarks>
    public sealed class TiroContextMenuItem
    {
        public TiroContextMenuItem(string label, Action<TiroContextMenuContext> action)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("A menu item needs a label.", nameof(label));
            Label = label;
            Invoke = action ?? throw new ArgumentNullException(nameof(action));
        }

        /// <summary>Convenience ctor for an action that doesn't care what was clicked.</summary>
        public TiroContextMenuItem(string label, Action action)
            : this(label, action == null ? (Action<TiroContextMenuContext>)null : _ => action())
        {
        }

        /// <summary>
        /// The menu text, as the user sees it. Also the item's identity: the underlying browser
        /// menu item is created once per label and reused (the WebView2 environment caps the
        /// number of live custom items), so two items sharing a label collapse into one.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// What to do when the user picks the item. Runs on the UI thread. An exception here is
        /// captured to telemetry and swallowed — a failing menu item must not take down the
        /// browser's context-menu event, and there is no user-facing place to report it.
        /// </summary>
        public Action<TiroContextMenuContext> Invoke { get; }

        /// <summary>
        /// Optional per-click filter. Return false to leave the item out of this menu — e.g.
        /// <c>Function(ctx) ctx.IsEditable</c> for an item that only makes sense over a field
        /// the user can paste into. Null means always shown.
        /// </summary>
        public Func<TiroContextMenuContext, bool> IsVisible { get; set; }

        /// <summary>
        /// An item that copies <paramref name="text"/>'s result to the Windows clipboard, for
        /// the user to paste with Ctrl+V into whichever field they want — the same as any other
        /// copy they make. The provider runs at click time, so the value follows the EHR's
        /// current state rather than whatever it held when the menu was configured.
        /// </summary>
        /// <remarks>
        /// An empty or null result is a no-op: the clipboard API rejects it, and clearing the
        /// clinician's clipboard is worse than doing nothing. Uses the retrying
        /// <see cref="Clipboard.SetDataObject(object, bool, int, int)"/> overload because the
        /// clipboard is a machine-wide single-owner resource another process can be holding
        /// open. <c>copy: true</c> leaves the value there after this application exits, so a
        /// copy made just before the form closes still pastes afterwards.
        /// <para>
        /// Plain text only. What is copied also leaves the application: the Windows clipboard is
        /// readable by every process on the machine, and clipboard managers, Remote Desktop
        /// redirection and Windows Cloud Clipboard may persist or sync it. Ordinary for a phrase
        /// the clinician is about to paste; worth a deliberate decision for a whole report.
        /// </para>
        /// </remarks>
        public static TiroContextMenuItem CopyToClipboard(string label, Func<string> text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return new TiroContextMenuItem(label, (Action<TiroContextMenuContext>)(_ =>
            {
                var value = text();
                if (string.IsNullOrEmpty(value)) return;
                Clipboard.SetDataObject(value, copy: true, retryTimes: 5, retryDelay: 50);
            }));
        }
    }
}
