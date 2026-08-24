using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// The embedded page answered one of the host's requests with an error instead of an
    /// acknowledgement — a handler on the page threw, or it did not recognise the message
    /// type. Raised for every such response; without it these failures are invisible to the
    /// host, because a send completes once the message is posted and the response arrives
    /// later on the inbound path.
    /// <para>
    /// Raised on the UI thread, so a handler may touch UI directly. Lives beside
    /// <see cref="PageOperationException"/> rather than in
    /// <c>Tiro.Health.SmartWebMessaging.Events</c>, because it is raised by the viewer and
    /// not by the message handler.
    /// </para>
    /// </summary>
    public class PageErrorEventArgs : EventArgs
    {
        /// <summary>The message type the host sent, e.g. <c>ui.form.requestSubmit</c>.</summary>
        public string MessageType { get; }

        /// <summary>Page-supplied error class, e.g. <c>HandlerException</c> or <c>UnknownMessageTypeException</c>.</summary>
        public string ErrorType { get; }

        /// <summary>Page-supplied message.</summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Id of the request that was rejected. Lets a host that issued several requests of
        /// the same type — a save-draft then a finalize, or a retry — tell which one failed.
        /// </summary>
        public string MessageId { get; }

        public PageErrorEventArgs(string messageType, string errorType, string errorMessage, string messageId)
        {
            MessageType = messageType;
            ErrorType = errorType;
            ErrorMessage = errorMessage;
            MessageId = messageId;
        }
    }
}
