using Hl7.Fhir.Model;
using Tiro.Health.Telemetry;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.FormFiller.WebView2.Fhir.R5
{
    /// <summary>
    /// FHIR R5 closed binding of <see cref="TiroFormViewer{TResource,TQR,TOO}"/>.
    /// Designer-friendly: sealed, parameterless ctor, bound to the R5
    /// <see cref="SmartWebMessaging.Fhir.R5.SmartMessageHandler"/>.
    /// Telemetry defaults to <see cref="NullTelemetrySink"/> (no-op). To enable Sentry
    /// telemetry, install the <c>Tiro.Health.FormFiller.WebView2.Sentry</c> NuGet and call
    /// <c>TiroFormFillerSentry.UseSentry()</c> once at application startup (e.g. in
    /// <c>Sub Main</c>, before any viewer is constructed).
    /// </summary>
    public sealed class TiroFormViewerR5 : TiroFormViewer<Resource, QuestionnaireResponse, OperationOutcome>
    {
        /// <summary>
        /// The Tiro-hosted SDC server for FHIR R5. Used as the default for
        /// <see cref="TiroFormViewer{T,Q,O}.SdcEndpointAddress"/>. Best-effort shared instance —
        /// no SLA, no uptime guarantees, not suitable for clinical workflows. Production
        /// integrators should host their own SDC server and override <c>SdcEndpointAddress</c>.
        /// </summary>
        public const string DefaultSdcEndpointAddress = "https://sdc.tiro.health/fhir/r5";

        /// <summary>
        /// Designer-friendly parameterless ctor. Telemetry is resolved from
        /// <see cref="TiroFormViewerDefaults.TelemetrySinkFactory"/> at construction —
        /// defaults to <see cref="NullTelemetrySink"/> when no factory is registered.
        /// </summary>
        public TiroFormViewerR5()
        {
            // Default the embedded form-filler to Tiro's R5 SDC server so out-of-the-box use
            // works without explicit configuration. Hosts that need to point the form at a
            // different SDC server overwrite this property before the WinForms Form.Load
            // handler awaits SetContextAsync; the WebView2 init yields well before the
            // bridge reads SdcEndpointAddress, so a Form.Load assignment wins.
            SdcEndpointAddress = DefaultSdcEndpointAddress;
        }

        /// <summary>
        /// The underlying R5 SMART Web Messaging handler. Shadows the base
        /// <see cref="TiroFormViewer{T,Q,O}.MessageHandler"/> to expose R5-typed send overloads
        /// (e.g. <c>SendSdcDisplayQuestionnaireAsync(Questionnaire, ...)</c>).
        /// </summary>
        public new SmartWebMessaging.Fhir.R5.SmartMessageHandler MessageHandler
            => (SmartWebMessaging.Fhir.R5.SmartMessageHandler)base.MessageHandler;

        protected override SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome> CreateMessageHandler()
            => new SmartWebMessaging.Fhir.R5.SmartMessageHandler();

        protected override bool IsOutcomeSuccessful(OperationOutcome outcome)
            => outcome == null || outcome.Success;
    }
}
