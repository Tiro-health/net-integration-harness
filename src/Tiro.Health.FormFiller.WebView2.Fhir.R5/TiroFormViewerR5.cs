using Hl7.Fhir.Model;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.FormFiller.WebView2.Fhir.R5
{
    /// <summary>
    /// FHIR R5 closed binding of <see cref="TiroFormViewer{TResource,TQR,TOO}"/>.
    /// Designer-friendly: sealed, parameterless ctor, bound to the R5
    /// <see cref="SmartWebMessaging.Fhir.R5.SmartMessageHandler"/>.
    /// </summary>
    public sealed class TiroFormViewerR5 : TiroFormViewer<Resource, QuestionnaireResponse, OperationOutcome>
    {
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
