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
        /// Default SDC endpoint applied to <see cref="TiroFormViewer{T,Q,O}.SdcEndpointAddress"/>
        /// so out-of-the-box R4 demos work without explicit configuration.
        /// <para>
        /// Currently points at Tiro's <c>/fhir/r5</c> endpoint — Tiro does not yet host a
        /// dedicated R4 SDC server. The R5 endpoint accepts and round-trips most R4 questionnaire
        /// content fine for development and demos, but resource shapes that diverge between
        /// versions (cardinalities, enum sets, slot fields) may be coerced silently. For
        /// production R4 workloads, override this with your own R4-hosting SDC server.
        /// </para>
        /// <para>
        /// Best-effort shared instance — no SLA, no uptime guarantees, not suitable for
        /// clinical workflows.
        /// </para>
        /// </summary>
        public const string DefaultSdcEndpointAddress = "https://sdc.tiro.health/fhir/r5";

        /// <summary>
        /// Designer-friendly parameterless ctor. Telemetry is resolved from
        /// <see cref="TiroFormViewerDefaults.TelemetrySinkFactory"/> at construction —
        /// defaults to <see cref="NullTelemetrySink"/> when no factory is registered.
        /// </summary>
        public TiroFormViewerR4()
        {
            // Seed SdcEndpointAddress with the default (currently the R5 endpoint — see the
            // XML doc on DefaultSdcEndpointAddress for the version-routing nuance). Hosts that
            // need to point the form at a different SDC server overwrite this property before
            // the WinForms Form.Load handler awaits SetContextAsync; the WebView2 init yields
            // well before the bridge reads SdcEndpointAddress, so a Form.Load assignment wins.
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

        // A save-draft round-trips status in-progress; the session must stay usable.
        protected override bool IsResponseFinal(QuestionnaireResponse response)
            => response?.Status != QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;
    }
}
