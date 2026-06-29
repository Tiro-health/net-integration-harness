using System;
using System.Net.Http;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Tiro.Health.Telemetry;

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
        // Built once and shared: the R5 FHIR converter set (~150 resource types) is identical for
        // every instance, and JsonSerializerOptions is thread-safe once used. Rebuilding it per
        // construction wastes CPU/allocations on the hot IHttpClientFactory per-request path.
        private static readonly JsonSerializerOptions FhirJson =
            new JsonSerializerOptions()
                .ForFhir(ModelInfo.ModelInspector)
                .UsingMode(DeserializerModes.Recoverable);

        /// <param name="baseAddress">The SDC server FHIR R5 base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client for custom TLS/proxy/timeouts.</param>
        /// <param name="telemetry">
        /// Optional telemetry session; when supplied, each <c>$validate</c>/<c>$extract</c> round-trip
        /// is recorded as a transaction in its trace. Omit for no telemetry.
        /// </param>
        public SdcClient(Uri baseAddress, HttpClient httpClient = null, ITelemetrySession telemetry = null)
            : base(baseAddress, FhirJson, httpClient, telemetry)
        {
        }

        /// <summary>
        /// Construct from an <see cref="SdcConnection"/> — shared base address, transport, and
        /// telemetry trace. Use this when the host also configures a form viewer against the same
        /// SDC server.
        /// </summary>
        /// <param name="connection">The SDC connection (base address + optional HttpClient + optional session).</param>
        public SdcClient(SdcConnection connection)
            : base(connection, FhirJson)
        {
        }
    }
}
