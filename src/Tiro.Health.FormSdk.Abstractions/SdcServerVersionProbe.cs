using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tiro.Health.FormSdk.Abstractions
{
    /// <summary>
    /// Reads the version of a live SDC server and evaluates it against
    /// <see cref="SdcCompatibility.MinimumSdcVersion"/>.
    /// </summary>
    /// <remarks>
    /// One source: <c>GET {sdcBase}/metadata</c> → <c>CapabilityStatement.software.version</c>.
    /// <para>
    /// The request URI is built by <em>appending</em> to the configured SDC base, so whatever
    /// answers it is the same server the forms and the operations talk to — attribution holds
    /// by construction, and a gateway path prefix survives. That is the whole reason this is
    /// the only source. An earlier revision fell back to <c>GET {origin}/openapi.json</c> for
    /// servers predating this route, but an origin-relative read follows the <em>host</em>, not
    /// the server: behind a gateway that routes <c>/tiro-sdc/fhir/r5</c> to the SDC server and
    /// <c>/</c> to something else, it reads a neighbouring application's version. FastAPI's
    /// default <c>info.version</c> is <c>0.1.0</c>, which parses, compares below any real floor,
    /// and would have refused every form launch against a perfectly healthy server. A source
    /// that cannot be attributed must not be able to fail closed, and one that can only fail
    /// open is not worth the code — so it is gone. A server too old to answer here reads as
    /// <see cref="SdcVersionCheckOutcome.Unknown"/> and is allowed through.
    /// </para>
    /// <para>
    /// Deliberately a plain <see cref="HttpClient"/> <c>GET</c> plus a two-field JSON read,
    /// rather than Firely's <c>FhirClient.CapabilityStatement()</c> or a full
    /// <c>FhirJsonParser.Parse&lt;CapabilityStatement&gt;</c>: this runs on the path to showing
    /// a clinician a form, two strings are all that is needed, <c>CapabilityStatement</c> lives
    /// in <c>Hl7.Fhir.Conformance</c> (which neither consuming core package references), and a
    /// single-field read cannot trip over an element a newer server emits. The document is
    /// ~530 bytes and sends <c>ETag</c> + <c>Cache-Control: public, max-age=300</c>; conditional
    /// requests are supported by the server but not worth the complexity for a startup check.
    /// </para>
    /// </remarks>
    public static class SdcServerVersionProbe
    {
        /// <summary>
        /// The probe's deadline. Bounded on purpose: a startup check must not become the reason
        /// a form takes long to appear. Note a supplied <see cref="HttpClient"/> with a shorter
        /// <see cref="HttpClient.Timeout"/> will fire first.
        /// </summary>
        /// <remarks>
        /// Not a <c>const</c>: a <c>const</c> is substituted into consuming assemblies at their
        /// compile time, so a consumer built against an older package would keep reporting a
        /// value this one no longer uses.
        /// </remarks>
        public static readonly int TimeoutMilliseconds = 3000;

        /// <summary>
        /// Hard cap on how much of a response body is read. A safety valve against an unbounded
        /// or hostile stream, not an expected limit: the real document is ~530 bytes.
        /// </summary>
        private const int MaxResponseBytes = 2 * 1024 * 1024;

        /// <summary>
        /// What the SDC server reports as <c>CapabilityStatement.software.name</c>. Checked when
        /// present, as belt-and-braces against a gateway routing <c>{base}/metadata</c> to a
        /// different FHIR server than the one serving the operations. Absence is tolerated
        /// rather than treated as a mismatch: every real server sets it, and requiring it would
        /// add a way for a future server-side change to silently disarm the check.
        /// </summary>
        private const string SdcServerSoftwareName = "Tiro.health SDC Server";

        private const string FhirJsonMediaType = "application/fhir+json";

        // One process-wide client for callers that don't supply one (the form viewer has no
        // HttpClient of its own). A client per probe would burn sockets in TIME_WAIT; a static
        // one is the documented remedy. No Timeout is set: the deadline comes from a linked
        // CancellationTokenSource, which is the only way to bound an injected or shared client
        // without mutating it. No custom HttpClientHandler either — the one document read here
        // is ~530 bytes, so compression would buy nothing, and a handler that threw in a static
        // initializer would poison this type for the whole process.
        private static readonly HttpClient SharedClient = new HttpClient();

        /// <summary>
        /// Probes <paramref name="sdcBaseAddress"/> and returns the verdict. Never throws for a
        /// server-side or transport problem — those are <see cref="SdcVersionCheckOutcome.Unknown"/>,
        /// carrying the reason in <see cref="SdcVersionCheckResult.Detail"/>. Throws only
        /// <see cref="ArgumentNullException"/>/<see cref="ArgumentException"/> for a bad
        /// <paramref name="sdcBaseAddress"/> (a caller bug, not a server condition), and
        /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
        /// is cancelled.
        /// </summary>
        /// <param name="sdcBaseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">
        /// Optional client, so the probe travels the same TLS/proxy/auth path as the operations
        /// it guards. When omitted an internally-owned, process-wide client is used — which
        /// means the probe is <em>unauthenticated</em>. A server that requires a credential on
        /// <c>/metadata</c> will answer 401/403, which is an <see cref="SdcVersionCheckOutcome.Unknown"/>,
        /// i.e. fails open.
        /// </param>
        /// <param name="cancellationToken">Caller's token; linked with the probe's own deadline.</param>
        public static async Task<SdcVersionCheckResult> CheckAsync(
            Uri sdcBaseAddress,
            HttpClient httpClient = null,
            CancellationToken cancellationToken = default)
        {
            if (sdcBaseAddress == null) throw new ArgumentNullException(nameof(sdcBaseAddress));
            if (!sdcBaseAddress.IsAbsoluteUri)
                throw new ArgumentException("sdcBaseAddress must be an absolute URI.", nameof(sdcBaseAddress));

            // A trailing slash on the PATH is required so "metadata" resolves against the full
            // base (".../fhir/r5/" + "metadata") instead of replacing the last segment.
            var baseWithSlash = sdcBaseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? sdcBaseAddress
                : new Uri(sdcBaseAddress, sdcBaseAddress.AbsolutePath + "/");

            var requestUri = new Uri(baseWithSlash, "metadata");
            var read = await ReadSoftwareAsync(httpClient ?? SharedClient, requestUri, cancellationToken)
                .ConfigureAwait(false);

            if (read.Version == null)
                return SdcVersionCheckResult.Unavailable(read.Detail);

            // Attribution guard; see SdcServerSoftwareName. Reported as "unknown" (fail open),
            // never as "too old" — a document we cannot attribute must not refuse a session.
            if (read.Name != null && !string.Equals(read.Name, SdcServerSoftwareName, StringComparison.Ordinal))
                return SdcVersionCheckResult.Unavailable(
                    $"GET {requestUri} → a CapabilityStatement whose software.name is " +
                    $"'{SdcVersionCheckResult.Clamp(read.Name)}', not '{SdcServerSoftwareName}'. " +
                    "Its version is not the SDC server's and was not used.");

            return SdcVersionCheckResult.FromReportedVersion(
                read.Version, SdcVersionCheckResult.CapabilityStatementSource);
        }

        // Returns software.name/software.version from the CapabilityStatement, or a null Version
        // plus the reason. Name may be null even on success (it is optional to us).
        private static async Task<(string Name, string Version, string Detail)> ReadSoftwareAsync(
            HttpClient http, Uri requestUri, CancellationToken cancellationToken)
        {
            // Checked before anything is issued: an already-cancelled caller must not get a
            // request sent on their behalf, and must not be able to reach the deadline handling
            // below (the linked source is born cancelled, so every branch there would read as
            // "timed out" and hand back a fail-open result instead of honouring the cancel).
            cancellationToken.ThrowIfCancellationRequested();

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeoutMilliseconds);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
                    {
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

                        // ResponseHeadersRead so a non-success status costs no body transfer —
                        // which matters for the 400 a server predating this route returns.
                        using (var response = await http
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                            .ConfigureAwait(false))
                        // The deadline has to be able to interrupt the BODY read too, not just
                        // the send. On net48, Stream.ReadAsync(byte[],int,int,ct) is the base
                        // Begin/EndRead wrapper and ignores its token; and because we asked for
                        // ResponseHeadersRead, SendAsync's own cancellation stopped covering the
                        // response at the headers. Without this registration a server that sends
                        // headers and then stalls hangs the read with no ceiling at all — capped
                        // at the launch budget in the viewer, and UNBOUNDED in SdcClient, which
                        // has no outer deadline. Disposing the response kills the stream, which
                        // is the only portable way to unblock that read. Dispose is idempotent,
                        // so racing the enclosing using is safe.
                        using (deadline.Token.Register(() => { try { response.Dispose(); } catch { /* already gone */ } }))
                        {
                            if (!response.IsSuccessStatusCode)
                                return (null, null, $"GET {requestUri} → {(int)response.StatusCode} {response.StatusCode}.");

                            var body = await ReadCappedAsync(response, deadline.Token).ConfigureAwait(false);
                            if (body == null)
                                return (null, null, $"GET {requestUri} → body exceeded {MaxResponseBytes} bytes.");
                            if (body.Length == 0)
                                return (null, null, $"GET {requestUri} → {(int)response.StatusCode} with an empty body.");

                            var software = ReadSoftware(body);
                            if (software.Version == null)
                                return (null, null,
                                    $"GET {requestUri} → {(int)response.StatusCode} without a string software.version.");

                            return (software.Name, software.Version, string.Empty);
                        }
                    }
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller cancelled mid-flight — their intent, not a probe failure. This
                    // must be tested BEFORE the deadline branch below, because the deadline
                    // source is linked to the caller's token and so reads as cancelled too.
                    // Catching Exception rather than OperationCanceledException on purpose: the
                    // response-dispose registration turns a cancel into ObjectDisposedException
                    // or IOException from the body read, and none of those may become a
                    // fail-open result. Rethrown against THEIR token, so a call site's
                    // `catch (OperationCanceledException e) when (e.CancellationToken == mine)`
                    // matches — the exception HttpClient raised carries the linked one instead.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    // Our own deadline, and only ours (the caller's case was handled above).
                    // Reached as OperationCanceledException from the send, or as
                    // ObjectDisposedException/IOException from a body read the registration
                    // unblocked. Claimed only when our CTS actually fired, so a supplied
                    // client's shorter Timeout is never misreported as this number.
                    return (null, null, $"GET {requestUri} → timed out after {TimeoutMilliseconds} ms.");
                }
                catch (Exception ex)
                {
                    // Every other transport failure (DNS, TLS, refused connection, proxy, a
                    // supplied client's own Timeout). Fail open: the version is unknown, which
                    // must never brick a deployment.
                    return (null, null, $"GET {requestUri} → {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // null = exceeded MaxResponseBytes; empty array = no body. Both are distinguishable by
        // the caller, which reports them differently.
        private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content == null) return new byte[0];

            // Content-Length is deliberately NOT consulted: a proxy that advertises a wrong,
            // oversized length would disarm the check on a body the loop below handles fine.
            // The loop is the real cap.
            //
            // No cancellable ReadAsStreamAsync overload on net48; the response-dispose
            // registration in the caller is what actually interrupts a stalled read.
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

        private static (string Name, string Version) ReadSoftware(byte[] utf8Json)
        {
            try
            {
                // JsonDocument rejects a leading UTF-8 BOM, which a hand-written server or a
                // proxy can prepend; skip it rather than reporting a malformed body.
                var start = utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF ? 3 : 0;
                using (var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(utf8Json, start, utf8Json.Length - start)))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return (null, null);
                    if (!root.TryGetProperty("software", out var software) || software.ValueKind != JsonValueKind.Object)
                        return (null, null);
                    return (ReadString(software, "name"), ReadString(software, "version"));
                }
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static string ReadString(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
