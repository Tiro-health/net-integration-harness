using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

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

        // Identifies this harness to the SDC server, which aggregates it to learn which
        // harness versions are deployed in the field (GH-63 / atticus-backend#3568) —
        // the data behind the support window. Computed once; reflection is not per-request.
        private static readonly ProductInfoHeaderValue UserAgent = BuildUserAgent();

        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private readonly JsonSerializerOptions _fhirJson;
        private readonly Uri _baseAddress;

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
        protected SdcClientBase(Uri baseAddress, JsonSerializerOptions fhirJson, HttpClient httpClient = null)
        {
            if (baseAddress == null) throw new ArgumentNullException(nameof(baseAddress));
            _fhirJson = fhirJson ?? throw new ArgumentNullException(nameof(fhirJson));

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

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUri))
            {
                request.Content = new StringContent(json, Encoding.UTF8, FhirJsonMediaType);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

                // Per-request, never on DefaultRequestHeaders: an injected client may be
                // shared (IHttpClientFactory) and must not be mutated. Any User-Agent the
                // consumer configured is carried over first — a per-request header would
                // otherwise suppress it, and hospital proxies may allowlist on it.
                foreach (var product in _http.DefaultRequestHeaders.UserAgent)
                    request.Headers.UserAgent.Add(product);
                request.Headers.UserAgent.Add(UserAgent);

                using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
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

        private static ProductInfoHeaderValue BuildUserAgent()
        {
            var asm = typeof(SdcClientBase<TQuestionnaireResponse, TOperationOutcome, TBundle>).Assembly;

            // InformationalVersion carries the package version (the publish workflow passes
            // -p:Version), but the SDK appends "+<commit sha>" — not wanted in a UA token.
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plus = informational?.IndexOf('+') ?? -1;
            if (plus >= 0) informational = informational.Substring(0, plus);

            foreach (var candidate in new[] { informational, asm.GetName().Version?.ToString(), "0.0.0" })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                // A version with characters HTTP tokens disallow would throw on every
                // request; try the next candidate instead.
                try { return new ProductInfoHeaderValue("Tiro.Health.FormSdk.Client", candidate); }
                catch (FormatException) { }
            }

            return new ProductInfoHeaderValue("Tiro.Health.FormSdk.Client", "0.0.0");
        }

        /// <summary>Disposes the internally-created <see cref="HttpClient"/>; a no-op when one was injected.</summary>
        public void Dispose()
        {
            if (_ownsHttpClient) _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
