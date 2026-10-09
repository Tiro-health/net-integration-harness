using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Content for a text shortcut: plain text, plus optional HTML for fields that take
    /// formatting. Returned from
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}.ResolveTextShortcut"/>.
    /// </summary>
    /// <remarks>Inserted as given: resolve placeholders before returning it.</remarks>
    public sealed class TiroSnippet
    {
        /// <param name="text">Plain-text rendition. Required.</param>
        /// <param name="html">Optional HTML fragment for fields that take formatting.</param>
        public TiroSnippet(string text, string html = null)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("A snippet needs its plain-text rendition.", nameof(text));
            Text = text;
            Html = string.IsNullOrEmpty(html) ? null : html;
        }

        /// <summary>
        /// A snippet from RTF, converted via <see cref="TiroRtf"/>. Null or empty RTF gives null,
        /// so a lookup that found nothing can be passed straight through. Call on the UI thread.
        /// </summary>
        /// <remarks>
        /// Fields keep bold, italic, underline and paragraphs; tables, fonts, colours, lists and
        /// images flatten to text. For another converter, pass your own HTML to the constructor.
        /// </remarks>
        public static TiroSnippet FromRtf(string rtf)
        {
            if (rtf == null) return null;
            var text = TiroRtf.ToPlainText(rtf);
            return string.IsNullOrEmpty(text) ? null : new TiroSnippet(text, TiroRtf.ToHtml(rtf));
        }

        public string Text { get; }

        /// <summary>Null when the snippet is plain text only.</summary>
        public string Html { get; }
    }
}
