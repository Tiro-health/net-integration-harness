using System;
using System.Net.Http;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace Tiro.Health.FormSdk.Client.Fhir.R5
{
    /// <summary>
    /// FHIR R5 SDC client. Wraps the stateless SDC server operations
    /// <c>QuestionnaireResponse/$validate</c> and <c>QuestionnaireResponse/$extract</c>.
    /// </summary>
    /// <remarks>
    /// These operations are R5-only on the SDC server. Construct with the server's FHIR base
    /// (e.g. <c>https://host/fhir/r5</c>); optionally inject a pre-configured <see cref="HttpClient"/>
    /// for custom TLS/proxy/timeouts.
    /// </remarks>
    public sealed class SdcClient : SdcClientBase<QuestionnaireResponse, OperationOutcome, Bundle>
    {
        /// <param name="baseAddress">The SDC server FHIR R5 base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client for custom TLS/proxy/timeouts.</param>
        public SdcClient(Uri baseAddress, HttpClient httpClient = null)
            : base(baseAddress, CreateFhirJson(), httpClient)
        {
        }

        private static JsonSerializerOptions CreateFhirJson()
            => new JsonSerializerOptions()
                .ForFhir(ModelInfo.ModelInspector)
                .UsingMode(DeserializerModes.Recoverable);
    }
}
