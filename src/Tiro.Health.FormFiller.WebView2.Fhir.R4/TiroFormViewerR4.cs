using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.FormFiller.WebView2.Fhir.R4
{
    /// <summary>
    /// FHIR R4 closed binding of <see cref="TiroFormViewer{TResource,TQR,TOO}"/>.
    /// Designer-friendly: sealed, parameterless ctor, bound to the R4
    /// <see cref="SmartWebMessaging.Fhir.R4.SmartMessageHandler"/>.
    /// Telemetry defaults to <see cref="SentryTelemetrySink"/> with the Tiro DSN; pass a
    /// custom sink to opt out or redirect.
    /// </summary>
    public sealed class TiroFormViewerR4 : TiroFormViewer<Resource, QuestionnaireResponse, OperationOutcome>
    {
        /// <summary>
        /// The Tiro-hosted SDC server for FHIR R4. Used as the default for
        /// <see cref="TiroFormViewer{T,Q,O}.SdcEndpointAddress"/>. Best-effort shared instance —
        /// no SLA, no uptime guarantees, not suitable for clinical workflows. Production
        /// integrators should host their own SDC server and override <c>SdcEndpointAddress</c>.
        /// </summary>
        public const string DefaultSdcEndpointAddress = "https://sdc.tiro.health/fhir/r4";

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

        protected override ITelemetrySink CreateTelemetrySink() => new SentryTelemetrySink();

        protected override bool IsOutcomeSuccessful(OperationOutcome outcome)
            => outcome == null || outcome.Success;
    }
}
