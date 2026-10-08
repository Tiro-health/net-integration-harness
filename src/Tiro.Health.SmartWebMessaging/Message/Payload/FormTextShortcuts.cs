using System.Collections.Generic;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for <c>ui.form.configureTextShortcuts</c>. Replaces the page's list; empty turns expansion off.
    /// </summary>
    public class FormTextShortcuts : RequestPayload
    {
        public List<TextShortcutEntry> Shortcuts { get; set; } = new List<TextShortcutEntry>();

        public FormTextShortcuts()
        {
        }

        public FormTextShortcuts(List<TextShortcutEntry> shortcuts)
        {
            Shortcuts = shortcuts ?? new List<TextShortcutEntry>();
        }
    }

    /// <summary>One abbreviation and its replacement.</summary>
    public class TextShortcutEntry
    {
        /// <summary>What the user types, without whitespace — e.g. <c>µnka</c>.</summary>
        public string Abbreviation { get; set; }

        /// <summary>The plain-text rendition. Always required.</summary>
        public string Text { get; set; }

        /// <summary>Optional body-level HTML fragment, tried first in fields that take it.</summary>
        public string Html { get; set; }
    }
}
