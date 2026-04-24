using System.Text.Json;
using System.Text.Json.Serialization;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tiro.Health.SmartWebMessaging.Fhir.R5
{
    /// <summary>
    /// FHIR R5 concrete handler. Binds Resource, QuestionnaireResponse, OperationOutcome.
    /// All convenience <c>Send*</c> overloads live on the generic base.
    /// </summary>
    public class SmartMessageHandler
        : SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome>
    {
        public SmartMessageHandler()
            : this(NullLogger<SmartMessageHandler>.Instance)
        {
        }

        public SmartMessageHandler(JsonSerializerOptions serializeOptions)
            : this(NullLogger<SmartMessageHandler>.Instance, serializeOptions)
        {
        }

        public SmartMessageHandler(ILogger<SmartMessageHandler> logger, JsonSerializerOptions serializeOptions = null)
            : base(logger, serializeOptions ?? CreateDefaultOptions())
        {
        }

        private static JsonSerializerOptions CreateDefaultOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }.ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Recoverable);
        }
    }
}
