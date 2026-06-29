using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Tiro.Health.Telemetry;

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
        private readonly Uri _baseAddress;
        // Null when no telemetry was supplied — every span call below is guarded with ?. so the
        // no-telemetry path allocates nothing and changes no behavior.
        private readonly ITelemetrySession _telemetry;

        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="fhirJson">FHIR-configured serializer options (built with <c>.ForFhir(...)</c> by the binding).</param>
        /// <param name="httpClient">
        /// Optional pre-configured client (for custom TLS/proxy/timeouts, or an
        /// <c>IHttpClientFactory</c>-managed instance). When omitted, an internally-owned client is
        /// created and disposed with this instance. The injected client is never mutated: requests
        /// are sent to absolute URIs resolved from <paramref name="baseAddress"/>, so its
        /// <see cref="HttpClient.BaseAddress"/> is irrelevant and a client shared across several
        /// <see cref="SdcClientBase{TQuestionnaireResponse, TOperationOutcome, TBundle}"/> instances is safe.
        /// </param>
        /// <param name="telemetry">
        /// Optional telemetry session. When supplied, each <c>$validate</c>/<c>$extract</c> round-trip
        /// is recorded as a transaction in this session's trace (operation, target URI, HTTP status,
        /// success/failure). Pass the same session the caller uses elsewhere (e.g. a form-viewer
        /// session) to correlate SDC calls with the surrounding trace. When omitted, no telemetry is
        /// emitted and there is no behavioral change.
        /// </param>
        protected SdcClientBase(Uri baseAddress, JsonSerializerOptions fhirJson, HttpClient httpClient = null, ITelemetrySession telemetry = null)
        {
            if (baseAddress == null) throw new ArgumentNullException(nameof(baseAddress));
            _fhirJson = fhirJson ?? throw new ArgumentNullException(nameof(fhirJson));
            _telemetry = telemetry;

            // A query/fragment on the base can't survive relative-URI resolution
            // (new Uri(base, "QuestionnaireResponse/$validate") drops them per RFC 3986), so they'd
            // be silently lost — fail fast instead. FHIR bases are plain path URLs.
            if (!string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
                throw new ArgumentException(
                    "baseAddress must be a plain path URL with no query or fragment (e.g. https://host/fhir/r5).",
                    nameof(baseAddress));

            // A trailing slash on the PATH is required so relative operation paths resolve against
            // the full base (".../fhir/r5/" + "QuestionnaireResponse/$validate") instead of dropping
            // the last segment.
            _baseAddress = baseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? baseAddress
                : new Uri(baseAddress, baseAddress.AbsolutePath + "/");

            // We resolve absolute request URIs ourselves (see PostResourceAsync), so we never touch
            // the client's BaseAddress — leaving an injected/shared client untouched.
            _http = httpClient ?? new HttpClient();
            _ownsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Construct from an <see cref="SdcConnection"/> — the recommended path when the host also
        /// configures a form viewer, so both share one base address, one transport, and one trace.
        /// Unpacks the connection and delegates to the primitive constructor.
        /// </summary>
        /// <param name="connection">The SDC connection (base address + optional HttpClient + optional session).</param>
        /// <param name="fhirJson">FHIR-configured serializer options (built with <c>.ForFhir(...)</c> by the binding).</param>
        protected SdcClientBase(SdcConnection connection, JsonSerializerOptions fhirJson)
            : this((connection ?? throw new ArgumentNullException(nameof(connection))).BaseAddress,
                   fhirJson,
                   connection.HttpClient,
                   connection.Telemetry)
        {
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

            // Absolute URI from our stored base, so HttpClient.BaseAddress is never consulted or mutated.
            var requestUri = new Uri(_baseAddress, relativePath);

            // One transaction per operation, in the caller's session trace. Name is "sdc.validate" /
            // "sdc.extract" (the segment after '$'). A null session yields a null span, and every
            // span call below is ?.-guarded, so the no-telemetry path allocates nothing.
            var spanName = "sdc." + relativePath.Substring(relativePath.LastIndexOf('$') + 1);
            var span = _telemetry?.StartTransaction(spanName, "http.client");
            span?.SetTag("sdc.operation", relativePath);
            span?.SetTag("http.request.method", "POST");
            span?.SetTag("url.full", requestUri.AbsoluteUri);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUri))
                {
                    request.Content = new StringContent(json, Encoding.UTF8, FhirJsonMediaType);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

                    using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        span?.SetTag("http.response.status_code", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

                        var responseBody = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            // Best-effort: surface a server OperationOutcome if the error body carried one.
                            // Symmetric with the success path — recover the partial result so a newer
                            // server's diagnostics aren't lost when the body has an unrecognized element/code.
                            OperationOutcome outcome = null;
                            try { outcome = JsonSerializer.Deserialize<Resource>(responseBody, _fhirJson) as OperationOutcome; }
                            catch (DeserializationFailedException ex) { outcome = ex.PartialResult as OperationOutcome; }
                            catch { /* non-FHIR error body; leave outcome null */ }

                            throw new SdcOperationException(
                                relativePath,
                                response.StatusCode,
                                outcome,
                                $"SDC operation '{relativePath}' failed with status {(int)response.StatusCode} {response.StatusCode}.");
                        }

                        Resource parsed;
                        try
                        {
                            parsed = JsonSerializer.Deserialize<Resource>(responseBody, _fhirJson);
                        }
                        catch (DeserializationFailedException ex) when (ex.PartialResult is Resource recovered)
                        {
                            // Honor the binding's Recoverable mode: the body parsed into a usable POCO
                            // despite issues the server's (possibly newer) FHIR introduced — e.g. an
                            // unrecognized element or code. JsonSerializer.Deserialize still throws in
                            // Recoverable mode, carrying the partial result on the exception.
                            parsed = recovered;
                        }
                        catch (Exception ex)
                        {
                            throw new SdcOperationException(relativePath, response.StatusCode, null,
                                $"SDC operation '{relativePath}' returned a body that could not be parsed as FHIR: {ex.Message}");
                        }

                        if (parsed is TOut typed) return typed;

                        // Wrong resource type on a success status. If it's an OperationOutcome, surface its
                        // diagnostics in the message (not just on the exception's Outcome) so the failure is legible.
                        var asOutcome = parsed as OperationOutcome;
                        var diagnostic = asOutcome?.Issue?.Count > 0
                            ? asOutcome.Issue[0].Diagnostics ?? asOutcome.Issue[0].Details?.Text
                            : null;
                        var detail = string.IsNullOrEmpty(diagnostic)
                            ? string.Empty
                            : $" Server OperationOutcome: {asOutcome.Issue[0].Severity} — {diagnostic}";
                        throw new SdcOperationException(relativePath, response.StatusCode, asOutcome,
                            $"SDC operation '{relativePath}' returned '{parsed?.TypeName ?? "null"}', expected '{typeof(TOut).Name}'.{detail}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation isn't a server failure — record it distinctly rather than letting the
                // finally below close the span Ok.
                span?.Finish(TelemetrySpanStatus.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                // Covers SdcOperationException (HTTP error / parse failure / wrong type) and any
                // transport exception from SendAsync.
                span?.Finish(ex);
                throw;
            }
            finally
            {
                // Success path: span not yet finished, so Dispose closes it Ok. Error paths already
                // finished above, so this is a no-op (Finish/Dispose are idempotent).
                span?.Dispose();
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
