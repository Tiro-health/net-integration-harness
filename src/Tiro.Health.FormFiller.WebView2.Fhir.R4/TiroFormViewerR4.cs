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
