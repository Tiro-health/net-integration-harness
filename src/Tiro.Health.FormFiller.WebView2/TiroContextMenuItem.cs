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
    /// clicked, so an action closing over EHR state sees the current patient, the current
    /// conclusion, the current everything. The collection itself is
    /// read at menu time too, so items can be added, removed or relabelled at any point —
    /// including from the EHR's own configuration, read at startup or per patient.
    /// </remarks>
    public sealed class TiroContextMenuItem
    {
        /// <summary>
        /// An item whose work is asynchronous — the shape
        /// <see cref="TiroFormViewer{TResource,TQR,TOO}.InsertContentAsync"/> needs. The returned
        /// task is observed, so a failure lands in telemetry instead of on the
        /// <see cref="System.Threading.SynchronizationContext"/> as an unhandled async-void
        /// exception, which is what an <c>Async Sub</c> lambda would have produced.
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

        /// <summary>
        /// Lifts a synchronous action into the async shape the item stores. Null stays null so
        /// the async ctor raises <see cref="ArgumentNullException"/> for the right argument
        /// rather than this throwing before the label has been checked.
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

    }
}
