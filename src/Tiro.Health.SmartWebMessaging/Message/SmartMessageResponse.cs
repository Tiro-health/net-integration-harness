using System;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Message
{
    public class SmartMessageResponse : SmartMessageBase
    {
        public string ResponseToMessageId { get; set; }
        public bool AdditionalResponsesExpected { get; set; }
        public ResponsePayload Payload { get; set; }

        public SmartMessageResponse() : base()
        {
        }

        public SmartMessageResponse(string messageId, string responseToMessageId, bool additionalResponsesExpected, ResponsePayload payload) : base(messageId)
        {
            ResponseToMessageId = responseToMessageId;
            AdditionalResponsesExpected = additionalResponsesExpected;
            Payload = payload;
        }

        public SmartMessageResponse(string responseToMessageId, bool additionalResponsesExpected, ResponsePayload payload)
            : this(Guid.NewGuid().ToString(), responseToMessageId, additionalResponsesExpected, payload)
        {
        }

        public SmartMessageResponse(string responseToMessageId, ResponsePayload payload)
            : this(Guid.NewGuid().ToString(), responseToMessageId, false, payload)
        {
        }

        public static SmartMessageResponse CreateErrorResponse(string responseToMessageId, ResponsePayload errorPayload)
        {
            return new SmartMessageResponse(Guid.NewGuid().ToString(), responseToMessageId, false, errorPayload);
        }
    }
}
