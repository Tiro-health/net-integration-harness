using System;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// RTF helpers for hosts holding their content as RTF, which most WinForms EHRs do.
    /// </summary>
    public static class TiroRtf
    {
        /// <summary>
        /// The plain-text rendition of an RTF document, using the RTF parser WinForms already
        /// contains — so no library is needed for this direction.
        /// </summary>
        /// <remarks>
        /// This exists in the harness because the correct implementation has a trap in it:
        /// <see cref="RichTextBox"/> owns a Win32 window handle and must be disposed, and a
        /// menu item runs once per click, so the obvious one-liner leaks a handle on every
        /// click until the process hits its limit.
        /// <para>
        /// Only this direction is free. RTF to <em>HTML</em> needs a real parser and stays the
        /// consumer's choice — the harness would otherwise be picking a fidelity trade-off on
        /// behalf of integrators who may already license a better engine.
        /// </para>
        /// <para>
        /// Must run on the UI (STA) thread, and creates a window handle per call: fine for a
        /// menu click, not for converting documents in bulk.
        /// </para>
        /// </remarks>
        /// <param name="rtf">An RTF document. Null or empty returns an empty string.</param>
        /// <returns>The text with all formatting dropped.</returns>
        /// <exception cref="ArgumentException">
        /// The string is not valid RTF. Deliberately not swallowed: putting the raw RTF markup
        /// into a clinical field would be worse than the item failing visibly.
        /// </exception>
        public static string ToPlainText(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return string.Empty;

            using (var box = new RichTextBox())
            {
                box.Rtf = rtf;
                return box.Text;
            }
        }
        /// <summary>
        /// An HTML fragment from an RTF document, ready to hand to
        /// <see cref="TiroFormViewer{TResource,TQR,TOO}.AddInsertItem"/> as the formatted
        /// rendition. Body-level markup only — no <c>&lt;html&gt;</c> or <c>&lt;head&gt;</c>
        /// wrapper.
        /// </summary>
        /// <remarks>
        /// A convenience, not a full-fidelity converter, and scoped on purpose to what a
        /// rich-text answer can store:
        /// <list type="bullet">
        /// <item><description>
        /// <b>Kept:</b> bold, italic and underline as <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c>/<c>&lt;u&gt;</c>,
        /// paragraphs from <c>\par</c>, line breaks from <c>\line</c>, and the text of hyperlink
        /// fields (the link target is not).
        /// </description></item>
        /// <item><description>
        /// <b>Flattened:</b> tables, lists, images, colours, fonts, embedded objects, headers
        /// and footers. Their text survives; their structure does not — because the field's
        /// editor has no node for any of it, so a table would collapse to paragraphs however
        /// carefully it had been converted.
        /// </description></item>
        /// <item><description>
        /// <b>Handled properly:</b> character encoding. <c>\'hh</c> is decoded through the
        /// codepage the document declares in <c>\ansicpg</c>, consecutive escapes together so
        /// double-byte codepages work, and <c>\uN</c> with exactly the <c>\ucN</c> fallback
        /// characters skipped. This is the part that decides whether an é or a µ in a clinical
        /// note survives, and the usual reason a hand-rolled converter produces mojibake.
        /// </description></item>
        /// </list>
        /// <para>
        /// It emits semantic tags rather than CSS classes deliberately: what gets handed over is
        /// a fragment with no stylesheet, so class-based styling would lose every rule with it.
        /// </para>
        /// <para>
        /// For RTF from arbitrary sources — Word imports, embedded logos, tracked changes — a
        /// dedicated library such as <a href="https://github.com/erdomke/RtfPipe">RtfPipe</a>
        /// will do better. Pass its output instead; nothing here is mandatory.
        /// </para>
        /// </remarks>
        /// <param name="rtf">An RTF document. Null or empty returns an empty string.</param>
        public static string ToHtml(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return string.Empty;
            return new RtfHtmlConverter(rtf).Run();
        }

    }
}
