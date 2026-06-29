using System;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Fhir.R5;
using Tiro.Health.FormSdk.Client;
using Tiro.Health.FormSdk.Client.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Sdc.Fhir.R5
{
    /// <summary>
    /// Opt-in SDC convenience bridging <see cref="TiroFormViewerR5"/> and the SDC client. This is the
    /// only seam where the form-filler and the SDC client meet — referencing this package is how a
    /// host opts in; neither core package depends on the other.
    /// </summary>
    public static class TiroFormViewerR5SdcExtensions
    {
        /// <summary>
        /// Point the viewer at the SDC server described by <paramref name="connection"/> — sets
        /// the viewer's <c>SdcEndpointAddress</c> from <see cref="SdcConnection.BaseAddress"/>.
        /// Build one <see cref="SdcConnection"/> and apply it to both the viewer (via this call) and
        /// any <see cref="SdcClient"/> (via <c>new SdcClient(connection)</c>) so the rendered form and
        /// direct <c>$validate</c>/<c>$extract</c> calls can't drift onto different servers.
        /// </summary>
        /// <param name="viewer">The viewer to configure. Call before <c>SetContextAsync</c>.</param>
        /// <param name="connection">The single source of truth for the SDC server.</param>
        /// <exception cref="ArgumentNullException"><paramref name="viewer"/> or <paramref name="connection"/> is null.</exception>
        public static void Configure(this TiroFormViewerR5 viewer, SdcConnection connection)
        {
            if (viewer == null) throw new ArgumentNullException(nameof(viewer));
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            viewer.SdcEndpointAddress = connection.BaseAddress.AbsoluteUri;
        }

        /// <summary>
        /// Extract FHIR resources from <paramref name="response"/> via the same SDC server the viewer
        /// renders against (its <c>SdcEndpointAddress</c>). Returns the transaction
        /// <see cref="Bundle"/> the SDC <c>$extract</c> operation produces.
        /// </summary>
        /// <param name="viewer">The viewer whose <c>SdcEndpointAddress</c> is reused for the call.</param>
        /// <param name="response">The completed QuestionnaireResponse to extract from.</param>
        /// <param name="cancellationToken">Cancels the extraction round-trip.</param>
        /// <exception cref="ArgumentNullException"><paramref name="viewer"/> or <paramref name="response"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The viewer has no <c>SdcEndpointAddress</c> set.</exception>
        /// <exception cref="SdcOperationException">The server returned a non-2xx status or an unparseable body.</exception>
        /// <remarks>
        /// <para>
        /// The viewer is read only at invocation (the address is copied synchronously); the call then
        /// runs self-contained on its own short-lived <c>HttpClient</c>. So it is <b>safe to fire
        /// without awaiting and let the viewer close</b> — e.g. extract in the background on submit:
        /// <c>pending.Add(viewer.ExtractAsync(e.Response)) : Me.Close()</c>. It carries no
        /// authentication and no telemetry of its own.
        /// </para>
        /// <para>
        /// For shared/authenticated transport, bulk extraction, or telemetry correlated with the
        /// form-session trace, use <see cref="SdcClient"/> / <see cref="SdcConnection"/> directly
        /// (passing your own <c>HttpClient</c> and/or the viewer's <c>TelemetrySession</c> while the
        /// viewer is alive) rather than this convenience.
        /// </para>
        /// </remarks>
        public static async Task<Bundle> ExtractAsync(
            this TiroFormViewerR5 viewer,
            QuestionnaireResponse response,
            CancellationToken cancellationToken = default)
        {
            if (viewer == null) throw new ArgumentNullException(nameof(viewer));
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (string.IsNullOrEmpty(viewer.SdcEndpointAddress))
                throw new InvalidOperationException(
                    "TiroFormViewerR5.SdcEndpointAddress is not set; cannot reach an SDC server to extract.");

            // Only the address is taken from the viewer (a string, copied here). The call is then
            // independent of the viewer's lifetime — no borrowed session, no shared HttpClient — so
            // the viewer may close while this runs.
            var connection = new SdcConnection(new Uri(viewer.SdcEndpointAddress, UriKind.Absolute));

            using (var client = new SdcClient(connection))
                return await client.ExtractAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }
}
