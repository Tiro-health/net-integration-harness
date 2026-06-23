using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;

namespace Tiro.Health.FormSdk.Client
{
    /// <summary>
    /// Version-agnostic client for the stateless SDC server FHIR operations. A closed-binding
    /// subclass (e.g. the R5 binding) fixes the FHIR resource types and supplies a FHIR-configured
    /// <see cref="JsonSerializerOptions"/>; all transport/serialization logic lives here.
    /// </summary>
    /// <remarks>
    /// Thin over Firely's serializer + a standard <see cref="HttpClient"/>. The operations POST a
    /// bare <c>QuestionnaireResponse</c> as the request body (not a <c>Parameters</c> envelope), which
    /// is what the SDC server expects, so Firely's <c>FhirClient</c> operation helpers (which wrap in
    /// <c>Parameters</c>) are deliberately not used.
    /// </remarks>
    /// <typeparam name="TQuestionnaireResponse">The FHIR QuestionnaireResponse type for the bound version.</typeparam>
    /// <typeparam name="TOperationOutcome">The FHIR OperationOutcome type for the bound version.</typeparam>
    /// <typeparam name="TBundle">The FHIR Bundle type for the bound version.</typeparam>
    public abstract class SdcClientBase<TQuestionnaireResponse, TOperationOutcome, TBundle> : IDisposable
        where TQuestionnaireResponse : Resource
        where TOperationOutcome : Resource
        where TBundle : Resource
    {
        private const string FhirJsonMediaType = "application/fhir+json";

        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private readonly JsonSerializerOptions _fhirJson;

        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="fhirJson">FHIR-configured serializer options (built with <c>.ForFhir(...)</c> by the binding).</param>
        /// <param name="httpClient">
        /// Optional pre-configured client (for custom TLS/proxy/timeouts). When supplied, its
        /// <see cref="HttpClient.BaseAddress"/> is set only if unset, leaving handler control to the caller.
        /// When omitted, an internally-owned client is created and disposed with this instance.
        /// </param>
        protected SdcClientBase(Uri baseAddress, JsonSerializerOptions fhirJson, HttpClient httpClient = null)
        {
            if (baseAddress == null) throw new ArgumentNullException(nameof(baseAddress));
            _fhirJson = fhirJson ?? throw new ArgumentNullException(nameof(fhirJson));

            // Trailing slash is required for relative-URI resolution to keep the full base path
            // (e.g. ".../fhir/r5/") instead of dropping the last segment.
            var normalized = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? baseAddress
                : new Uri(baseAddress.AbsoluteUri + "/");

            if (httpClient == null)
            {
                _http = new HttpClient { BaseAddress = normalized };
                _ownsHttpClient = true;
            }
            else
            {
                _http = httpClient;
                if (_http.BaseAddress == null) _http.BaseAddress = normalized;
                _ownsHttpClient = false;
            }
        }

        /// <summary>
        /// Validate a QuestionnaireResponse against its referenced Questionnaire
        /// (<c>POST QuestionnaireResponse/$validate</c>). A validation failure is reported as issues
        /// in the returned <typeparamref name="TOperationOutcome"/>, not as an exception.
        /// </summary>
        public Task<TOperationOutcome> ValidateAsync(TQuestionnaireResponse questionnaireResponse, CancellationToken cancellationToken = default)
            => PostResourceAsync<TOperationOutcome>("QuestionnaireResponse/$validate", questionnaireResponse, cancellationToken);

        /// <summary>
        /// Extract FHIR resources from a QuestionnaireResponse
        /// (<c>POST QuestionnaireResponse/$extract</c>), returning the resulting transaction <typeparamref name="TBundle"/>.
        /// </summary>
        public Task<TBundle> ExtractAsync(TQuestionnaireResponse questionnaireResponse, CancellationToken cancellationToken = default)
            => PostResourceAsync<TBundle>("QuestionnaireResponse/$extract", questionnaireResponse, cancellationToken);

        private async Task<TOut> PostResourceAsync<TOut>(string relativePath, Resource body, CancellationToken cancellationToken)
            where TOut : Resource
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            var json = JsonSerializer.Serialize(body, _fhirJson);

            using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativePath, UriKind.Relative)))
            {
                request.Content = new StringContent(json, Encoding.UTF8, FhirJsonMediaType);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

                using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var responseBody = response.Content == null
                        ? string.Empty
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        // Best-effort: surface a server OperationOutcome if the error body carried one.
                        OperationOutcome outcome = null;
                        try { outcome = JsonSerializer.Deserialize<Resource>(responseBody, _fhirJson) as OperationOutcome; }
                        catch { /* non-FHIR error body; leave outcome null */ }

                        throw new SdcOperationException(
                            relativePath,
                            response.StatusCode,
                            outcome,
                            $"SDC operation '{relativePath}' failed with status {(int)response.StatusCode} {response.StatusCode}.");
                    }

                    Resource parsed;
                    try { parsed = JsonSerializer.Deserialize<Resource>(responseBody, _fhirJson); }
                    catch (Exception ex)
                    {
                        throw new SdcOperationException(relativePath, response.StatusCode, null,
                            $"SDC operation '{relativePath}' returned a body that could not be parsed as FHIR: {ex.Message}");
                    }

                    if (parsed is TOut typed) return typed;

                    throw new SdcOperationException(relativePath, response.StatusCode, parsed as OperationOutcome,
                        $"SDC operation '{relativePath}' returned '{parsed?.TypeName ?? "null"}', expected '{typeof(TOut).Name}'.");
                }
            }
        }

        /// <summary>Disposes the internally-created <see cref="HttpClient"/>; a no-op when one was injected.</summary>
        public void Dispose()
        {
            if (_ownsHttpClient) _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
