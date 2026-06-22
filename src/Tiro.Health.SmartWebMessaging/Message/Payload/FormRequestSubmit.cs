namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for <c>ui.form.requestSubmit</c> when the host wants to express which
    /// user-facing action triggered the submit. The form remains the authority on the
    /// resulting <c>QuestionnaireResponse.status</c> — the host only states the intent.
    /// </summary>
    public class FormRequestSubmit : RequestPayload
    {
        /// <summary>
        /// "finalize" (default) — validate and write <c>status = "completed"</c>
        /// (or "amended" when prior originate provenance exists).
        /// "save-draft" — skip required-field validation and write <c>status = "in-progress"</c>.
        /// A missing value is treated as "finalize" by the form.
        /// </summary>
        public string Intent { get; set; }

        public FormRequestSubmit()
        {
        }

        public FormRequestSubmit(string intent)
        {
            Intent = intent;
        }
    }
}
