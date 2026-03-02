using System;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Represents an error response payload. This is a custom error payload (not in the specs).
    /// </summary>
    public class ErrorResponse : ResponsePayload
    {
        public string ErrorMessage { get; set; }
        public string ErrorType { get; set; }

        public ErrorResponse(string errorMessage, string errorType)
        {
            ErrorMessage = errorMessage;
            ErrorType = errorType;
        }

        public ErrorResponse(Exception error)
        {
            ErrorMessage = error.Message;
            ErrorType = error.GetType().Name;
        }
    }
}
