using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    [JsonDerivedType(typeof(ResponsePayload), typeDiscriminator: "base")]
    [JsonDerivedType(typeof(ErrorResponse), typeDiscriminator: "error")]
    public class ResponsePayload
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = new Dictionary<string, JsonElement>();
    }
}
