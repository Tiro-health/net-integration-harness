using System.ComponentModel.DataAnnotations;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class FormSubmit<TQuestionnaireResponse, TOperationOutcome> : RequestPayload
    {
        [Required(ErrorMessage = "Outcome is mandatory.")]
        public TOperationOutcome Outcome { get; set; }

        [Required(ErrorMessage = "Response is mandatory.")]
        public TQuestionnaireResponse Response { get; set; }

        // Settable properties + parameterless ctor: matches every other DTO in the
        // protocol and lets System.Text.Json deserialize via standard property-setter
        // path. The earlier get-only-props design forced ctor-parameter-name binding,
        // so any rename of `outcome` / `response` would silently break inbound parsing.
        public FormSubmit() { }

        public FormSubmit(TOperationOutcome outcome, TQuestionnaireResponse response)
        {
            Outcome = outcome;
            Response = response;
        }
    }
}
