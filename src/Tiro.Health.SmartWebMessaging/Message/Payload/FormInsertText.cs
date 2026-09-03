namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for <c>ui.form.insertText</c>: text the host wants typed into the form
    /// field the user is currently in, at the caret. Deliberately carries no target —
    /// no linkId, no field name. The caret is the target, so the host needs no knowledge
    /// of the questionnaire's structure and the renderer stays the only writer of answers.
    /// </summary>
    public class FormInsertText : RequestPayload
    {
        /// <summary>
        /// The text to insert. Inserted as typed input (it replaces the selection, if any),
        /// so newlines only survive in a field that accepts them.
        /// </summary>
        public string Text { get; set; }

        public FormInsertText()
        {
        }

        public FormInsertText(string text)
        {
            Text = text;
        }
    }
}
