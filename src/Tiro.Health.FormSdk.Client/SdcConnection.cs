using System;
using System.Net.Http;

namespace Tiro.Health.FormSdk.Client
{
    /// <summary>
    /// Single source of truth for reaching an SDC server: the FHIR base address and an optional
    /// pre-configured <see cref="HttpClient"/> (custom TLS/proxy/timeouts).
    /// </summary>
    /// <remarks>
    /// A host that both embeds a form viewer and calls <c>$validate</c>/<c>$extract</c> builds one
    /// of these and applies it to both, so the rendered form and the client can't point at different
    /// servers.
    /// <para>
    /// Immutable, and a plain config bag: it does <b>not</b> own or dispose the
    /// <see cref="HttpClient"/> — that ownership stays with whoever created it. The same instance is
    /// safe to share across several clients.
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

        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client; <c>null</c> lets the client own its own.</param>
        public SdcConnection(Uri baseAddress, HttpClient httpClient = null)
        {
            BaseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
            HttpClient = httpClient;
        }

        /// <summary>Convenience overload taking the base address as an absolute URL string.</summary>
        /// <param name="baseAddress">The SDC server FHIR base, e.g. <c>https://host/fhir/r5</c>.</param>
        /// <param name="httpClient">Optional pre-configured client; <c>null</c> lets the client own its own.</param>
        public SdcConnection(string baseAddress, HttpClient httpClient = null)
            : this(new Uri(baseAddress ?? throw new ArgumentNullException(nameof(baseAddress)), UriKind.Absolute),
                   httpClient)
        {
        }
    }
}
