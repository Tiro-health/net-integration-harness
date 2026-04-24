namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Explicit lifecycle state of a <see cref="TiroFormViewer{TResource,TQR,TOO}"/>.
    /// Read via <see cref="TiroFormViewer{TResource,TQR,TOO}.State"/>; transitions
    /// happen internally and are not user-driven.
    /// </summary>
    public enum TiroFormViewerState
    {
        /// <summary>
        /// Post-construction; the WebView2 browser is loading and/or the page has
        /// not yet posted its <c>status.handshake</c>. <c>SetContextAsync</c> callers
        /// wait internally until this state advances to <see cref="Ready"/>.
        /// </summary>
        Initializing,

        /// <summary>
        /// Handshake received; ready to accept a call to <c>SetContextAsync</c>.
        /// </summary>
        Ready,

        /// <summary>
        /// <c>SetContextAsync</c> has successfully sent <c>sdc.displayQuestionnaire</c>;
        /// the form is displayed and awaiting user submission. Calling
        /// <c>SetContextAsync</c> again in this state is rejected — create a new
        /// viewer for a second form.
        /// </summary>
        ContextSet,

        /// <summary>
        /// <c>form.submitted</c> received and the <c>FormSubmitted</c> event has fired.
        /// No further <c>Send*</c> operations are accepted.
        /// </summary>
        Submitted,

        /// <summary>
        /// Control disposed. Terminal state; every public operation throws
        /// <see cref="System.ObjectDisposedException"/>.
        /// </summary>
        Disposed
    }
}
