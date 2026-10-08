namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>Payload for the inbound <c>ui.form.textShortcutRequested</c> notification.</summary>
    public class TextShortcutRequest : RequestPayload
    {
        public string RequestId { get; set; }

        public string Abbreviation { get; set; }
    }

    /// <summary>
    /// Payload for <c>ui.form.resolveTextShortcut</c>: the content for a requested abbreviation.
    /// Null <see cref="Text"/> means there is none, and the page leaves the abbreviation as typed.
    /// </summary>
    public class ResolveTextShortcut : RequestPayload
    {
        public string RequestId { get; set; }

        public string Text { get; set; }

        public string Html { get; set; }
    }
}
