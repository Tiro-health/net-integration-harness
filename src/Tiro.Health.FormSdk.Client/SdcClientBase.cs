using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Tiro.Health.FormSdk.Abstractions;

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

        // The one-time SDC server version check (GH-62); null until the first operation starts
        // it. The TASK is cached, and it is started with CancellationToken.None so that caching
        // it is safe: a task bound to the first caller's token would be poisoned for every later
        // operation the moment that caller cancelled. Caching the *result* instead had a worse
        // failure — two concurrent first operations would each probe, and if one came back
        // Unknown (a transient blip) while the other came back TooOld, the Unknown caller would
        // POST to a server the gate exists to refuse. One task means one verdict for everyone.
        //
        // Started under a lock rather than published with a CAS. A CAS looked like it made "one
        // probe" a property of the code and did not: CheckAsync is an async method, so calling
        // it issues the GET immediately and the CAS only chose which already-in-flight task to
        // keep — eight concurrent first operations made eight requests. The lock gates the
        // start, which is the thing that had to be gated.
        private volatile Task<SdcVersionCheckResult> _versionCheckTask;
        private readonly object _versionCheckGate = new object();

        // The completed verdict, for ServerVersionCheck. Written after the await rather than read
        // off the task so the property never blocks and never rethrows. Assigned with a CAS so
        // the "trace once" branch below cannot fire twice.
        private SdcVersionCheckResult _versionCheck;

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

        /// <summary>
        /// The outcome of this client's SDC server version check, or <c>null</c> until the
        /// first operation has run it. Exposed because the check's telemetry lands in the
        /// <em>customer's</em> logs, not Tiro's — they self-host the server — so a host that
        /// wants to surface "the version could not be established" in its own diagnostics
        /// needs a way to read it. See <see cref="SdcCompatibility.MinimumSdcVersion"/>.
        /// </summary>
        public SdcVersionCheckResult ServerVersionCheck => Volatile.Read(ref _versionCheck);

        /// <summary>
        /// Runs the SDC server version check once per client instance, before the first
        /// operation reaches the server (GH-62). Reports; never refuses — see the note at the
        /// end of the method body.
        /// </summary>
        /// <remarks>
        /// Every outcome lets the operation through. A too-old server is reported as an
        /// actionable warning ("upgrade the SDC server"), an unreadable version as a diagnostic
        /// about the check itself. "Reported" is <see cref="Trace"/> here rather than a logger,
        /// because this client is deliberately telemetry-free (GH-33 tracks an optional
        /// <c>ILogger</c> seam); <see cref="ServerVersionCheck"/> is the programmatic view.
        /// <para>
        /// The check goes through the same <see cref="HttpClient"/> as the operations, so a
        /// host that injected one for custom TLS/proxy/auth has that apply to the probe too.
        /// Unlike the viewer's, it is strictly serial in front of the first operation, so that
        /// operation pays one extra round trip (bounded by
        /// <see cref="SdcServerVersionProbe.TimeoutMilliseconds"/>).
        /// </para>
        /// </remarks>
        private async Task EnsureServerVersionSupportedAsync(CancellationToken cancellationToken)
        {
            // Before anything else: a caller who has already cancelled must not be made to wait
            // on a probe, and must get their own cancellation back rather than a pairing verdict.
            cancellationToken.ThrowIfCancellationRequested();

            var probe = _versionCheckTask;
            if (probe == null)
            {
                lock (_versionCheckGate)
                {
                    // CancellationToken.None on purpose — see the field comment. The probe's own
                    // deadline is what bounds it.
                    probe = _versionCheckTask
                        ?? (_versionCheckTask = SdcServerVersionProbe.CheckAsync(_baseAddress, _http, CancellationToken.None));
                }
            }

            // The shared task deliberately ignores the caller's token, so the WAIT has to honour
            // it — otherwise cancelling an operation still blocked for the probe's full deadline
            // and then threw a pairing exception instead of a cancellation.
            var result = await WaitAsync(probe, cancellationToken).ConfigureAwait(false);

            // First-wins, atomically, so concurrent awaiters of the same task cannot both trace.
            if (Interlocked.CompareExchange(ref _versionCheck, result, null) == null)
            {
                // Loud, once. Trace rather than a logger because this client is deliberately
                // telemetry-free (GH-33 tracks an optional ILogger seam), and because the
                // audience is the customer's own logs: they self-host the server.
                // Trace and nothing else, deliberately: this client is telemetry-free (GH-33
                // tracks an optional ILogger seam) and ServerVersionCheck is the programmatic
                // view. The viewer, which has a telemetry sink, also captures a message.
                if (result.Outcome != SdcVersionCheckOutcome.Satisfied)
                    Trace.TraceWarning("Tiro.Health.FormSdk.Client: " + result);
            }

            // WHEN THE FLOOR IS FIRST RAISED FOR A REAL REASON, refuse here:
            //
            //     if (result.Outcome == SdcVersionCheckOutcome.TooOld)
            //         throw new SdcServerTooOldException(result);
            //
            // See the matching note in TiroFormViewer.ApplySdcVersionCheckAsync for why not yet:
            // enforcement and the floor ship in the same assembly, so fielding the throw early
            // protects nobody, while the current floor is the first version that can answer the
            // probe at all — so the throw could only ever fire on a mistake.
        }

        /// <summary>
        /// Awaits <paramref name="task"/> but gives up when <paramref name="cancellationToken"/>
        /// is cancelled. The task itself is not cancelled — it is shared, so one caller's
        /// cancellation must not decide it for the others.
        /// </summary>
        /// <remarks>
        /// A local polyfill because <c>Task.WaitAsync</c> arrived in .NET 6 and this package
        /// targets <c>netstandard2.0</c>/<c>net48</c>. The messaging package has the same helper,
        /// but this client deliberately does not reference it (GH-37 Decision 1).
        /// </remarks>
        private static Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted || !cancellationToken.CanBeCanceled) return task;

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            task.ContinueWith(completed =>
            {
                registration.Dispose();
                if (completed.IsFaulted) tcs.TrySetException(completed.Exception.InnerExceptions);
                else if (completed.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(completed.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return tcs.Task;
        }

        private async Task<TOut> PostResourceAsync<TOut>(string relativePath, Resource body, CancellationToken cancellationToken)
            where TOut : Resource
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            await EnsureServerVersionSupportedAsync(cancellationToken).ConfigureAwait(false);

            var json = JsonSerializer.Serialize(body, _fhirJson);

            // Absolute URI from our stored base, so HttpClient.BaseAddress is never consulted or mutated.
            var requestUri = new Uri(_baseAddress, relativePath);

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUri))
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

        /// <summary>Disposes the internally-created <see cref="HttpClient"/>; a no-op when one was injected.</summary>
        public void Dispose()
        {
            if (_ownsHttpClient) _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
