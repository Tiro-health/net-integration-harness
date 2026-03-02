using System.ComponentModel.DataAnnotations;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class FormSubmit<TQuestionnaireResponse, TOperationOutcome> : RequestPayload
    {
        [Required(ErrorMessage = "Outcome is mandatory.")]
        public TOperationOutcome Outcome { get; }

        [Required(ErrorMessage = "Response is mandatory.")]
        public TQuestionnaireResponse Response { get; }

        public FormSubmit(TOperationOutcome outcome, TQuestionnaireResponse response)
        {
            Outcome = outcome;
            Response = response;
        }
    }
}
