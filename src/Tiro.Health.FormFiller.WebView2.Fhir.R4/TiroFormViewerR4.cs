using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.FormFiller.WebView2.Fhir.R4
{
    /// <summary>
    /// FHIR R4 closed binding of <see cref="TiroFormViewer{TResource,TQR,TOO}"/>.
    /// Designer-friendly: sealed, parameterless ctor, bound to the R4
    /// <see cref="SmartWebMessaging.Fhir.R4.SmartMessageHandler"/>.
    /// Telemetry defaults to <see cref="NullTelemetrySink"/> (no-op). To enable Sentry
    /// telemetry, install the <c>Tiro.Health.FormFiller.WebView2.Sentry</c> NuGet and call
    /// <c>TiroFormFillerSentry.UseSentry()</c> once at application startup (e.g. in
    /// <c>Sub Main</c>, before any viewer is constructed).
    /// </summary>
    public sealed class TiroFormViewerR4 : TiroFormViewer<Resource, QuestionnaireResponse, OperationOutcome>
    {
        /// <summary>
        /// The Tiro-hosted SDC server for FHIR R4. Used as the default for
        /// <see cref="TiroFormViewer{T,Q,O}.SdcEndpointAddress"/>. Best-effort shared instance —
        /// no SLA, no uptime guarantees, not suitable for clinical workflows. Production
        /// integrators should host their own SDC server and override <c>SdcEndpointAddress</c>.
        /// </summary>
        // Temporary: the dedicated R4 endpoint (`/fhir/r4`) is offline, so we point the R4
        // default at the R5 endpoint to keep the demo working. Revert to `/fhir/r4` once
        // the R4 SDC server is back online.
        public const string DefaultSdcEndpointAddress = "https://sdc.tiro.health/fhir/r5";

        /// <summary>
        /// Designer-friendly parameterless ctor. Telemetry is resolved from
        /// <see cref="TiroFormViewerDefaults.TelemetrySinkFactory"/> at construction —
        /// defaults to <see cref="NullTelemetrySink"/> when no factory is registered.
        /// </summary>
        public TiroFormViewerR4()
        {
            // Default the embedded form-filler to Tiro's R4 SDC server so out-of-the-box use
            // works without explicit configuration. Hosts that need to point the form at a
            // different SDC server overwrite this property before the WinForms Form.Load
            // handler awaits SetContextAsync; the WebView2 init yields well before the
            // bridge reads SdcEndpointAddress, so a Form.Load assignment wins.
            SdcEndpointAddress = DefaultSdcEndpointAddress;
        }

        /// <summary>
        /// The underlying R4 SMART Web Messaging handler. Shadows the base
        /// <see cref="TiroFormViewer{T,Q,O}.MessageHandler"/> to expose R4-typed send overloads
        /// (e.g. <c>SendSdcDisplayQuestionnaireAsync(Questionnaire, ...)</c>).
        /// </summary>
        public new SmartWebMessaging.Fhir.R4.SmartMessageHandler MessageHandler
            => (SmartWebMessaging.Fhir.R4.SmartMessageHandler)base.MessageHandler;

        protected override SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome> CreateMessageHandler()
            => new SmartWebMessaging.Fhir.R4.SmartMessageHandler();

        protected override bool IsOutcomeSuccessful(OperationOutcome outcome)
            => outcome == null || outcome.Success;
    }
}
