using System;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// Triggered when a form.submitted message is received.
    /// </summary>
    public class FormSubmittedEventArgs<TQuestionnaireResponse, TOperationOutcome> : EventArgs
    {
        public TQuestionnaireResponse Response { get; }
        public TOperationOutcome Outcome { get; }

        public FormSubmittedEventArgs(TQuestionnaireResponse response, TOperationOutcome outcome)
        {
            Response = response;
            Outcome = outcome;
        }
    }
}
