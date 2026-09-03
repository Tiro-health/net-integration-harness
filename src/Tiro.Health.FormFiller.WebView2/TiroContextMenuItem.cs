using System;
using System.Threading.Tasks;

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
        /// <summary>
        /// An item whose work is asynchronous — the shape
        /// <see cref="TiroFormViewer{TResource,TQR,TOO}.InsertTextAsync"/> needs. The returned
        /// task is observed: a failure lands in telemetry instead of on the
        /// <see cref="System.Threading.SynchronizationContext"/> as an unhandled
        /// async-void exception, which is what an <c>Async Sub</c> lambda would have done.
        /// </summary>
        public TiroContextMenuItem(string label, Func<TiroContextMenuContext, Task> action)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("A menu item needs a label.", nameof(label));
            if (action == null) throw new ArgumentNullException(nameof(action));
            Label = label;
            Invoke = action;
        }

        public TiroContextMenuItem(string label, Action<TiroContextMenuContext> action)
            : this(label, Wrap(action))
        {
        }

        /// <summary>Convenience ctor for an action that doesn't care what was clicked.</summary>
        public TiroContextMenuItem(string label, Action action)
            : this(label, action == null ? (Action<TiroContextMenuContext>)null : _ => action())
        {
        }

        /// <summary>
        /// Lifts a synchronous action into the async shape the item stores. Null stays null so
        /// the async ctor raises <see cref="ArgumentNullException"/> for the right argument
        /// rather than this method throwing before the label has even been checked.
        /// </summary>
        private static Func<TiroContextMenuContext, Task> Wrap(Action<TiroContextMenuContext> action)
        {
            if (action == null) return null;
            return context =>
            {
                action(context);
                return CompletedTask;
            };
        }

        // net48 has no Task.CompletedTask.
        private static readonly Task CompletedTask = Task.FromResult(true);

        /// <summary>
        /// The menu text, as the user sees it. Also the item's identity: the underlying browser
        /// menu item is created once per label and reused (the WebView2 environment caps the
        /// number of live custom items), so two items sharing a label collapse into one.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// What to do when the user picks the item. Starts on the UI thread; a synchronous
        /// action is lifted into a completed task. Failures — thrown or faulted — are captured
        /// to telemetry and swallowed: a failing menu item must not take down the browser's
        /// context-menu event, and there is no user-facing place to report it.
        /// </summary>
        public Func<TiroContextMenuContext, Task> Invoke { get; }

        /// <summary>
        /// Optional per-click filter. Return false to leave the item out of this menu — e.g.
        /// <c>Function(ctx) ctx.IsEditable</c> for an item that only makes sense over a field
        /// the user can paste into. Null means always shown.
        /// </summary>
        public Func<TiroContextMenuContext, bool> IsVisible { get; set; }

        /// <summary>
        /// An item that puts <paramref name="text"/>'s result on the Windows clipboard as plain
        /// text, for the user to paste with Ctrl+V wherever they want it. The provider runs at
        /// click time, so it reflects the EHR's state then, not when the menu was configured.
        /// </summary>
        /// <remarks>
        /// An empty or null result is a no-op. See <see cref="TiroClipboard"/> for the clipboard
        /// mechanics and for what copying implies about where the data can travel.
        /// </remarks>
        public static TiroContextMenuItem CopyToClipboard(string label, Func<string> text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return new TiroContextMenuItem(label, (Action<TiroContextMenuContext>)(_ =>
                TiroClipboard.SetContent(new TiroClipboardContent { PlainText = text() })));
        }

        /// <summary>
        /// An item that copies formatted content — HTML for the form's rich-text fields, with a
        /// plain-text fallback and optionally RTF for Word and Outlook. Every format the
        /// returned <see cref="TiroClipboardContent"/> carries goes on the clipboard together,
        /// and each paste target picks the richest one it understands.
        /// </summary>
        /// <remarks>
        /// The provider runs at click time, so an EHR can convert its RTF then rather than
        /// up front. The harness does no conversion of its own — see
        /// <see cref="TiroClipboardContent"/> for why, and for the class-versus-inline-styles
        /// trap that decides whether a converter's output survives the clipboard at all.
        /// </remarks>
        public static TiroContextMenuItem CopyRichTextToClipboard(
            string label, Func<TiroClipboardContent> content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return new TiroContextMenuItem(label, (Action<TiroContextMenuContext>)(_ =>
                TiroClipboard.SetContent(content())));
        }
    }
}
