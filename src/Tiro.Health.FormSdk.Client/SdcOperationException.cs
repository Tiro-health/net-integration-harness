using System;
using System.Net;
using Hl7.Fhir.Model;

namespace Tiro.Health.FormSdk.Client
{
    /// <summary>
    /// Thrown when an SDC server operation fails at the transport/server level (a non-success
    /// HTTP status). Carries the operation name, the HTTP status, and — when the server returned
    /// one — the parsed <see cref="OperationOutcome"/> describing the failure.
    /// </summary>
    /// <remarks>
    /// A <c>$validate</c> call that simply reports validation issues is NOT an error: it returns a
    /// normal 200 response with an <see cref="OperationOutcome"/>, which the client returns directly.
    /// This exception is reserved for actual operation failures (4xx/5xx, unreadable bodies, etc.).
    /// </remarks>
    public class SdcOperationException : Exception
    {
        /// <summary>The operation that failed, e.g. <c>QuestionnaireResponse/$validate</c>.</summary>
        public string Operation { get; }

        /// <summary>The HTTP status code returned by the SDC server.</summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>The server's <see cref="OperationOutcome"/>, if the error body contained one; otherwise <c>null</c>.</summary>
        public OperationOutcome Outcome { get; }

        /// <summary>Creates the exception with the failing operation, HTTP status, optional server outcome, and a message.</summary>
        public SdcOperationException(string operation, HttpStatusCode statusCode, OperationOutcome outcome, string message)
            : base(message)
        {
            Operation = operation;
            StatusCode = statusCode;
            Outcome = outcome;
        }
    }
}
