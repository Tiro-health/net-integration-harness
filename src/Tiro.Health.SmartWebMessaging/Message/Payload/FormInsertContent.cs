namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for <c>ui.form.insertContent</c>: content the host wants placed into the form
    /// field that currently holds the caret. Carries no target — no linkId, no field name. The
    /// caret is the target, so the host needs no knowledge of the questionnaire's structure and
    /// the renderer stays the only writer of answers.
    /// </summary>
    public class FormInsertContent : RequestPayload
    {
        /// <summary>
        /// The plain-text rendition. Always required: it is what a string-typed answer receives,
        /// and the fallback when <see cref="Html"/> is absent or the field declines it.
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Optional body-level HTML fragment. When present the page tries it first, so a
        /// rich-text answer keeps the formatting; a field that cannot take it falls back to
        /// <see cref="Text"/>, and the page reports which happened.
        /// </summary>
        public string Html { get; set; }

        public FormInsertContent()
        {
        }

        public FormInsertContent(string text, string html = null)
        {
            Text = text;
            Html = html;
        }
    }
}
