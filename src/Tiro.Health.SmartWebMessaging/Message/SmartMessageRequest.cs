using System.ComponentModel.DataAnnotations;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Message
{
    public class SmartMessageRequest : SmartMessageBase
    {
        [Required(ErrorMessage = "MessagingHandle is mandatory.")]
        public string MessagingHandle { get; set; }

        [Required(ErrorMessage = "MessageType is mandatory.")]
        public string MessageType { get; set; }

        [Required(ErrorMessage = "Payload is mandatory.")]
        public RequestPayload Payload { get; set; }

        public SmartMessageRequest() : base()
        {
        }

        public SmartMessageRequest(string messageId, string messagingHandle, string messageType, RequestPayload payload) : base(messageId)
        {
            MessagingHandle = messagingHandle;
            MessageType = messageType;
            Payload = payload;
        }
    }
}
