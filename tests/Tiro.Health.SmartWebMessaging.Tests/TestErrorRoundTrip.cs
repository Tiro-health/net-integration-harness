using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.SmartWebMessaging.Message;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Tests
{
    /// <summary>
    /// The page reports a rejected request as a payload with $type "error". That has to
    /// round-trip: ErrorResponse briefly had only parameterized constructors, which made
    /// System.Text.Json throw NotSupportedException on every inbound error — so a rejection
    /// the page reported could not even be parsed, let alone surfaced to the host.
    /// </summary>
    [TestClass]
    public class TestErrorRoundTrip
    {
        [TestMethod]
        public void InboundErrorPayload_DeserializesAsErrorResponse()
        {
            const string json = """
            {
              "messageId": "resp-1",
              "responseToMessageId": "req-1",
              "additionalResponsesExpected": false,
              "payload": { "$type": "error", "errorType": "HandlerException", "errorMessage": "boom" }
            }
            """;
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
            var resp = JsonSerializer.Deserialize<SmartMessageResponse>(json, opts);
            Assert.IsInstanceOfType(resp!.Payload, typeof(ErrorResponse));
            Assert.AreEqual("HandlerException", ((ErrorResponse)resp.Payload!).ErrorType);
        }
    }
}
