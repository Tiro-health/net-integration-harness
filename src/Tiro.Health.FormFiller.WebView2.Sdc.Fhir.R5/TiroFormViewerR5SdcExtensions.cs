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
    /// Opt-in SDC convenience for <see cref="TiroFormViewerR5"/>. Lets a host run <c>$extract</c>
    /// against the <c>QuestionnaireResponse</c> it just received without constructing or
    /// configuring an <see cref="SdcClient"/>: the server address and telemetry trace are read
    /// from the viewer itself, so the call can't diverge from the form the user filled.
    /// </summary>
    /// <remarks>
    /// This is the only seam where the form-filler and the SDC client meet — referencing this
    /// package is how a host opts into the convenience; neither core package depends on the other.
    /// </remarks>
    public static class TiroFormViewerR5SdcExtensions
    {
        /// <summary>
        /// Extract FHIR resources from <paramref name="response"/> via the same SDC server the
        /// viewer renders against, recorded in the viewer's telemetry trace. Returns the
        /// transaction <see cref="Bundle"/> the SDC <c>$extract</c> operation produces.
        /// </summary>
        /// <param name="viewer">The viewer whose <c>SdcEndpointAddress</c> and telemetry session are reused.</param>
        /// <param name="response">The completed QuestionnaireResponse to extract from.</param>
        /// <param name="cancellationToken">Cancels the extraction round-trip.</param>
        /// <exception cref="ArgumentNullException"><paramref name="viewer"/> or <paramref name="response"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The viewer has no <c>SdcEndpointAddress</c> set.</exception>
        /// <exception cref="SdcOperationException">The server returned a non-2xx status or an unparseable body.</exception>
        /// <remarks>
        /// A short-lived <see cref="SdcClient"/> is created per call with its own
        /// <c>HttpClient</c> — appropriate at form-submit cadence (one extract per human
        /// submission). A host that extracts in bulk should use <see cref="SdcClient"/> /
        /// <see cref="SdcConnection"/> directly with a shared <c>HttpClient</c>.
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

            // Address + trace come from the viewer, so the extract call can't point at a different
            // server than the rendered form, and its span joins the form-session trace.
            var connection = new SdcConnection(
                new Uri(viewer.SdcEndpointAddress, UriKind.Absolute),
                telemetry: viewer.TelemetrySession);

            using (var client = new SdcClient(connection))
                return await client.ExtractAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }
}
