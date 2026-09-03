using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// One piece of content in several clipboard formats at once. Everything set here goes on
    /// the clipboard together, and each consumer takes the richest flavour it understands —
    /// the rich-text answer fields take <see cref="Html"/>, a plain field or Notepad takes
    /// <see cref="PlainText"/>, Word and Outlook prefer <see cref="Rtf"/>.
    /// </summary>
    /// <remarks>
    /// The harness deliberately does not convert between these. An EHR holding RTF converts it
    /// to HTML with whatever library it already trusts (RtfPipe, for instance) and hands both
    /// over; the harness owns only the part that is shared and easy to get wrong — the CF_HTML
    /// framing and putting several flavours on the clipboard atomically.
    /// <para>
    /// A converter that emits CSS <em>classes</em> plus a stylesheet is a poor fit here: a
    /// clipboard HTML flavour is a fragment, so the stylesheet is gone and every class-based
    /// rule with it (underline, colour, font). Prefer a converter that emits semantic tags or
    /// inline styles.
    /// </para>
    /// </remarks>
    public sealed class TiroClipboardContent
    {
        public TiroClipboardContent()
        {
        }

        /// <summary>Shorthand for HTML with a plain-text fallback.</summary>
        public TiroClipboardContent(string html, string plainText = null)
        {
            Html = html;
            PlainText = plainText;
        }

        /// <summary>
        /// An HTML <em>fragment</em> — the body content, no <c>&lt;html&gt;</c> or
        /// <c>&lt;head&gt;</c> wrapper. <see cref="TiroClipboard"/> adds the CF_HTML header and
        /// wrapper Windows requires. This is the flavour the SDK's rich-text answer fields read
        /// on paste, and what they store.
        /// </summary>
        public string Html { get; set; }

        /// <summary>
        /// The plain-text flavour. Some targets take nothing else, so leaving this unset is
        /// usually a mistake: <see cref="TiroClipboard"/> then derives one from
        /// <see cref="Html"/> by a crude tag strip, which is worse than what the caller can do.
        /// An EHR holding the original RTF gets a far better rendition from
        /// <c>New RichTextBox() With {.Rtf = rtf}.Text</c>.
        /// </summary>
        public string PlainText { get; set; }

        /// <summary>
        /// Optional RTF flavour, carried through untouched. Chromium ignores it — pasting into
        /// the form uses <see cref="Html"/> — so this exists purely so the same copy keeps full
        /// fidelity when the user pastes into Word or Outlook instead. Costs nothing to include.
        /// </summary>
        public string Rtf { get; set; }

        /// <summary>True when there is at least one flavour worth putting on the clipboard.</summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(Html) && string.IsNullOrEmpty(PlainText) && string.IsNullOrEmpty(Rtf);
    }
}
