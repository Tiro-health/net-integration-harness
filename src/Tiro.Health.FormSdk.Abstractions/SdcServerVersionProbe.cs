using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tiro.Health.FormSdk.Abstractions
{
    /// <summary>
    /// Reads the version of a live SDC server and evaluates it against
    /// <see cref="SdcCompatibility.MinimumSdcVersion"/>. Two sources are tried in order; the
    /// second exists because no deployed server answers the first yet.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>
    /// <b><c>GET {sdcBase}/metadata</c></b> → <c>CapabilityStatement.software.version</c>.
    /// Resolved <em>base-relative</em>, so it survives a gateway path prefix, and it is the
    /// spec's own field for "version of the software running". ~530 bytes, sent with
    /// <c>ETag</c> and <c>Cache-Control: public, max-age=300</c>. Conditional requests are
    /// supported by the server but not worth the complexity for a startup check.
    /// </item>
    /// <item>
    /// <b><c>GET {origin}/openapi.json</c></b> → <c>info.version</c> — the same string from
    /// the same <c>VERSION_INFO</c>, and the only source the entire currently-deployed fleet
    /// answers (as of writing, <c>sdc.tiro.health/fhir/r5/metadata</c> still returns 400 while
    /// its <c>/openapi.json</c> reports <c>v0.9.38</c>). Origin-relative, so unlike step 1 it
    /// does not survive a gateway path prefix — accepted, because it is a fallback whose whole
    /// purpose is the pre-<c>/metadata</c> installed base. It is also two orders of magnitude
    /// larger (~235 KB, ~26 KB gzipped), which is why it is tried second and why the
    /// internally-owned client requests compression. This step is droppable once no supported
    /// server predates the <c>/metadata</c> route.
    /// </item>
    /// <item>Neither answered → <see cref="SdcVersionCheckOutcome.Unknown"/>, i.e. fail open.</item>
    /// </list>
    /// <para>
    /// Deliberately a plain <see cref="HttpClient"/> <c>GET</c> plus a single-field JSON read,
    /// rather than Firely's <c>FhirClient.CapabilityStatement()</c> or a full
    /// <c>FhirJsonParser.Parse&lt;CapabilityStatement&gt;</c>: this runs on the path to showing
    /// a clinician a form, one string is all that's needed, <c>CapabilityStatement</c> lives in
    /// <c>Hl7.Fhir.Conformance</c> (which neither consuming core package references), and a
    /// single-field read cannot trip over an element a newer server emits. It also lets both
    /// sources be handled identically.
    /// </para>
    /// </remarks>
    public static class SdcServerVersionProbe
    {
        /// <summary>
        /// Per-attempt deadline. Two attempts, so the worst case a caller can wait is twice
        /// this — bounded on purpose, because a startup check must not become the reason a
        /// form takes long to appear.
        /// </summary>
        public const int AttemptTimeoutMilliseconds = 3000;

        /// <summary>
        /// Hard cap on how much of a response body is read. A safety valve against an
        /// unbounded stream, not an expected limit: the largest real body here is
        /// <c>/openapi.json</c> at a few hundred KB.
        /// </summary>
        private const int MaxResponseBytes = 2 * 1024 * 1024;

        private const string FhirJsonMediaType = "application/fhir+json";
        private const string JsonMediaType = "application/json";

        // One process-wide client for callers that don't supply one (the form viewer has no
        // HttpClient of its own). A client per probe would burn sockets in TIME_WAIT; a static
        // one is the documented remedy. Decompression is enabled here because /openapi.json is
        // ~235 KB uncompressed and ~26 KB gzipped — an injected client keeps whatever its owner
        // configured, which is the right trade for a client that also carries their TLS/proxy
        // and any auth headers. No Timeout is set: the deadline comes from a linked
        // CancellationTokenSource, which is the only way to bound an injected/shared client
        // without mutating it.
        private static readonly HttpClient SharedClient = CreateSharedClient();

        private static HttpClient CreateSharedClient()
        {
            var handler = new HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return new HttpClient(handler);
        }

        /// <summary>
        /// Probes <paramref name="sdcBaseAddress"/> and returns the verdict. Never throws for
        /// a server-side or transport problem — those are <see cref="SdcVersionCheckOutcome.Unknown"/>,
        /// carrying the reason in <see cref="SdcVersionCheckResult.Detail"/>. Only a
        /// cancellation requested through <paramref name="cancellationToken"/> propagates.
        /// </summary>
        /// <param name="sdcBaseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">
        /// Optional client, so the probe travels the same TLS/proxy/auth path as the operations
        /// it guards. When omitted an internally-owned, process-wide client is used — which
        /// means the probe is <em>unauthenticated</em>. A server that requires a credential on
        /// <c>/metadata</c> will answer 401/403, which is an <see cref="SdcVersionCheckOutcome.Unknown"/>,
        /// i.e. fails open.
        /// </param>
        /// <param name="cancellationToken">Caller's token; linked with the per-attempt deadline.</param>
        public static async Task<SdcVersionCheckResult> CheckAsync(
            Uri sdcBaseAddress,
            HttpClient httpClient = null,
            CancellationToken cancellationToken = default)
        {
            if (sdcBaseAddress == null) throw new ArgumentNullException(nameof(sdcBaseAddress));
            if (!sdcBaseAddress.IsAbsoluteUri)
                throw new ArgumentException("sdcBaseAddress must be an absolute URI.", nameof(sdcBaseAddress));

            var http = httpClient ?? SharedClient;

            // A trailing slash on the PATH is required so "metadata" resolves against the full
            // base (".../fhir/r5/" + "metadata") instead of replacing the last segment.
            var baseWithSlash = sdcBaseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? sdcBaseAddress
                : new Uri(sdcBaseAddress, sdcBaseAddress.AbsolutePath + "/");

            var capability = await ReadVersionAsync(
                http, new Uri(baseWithSlash, "metadata"), FhirJsonMediaType,
                "software", "version", cancellationToken).ConfigureAwait(false);
            if (capability.Version != null)
                return SdcVersionCheckResult.FromReportedVersion(
                    capability.Version, SdcVersionCheckResult.CapabilityStatementSource);

            var openApi = await ReadVersionAsync(
                http, new Uri(sdcBaseAddress, "/openapi.json"), JsonMediaType,
                "info", "version", cancellationToken).ConfigureAwait(false);
            if (openApi.Version != null)
                return SdcVersionCheckResult.FromReportedVersion(
                    openApi.Version, SdcVersionCheckResult.OpenApiSource);

            return SdcVersionCheckResult.Unavailable(
                $"{capability.Detail} {openApi.Detail}");
        }

        // Returns the string at the given two-level JSON path, or null with a reason.
        private static async Task<(string Version, string Detail)> ReadVersionAsync(
            HttpClient http, Uri requestUri, string accept, string outerProperty, string innerProperty,
            CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(AttemptTimeoutMilliseconds);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
                    {
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

                        // ResponseHeadersRead so a non-success status costs no body transfer —
                        // which matters for the 400 every pre-/metadata server returns here.
                        using (var response = await http
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                            .ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                                return (null, $"GET {requestUri} → {(int)response.StatusCode} {response.StatusCode}.");

                            var body = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);
                            if (body == null)
                                return (null, $"GET {requestUri} → body exceeded {MaxResponseBytes} bytes.");

                            var version = ReadJsonString(body, outerProperty, innerProperty);
                            return version != null
                                ? (version, string.Empty)
                                : (null, $"GET {requestUri} → 200 without a string {outerProperty}.{innerProperty}.");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller cancelled — their intent, not a probe failure.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return (null, $"GET {requestUri} → timed out after {AttemptTimeoutMilliseconds} ms.");
                }
                catch (Exception ex)
                {
                    // Every transport failure lands here (DNS, TLS, refused connection, proxy).
                    // Fail open: the version is unknown, which must never brick a deployment.
                    return (null, $"GET {requestUri} → {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content == null) return new byte[0];
            if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;

            // No cancellable ReadAsStreamAsync overload on net48; the per-read token below is
            // what actually bounds this, and SendAsync's token already aborts the request.
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffered = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffered.Length + read > MaxResponseBytes) return null;
                    buffered.Write(chunk, 0, read);
                }
                return buffered.ToArray();
            }
        }

        private static string ReadJsonString(byte[] utf8Json, string outerProperty, string innerProperty)
        {
            try
            {
                // JsonDocument rejects a leading UTF-8 BOM, which a hand-written server or a
                // proxy can prepend; skip it rather than reporting a malformed body.
                var start = utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF ? 3 : 0;
                using (var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(utf8Json, start, utf8Json.Length - start)))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty(outerProperty, out var outer) || outer.ValueKind != JsonValueKind.Object) return null;
                    if (!outer.TryGetProperty(innerProperty, out var inner) || inner.ValueKind != JsonValueKind.String) return null;
                    var value = inner.GetString();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
