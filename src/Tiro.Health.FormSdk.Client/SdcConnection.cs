using System;
using System.Net.Http;
using Tiro.Health.Telemetry;

namespace Tiro.Health.FormSdk.Client
{
    /// <summary>
    /// Single source of truth for reaching an SDC server: the FHIR base address, an optional
    /// pre-configured <see cref="HttpClient"/> (custom TLS/proxy/timeouts), and an optional
    /// <see cref="ITelemetrySession"/> to fold calls into a surrounding trace.
    /// </summary>
    /// <remarks>
    /// A host that both embeds a form viewer and calls <c>$validate</c>/<c>$extract</c> builds one
    /// of these and hands it to both, so the rendered form and the client can't point at different
    /// servers or land in different traces.
    /// <para>
    /// Immutable, and a plain config bag: it does <b>not</b> own or dispose the
    /// <see cref="HttpClient"/> or the <see cref="ITelemetrySession"/> — that ownership stays with
    /// whoever created them. The same instance is safe to share across several clients.
    /// </para>
    /// </remarks>
    public sealed class SdcConnection
    {
        /// <summary>The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</summary>
        public Uri BaseAddress { get; }

        /// <summary>
        /// Optional pre-configured client (custom TLS/proxy/timeouts, or an
        /// <c>IHttpClientFactory</c>-managed instance). <c>null</c> ⇒ the client creates and owns
        /// its own. Never mutated by the client.
        /// </summary>
        public HttpClient HttpClient { get; }

        /// <summary>
        /// Optional telemetry session. When supplied, each <c>$validate</c>/<c>$extract</c>
        /// round-trip is recorded as a transaction in this session's trace. Pass the session the
        /// host uses elsewhere (e.g. a form viewer's <c>TelemetrySession</c>) to correlate SDC
        /// calls with the surrounding form-session trace. <c>null</c> ⇒ no telemetry.
        /// </summary>
        public ITelemetrySession Telemetry { get; }

        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client; <c>null</c> lets the client own its own.</param>
        /// <param name="telemetry">Optional telemetry session; <c>null</c> for no telemetry.</param>
        public SdcConnection(Uri baseAddress, HttpClient httpClient = null, ITelemetrySession telemetry = null)
        {
            BaseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
            HttpClient = httpClient;
            Telemetry = telemetry;
        }

        /// <summary>Convenience overload taking the base address as an absolute URL string.</summary>
        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client; <c>null</c> lets the client own its own.</param>
        /// <param name="telemetry">Optional telemetry session; <c>null</c> for no telemetry.</param>
        public SdcConnection(string baseAddress, HttpClient httpClient = null, ITelemetrySession telemetry = null)
            : this(new Uri(baseAddress ?? throw new ArgumentNullException(nameof(baseAddress)), UriKind.Absolute),
                   httpClient, telemetry)
        {
        }

        /// <summary>
        /// Returns a copy with <paramref name="telemetry"/> attached — for pairing the connection
        /// with a session that only becomes available after construction (e.g. a viewer's session,
        /// minted when the viewer is constructed).
        /// </summary>
        public SdcConnection WithTelemetry(ITelemetrySession telemetry)
            => new SdcConnection(BaseAddress, HttpClient, telemetry);
    }
}
