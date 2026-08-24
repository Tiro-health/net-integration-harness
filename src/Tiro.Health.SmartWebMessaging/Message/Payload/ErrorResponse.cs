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

        // Settable properties + parameterless ctor: matches every other payload DTO in the
        // protocol and lets System.Text.Json deserialize via the property-setter path.
        // Without it the two parameterized ctors below are ambiguous to the serializer, so
        // an INBOUND error payload threw NotSupportedException — meaning a rejection the
        // page reported could never even be parsed by the host, let alone surfaced.
        public ErrorResponse()
        {
        }

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
