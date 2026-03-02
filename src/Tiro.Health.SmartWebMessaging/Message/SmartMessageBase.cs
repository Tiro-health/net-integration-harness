using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Message
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$messageType")]
    [JsonDerivedType(typeof(SmartMessageRequest), typeDiscriminator: "request")]
    [JsonDerivedType(typeof(SmartMessageResponse), typeDiscriminator: "response")]
    public abstract class SmartMessageBase
    {
        [Required(ErrorMessage = "MessageId is mandatory.")]
        public string MessageId { get; set; }

        protected SmartMessageBase()
        {
        }

        protected SmartMessageBase(string messageId)
        {
            MessageId = messageId;
        }
    }
}
