using System.Threading.Tasks;
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
            // Through the real handler, not ad-hoc options: it is the production
            // configuration that has to be able to read an error payload.
            SmartMessageResponse? seen = null;
            var handler = new Fhir.R5.SmartMessageHandler { SendMessage = _ => Task.CompletedTask };
            handler.SendRequestAsync(
                new SmartMessageRequest("req-1", "smart-web-messaging", "ui.form.requestSubmit", new RequestPayload()),
                responseHandler: r => { seen = r; return Task.CompletedTask; }).GetAwaiter().GetResult();
            handler.HandleMessage(json);
            var resp = seen;
            Assert.IsNotNull(resp, "the handler dropped the response");
            Assert.IsInstanceOfType(resp!.Payload, typeof(ErrorResponse));
            Assert.AreEqual("HandlerException", ((ErrorResponse)resp.Payload!).ErrorType);
        }
    }
}
