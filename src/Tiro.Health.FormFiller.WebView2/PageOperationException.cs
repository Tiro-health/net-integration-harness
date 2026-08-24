using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// The embedded page rejected a host request. Not thrown to the caller — a send
    /// completes when the message is posted, long before the response arrives — but
    /// captured to telemetry so the failure appears somewhere other than the WebView
    /// console. Subscribe to <c>TiroFormViewer.PageError</c> to react in code.
    /// </summary>
    public class PageOperationException : Exception
    {
        public string MessageType { get; }
        public string ErrorType { get; }

        public PageOperationException(string messageType, string errorType, string errorMessage)
            : base($"The page rejected '{messageType}': {errorType}: {errorMessage}")
        {
            MessageType = messageType;
            ErrorType = errorType;
        }
    }
}
