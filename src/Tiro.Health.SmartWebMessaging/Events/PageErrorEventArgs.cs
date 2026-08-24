using System;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// The embedded page answered one of the host's requests with an error instead of an
    /// acknowledgement — a handler on the page threw, or it did not recognise the message
    /// type. Raised for every such response; without it these failures are invisible to the
    /// host, because a send completes once the message is posted and the response arrives
    /// later on the inbound path.
    /// </summary>
    public class PageErrorEventArgs : EventArgs
    {
        /// <summary>The message type the host sent, e.g. <c>ui.form.requestSubmit</c>.</summary>
        public string MessageType { get; }

        /// <summary>Page-supplied error class, e.g. <c>HandlerException</c> or <c>UnknownMessageTypeException</c>.</summary>
        public string ErrorType { get; }

        /// <summary>Page-supplied message.</summary>
        public string ErrorMessage { get; }

        public PageErrorEventArgs(string messageType, string errorType, string errorMessage)
        {
            MessageType = messageType;
            ErrorType = errorType;
            ErrorMessage = errorMessage;
        }
    }
}
