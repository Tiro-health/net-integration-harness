using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// A typed abbreviation and its replacement, e.g. <c>µnka</c> → "No known drug allergies.".
    /// See <see cref="TiroFormViewer{TResource,TQR,TOO}.TextShortcuts"/>.
    /// </summary>
    /// <remarks>Inserted as given: resolve placeholders before creating it.</remarks>
    public sealed class TiroTextShortcut
    {
        /// <param name="abbreviation">What the user types; no whitespace. A prefix like <c>µ</c> is just part of it.</param>
        /// <param name="text">Plain-text rendition. Required.</param>
        /// <param name="html">Optional HTML fragment for fields that take formatting.</param>
        public TiroTextShortcut(string abbreviation, string text, string html = null)
        {
            if (string.IsNullOrEmpty(abbreviation))
                throw new ArgumentException("A text shortcut needs an abbreviation.", nameof(abbreviation));
            foreach (var c in abbreviation)
                if (char.IsWhiteSpace(c))
                    throw new ArgumentException(
                        "An abbreviation cannot contain whitespace: the space after it is what triggers it.",
                        nameof(abbreviation));
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("A text shortcut needs its plain-text rendition.", nameof(text));

            Abbreviation = abbreviation;
            Text = text;
            Html = string.IsNullOrEmpty(html) ? null : html;
        }

        /// <summary>
        /// A shortcut from RTF, converted now via <see cref="TiroRtf"/>. Call on the UI thread.
        /// </summary>
        /// <remarks>
        /// Fields keep bold, italic, underline and paragraphs; tables, fonts, colours, lists and
        /// images flatten to text. For another converter, pass your own HTML to the constructor.
        /// </remarks>
        public static TiroTextShortcut FromRtf(string abbreviation, string rtf)
        {
            if (rtf == null) throw new ArgumentNullException(nameof(rtf));
            return new TiroTextShortcut(abbreviation, TiroRtf.ToPlainText(rtf), TiroRtf.ToHtml(rtf));
        }

        public string Abbreviation { get; }

        public string Text { get; }

        /// <summary>Null when the shortcut is plain text only.</summary>
        public string Html { get; }
    }
}
