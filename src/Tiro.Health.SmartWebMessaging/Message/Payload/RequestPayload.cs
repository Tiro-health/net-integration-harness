using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    [JsonDerivedType(typeof(RequestPayload), typeDiscriminator: "base")]
    [JsonDerivedType(typeof(SdcConfigure), typeDiscriminator: "sdcConfigure")]
    public class RequestPayload
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = new Dictionary<string, JsonElement>();
    }
}
