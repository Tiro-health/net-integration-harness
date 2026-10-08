namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// What the user right-clicked on inside the form, handed to
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}.ContextMenuOpening"/> so the host can decide
    /// per click. Nothing here identifies a question — the embedded browser reports the DOM
    /// target, and linkIds are not part of the DOM.
    /// </summary>
    public sealed class TiroContextMenuContext
    {
        public TiroContextMenuContext(bool isEditable, string selectionText)
        {
            IsEditable = isEditable;
            SelectionText = selectionText;
        }

        /// <summary>
        /// True when the click landed in something the user can type into. The usual test for
        /// showing an insert menu: over a read-only score or a checkbox there is nowhere for the
        /// content to go.
        /// </summary>
        public bool IsEditable { get; }

        /// <summary>
        /// The text the user had selected, or null when nothing was selected. Note this is
        /// content out of the form — treat it as clinical data.
        /// </summary>
        public string SelectionText { get; }
    }
}
