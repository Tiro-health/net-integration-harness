using System.Collections.Generic;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for <c>ui.form.configureTextShortcuts</c>: the abbreviations the form watches
    /// for. Replaces the page's list; empty turns text shortcuts off.
    /// </summary>
    public class FormTextShortcuts : RequestPayload
    {
        public List<string> Abbreviations { get; set; } = new List<string>();

        public FormTextShortcuts()
        {
        }

        public FormTextShortcuts(List<string> abbreviations)
        {
            Abbreviations = abbreviations ?? new List<string>();
        }
    }
}
