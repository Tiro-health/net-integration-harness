using System;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// Triggered when a ui.form.textShortcutRequested message is received: the user typed an
    /// abbreviation whose content the host resolves. Answer with
    /// <c>SendResolveTextShortcutAsync</c> and the same <see cref="RequestId"/>.
    /// </summary>
    public class TextShortcutRequestedEventArgs : EventArgs
    {
        public string RequestId { get; }

        public string Abbreviation { get; }

        public TextShortcutRequestedEventArgs(string requestId, string abbreviation)
        {
            RequestId = requestId;
            Abbreviation = abbreviation;
        }
    }
}
