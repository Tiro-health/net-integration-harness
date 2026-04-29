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

        /// <summary>
        /// Optional transport-layer metadata (trace propagation, etc.). Serialized as
        /// <c>_meta</c>; omitted entirely when null so non-instrumented messages stay clean.
        /// </summary>
        [JsonPropertyName("_meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MessageMeta Meta { get; set; }

        protected SmartMessageBase()
        {
        }

        protected SmartMessageBase(string messageId)
        {
            MessageId = messageId;
        }
    }
}
