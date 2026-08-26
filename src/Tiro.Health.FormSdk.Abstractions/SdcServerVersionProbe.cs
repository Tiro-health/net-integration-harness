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
    /// One source: <c>GET {sdcBase}/metadata</c> → <c>CapabilityStatement.software.version</c>,
    /// accepted only from a document that identifies itself as the SDC server.
    /// <para>
    /// The request URI is built by <em>appending</em> to the configured SDC base, so the request
    /// reaches the same host the forms and the operations talk to, and a gateway path prefix
    /// survives. That is necessary but <b>not</b> sufficient for attribution, and it is worth
    /// being precise about why: on a server predating this route, <c>{base}/metadata</c> has no
    /// local handler and falls into the SDC server's data tunnel, which proxies it to the
    /// configured data endpoint — so a self-hosted deployment with <c>DEFAULT_DATA_ENDPOINT</c>
    /// set answers with the <em>hospital's own</em> CapabilityStatement. Base-relativity gets
    /// the request to the right host; only the body can say who composed it. Hence the
    /// <c>software.name</c> requirement below, which is what actually makes the read attributable.
    /// </para>
    /// <para>
    /// An earlier revision also fell back to <c>GET {origin}/openapi.json</c>. That source was
    /// worse still — origin-relative, so it followed the <em>host</em> rather than the server:
    /// behind a gateway routing <c>/tiro-sdc/fhir/r5</c> to the SDC server and <c>/</c> to
    /// something else it read a neighbouring application, and FastAPI's default
    /// <c>info.version</c> of <c>0.1.0</c> parses and sorts below any real floor. It is gone.
    /// Losing it costs nothing: a server too old to answer <c>metadata</c> is also older than
    /// any floor this harness declares, so it reads as
    /// <see cref="SdcVersionCheckOutcome.Unknown"/> and is allowed through — which is the same
    /// outcome, reached without a read nobody can attribute.
    /// </para>
    /// <para>
    /// Deliberately a plain <see cref="HttpClient"/> <c>GET</c> plus a two-field JSON read,
    /// rather than Firely's <c>FhirClient.CapabilityStatement()</c> or a full
    /// <c>FhirJsonParser.Parse&lt;CapabilityStatement&gt;</c>: this runs on the path to showing
    /// a clinician a form, two strings are all that is needed, <c>CapabilityStatement</c> lives
    /// in <c>Hl7.Fhir.Conformance</c> (which neither consuming core package references), and a
    /// single-field read cannot trip over an element a newer server emits. The document is
    /// ~530 bytes, and it is re-fetched per probe: the server sends <c>ETag</c> and
    /// <c>Cache-Control</c>, but nothing here issues a conditional request and
    /// <see cref="HttpClient"/> has no response cache, so no caching benefit is claimed.
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
        /// or hostile stream, not an expected limit: the real document is ~530 bytes, so this is
        /// already two orders of magnitude of headroom.
        /// </summary>
        private const int MaxResponseBytes = 64 * 1024;

        /// <summary>
        /// What the SDC server reports as <c>CapabilityStatement.software.name</c>. This is the
        /// attribution signal: the version is used only when it matches. It is <b>required</b>,
        /// not merely checked when present — <c>software.name</c> is <c>1..1</c> whenever
        /// <c>software</c> is present in R4 and R5, so a conformant server cannot drop it and
        /// requiring it adds no way for a legitimate server-side change to disarm the check. A
        /// document that omits it is by definition non-conformant, which is exactly the class
        /// (a tunnelled response, a hand-written server, a proxy) that must not be trusted to
        /// refuse a session.
        /// <para>
        /// Compared case-insensitively and trimmed on purpose. The literal has a lowercase
        /// <c>h</c> in "health"; a cosmetic capitalization change on the server would otherwise
        /// disarm this gate in every already-shipped binary, and no reading of the string's
        /// intent depends on its case.
        /// </para>
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
            if (!string.Equals(read.Name?.Trim(), SdcServerSoftwareName, StringComparison.OrdinalIgnoreCase))
                return SdcVersionCheckResult.Unavailable(
                    $"GET {requestUri} → a CapabilityStatement whose software.name is " +
                    $"{Describe(read.Name)}, not '{SdcServerSoftwareName}'. " +
                    "Its version is not the SDC server's and was not used.");

            return SdcVersionCheckResult.FromReportedVersion(read.Version);
        }

        private static string Describe(string name)
            => name == null ? "absent" : $"'{SdcVersionCheckResult.Clamp(name)}'";

        // Returns software.name/software.version from the CapabilityStatement, or a null Version
        // plus the reason. A null Name is a failed attribution, handled by the caller.
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
                        // has no outer deadline. Disposing the response is what unblocks it, for
                        // every stream whose read observes disposal — which is the real ones, but
                        // not a guarantee: a stream that ignores both its token and its own
                        // disposal can still outrun the deadline, and one that answers a torn
                        // read with 0 surfaces as "no software.version" rather than a timeout.
                        // Dispose is idempotent, and CancellationTokenRegistration.Dispose waits
                        // for a running callback, so racing the enclosing using is safe.
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
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && IsExpectedFailure(ex))
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
                catch (Exception ex) when (deadline.IsCancellationRequested && IsExpectedFailure(ex))
                {
                    // Our own deadline, and only ours (the caller's case was handled above).
                    // Reached as OperationCanceledException from the send, or as
                    // ObjectDisposedException/IOException from a body read the registration
                    // unblocked. Claimed only when our CTS actually fired, so a supplied
                    // client's shorter Timeout is never misreported as this number.
                    _ = ex;
                    return (null, null, $"GET {requestUri} → timed out after {TimeoutMilliseconds} ms.");
                }
                catch (Exception ex)
                {
                    // Every other transport failure (DNS, TLS, refused connection, proxy, a
                    // supplied client's own Timeout) — and any genuine defect, which lands here
                    // rather than being relabelled by the two filtered catches above. Fail open:
                    // the version is unknown, which must never brick a deployment. The message
                    // is clamped because an injected DelegatingHandler can make it arbitrarily
                    // long, and this string reaches a log line and a telemetry breadcrumb.
                    return (null, null,
                        $"GET {requestUri} → {ex.GetType().Name}: {SdcVersionCheckResult.Clamp(ex.Message)}");
                }

                // Only these become "cancelled" or "timed out". A NullReferenceException from a
                // broken handler that merely coincides with a cancel is a defect, and must be
                // reported as one instead of being dressed up as a deadline.
                bool IsExpectedFailure(Exception ex)
                    => ex is OperationCanceledException
                    || ex is ObjectDisposedException
                    || ex is IOException
                    || ex is HttpRequestException;
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
