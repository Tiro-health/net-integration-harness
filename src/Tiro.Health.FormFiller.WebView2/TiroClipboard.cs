using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Puts <see cref="TiroClipboardContent"/> on the Windows clipboard in every format it
    /// carries, and builds the CF_HTML envelope that HTML on the Windows clipboard requires.
    /// </summary>
    /// <remarks>
    /// Whatever is copied leaves the application: the Windows clipboard is readable by every
    /// process on the machine, and clipboard managers, Remote Desktop redirection and Windows
    /// Cloud Clipboard may persist or sync it. Ordinary for a phrase the clinician is about to
    /// paste; worth a deliberate decision for a whole report.
    /// </remarks>
    public static class TiroClipboard
    {
        // Fixed-width offsets (D10) so the header's own length doesn't change once the numbers
        // are filled in — which is what makes measuring it with zeros valid below.
        private const string HeaderTemplate =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

        private const string FragmentPrefix = "<html><body><!--StartFragment-->";
        private const string FragmentSuffix = "<!--EndFragment--></body></html>";

        /// <summary>
        /// Wraps an HTML fragment in the CF_HTML envelope: the <c>Version:0.9</c> header with
        /// its four offsets, then the fragment between the marker comments.
        /// </summary>
        /// <remarks>
        /// The offsets are <em>byte</em> offsets into the UTF-8 encoding, not character counts.
        /// Any non-ASCII in the fragment — an accent, a µ, a curly quote — makes the two differ,
        /// and a reader following character counts then starts or ends the fragment mid-way
        /// through the markup. Hence <see cref="Encoding.GetByteCount(string)"/> throughout and
        /// the non-ASCII tests beside this.
        /// <para>
        /// A fragment that already looks framed is returned untouched, so passing the result of
        /// this method back into it is harmless.
        /// </para>
        /// </remarks>
        /// <param name="fragment">Body-level HTML. Null or empty returns null — there is nothing to frame.</param>
        public static string ToCfHtml(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return null;
            if (fragment.StartsWith("Version:", StringComparison.Ordinal)) return fragment;

            var utf8 = Encoding.UTF8;

            // Valid because D10 pins each offset to ten characters, all of them ASCII: the
            // header formatted with zeros is byte-for-byte as long as the final one.
            var headerLength = utf8.GetByteCount(
                string.Format(CultureInfo.InvariantCulture, HeaderTemplate, 0, 0, 0, 0));

            var startHtml = headerLength;
            // Just past the StartFragment comment...
            var startFragment = startHtml + utf8.GetByteCount(FragmentPrefix);
            // ...and up to, not past, the EndFragment comment.
            var endFragment = startFragment + utf8.GetByteCount(fragment);
            var endHtml = endFragment + utf8.GetByteCount(FragmentSuffix);

            return string.Format(CultureInfo.InvariantCulture, HeaderTemplate,
                       startHtml, endHtml, startFragment, endFragment)
                   + FragmentPrefix + fragment + FragmentSuffix;
        }

        /// <summary>
        /// Puts every format <paramref name="content"/> carries on the clipboard as one item,
        /// so a consumer can pick the richest one it understands.
        /// </summary>
        /// <remarks>
        /// Empty content is a no-op: the clipboard API rejects it, and clearing the clinician's
        /// clipboard is worse than doing nothing. Goes through the retrying
        /// <see cref="Clipboard.SetDataObject(object, bool, int, int)"/> overload because the
        /// clipboard is a machine-wide single-owner resource another process may be holding
        /// open. <c>copy: true</c> leaves the data there after this application exits, so a copy
        /// made just before the form closes still pastes afterwards.
        /// <para>
        /// Must be called on an STA thread — the UI thread. Menu items and button handlers are
        /// already there; a background thread needs to marshal.
        /// </para>
        /// </remarks>
        /// <returns>True when something was placed on the clipboard.</returns>
        public static bool SetContent(TiroClipboardContent content)
        {
            if (content == null || content.IsEmpty) return false;

            var data = new DataObject();

            var cfHtml = ToCfHtml(content.Html);
            if (!string.IsNullOrEmpty(cfHtml)) data.SetData(DataFormats.Html, cfHtml);

            if (!string.IsNullOrEmpty(content.Rtf)) data.SetData(DataFormats.Rtf, content.Rtf);

            // Always last, and always present when there is any content at all: a target that
            // reads nothing but text — Notepad, a plain-string answer — would otherwise get an
            // empty paste from a copy that looked like it worked.
            var plainText = string.IsNullOrEmpty(content.PlainText)
                ? DerivePlainText(content.Html)
                : content.PlainText;
            if (!string.IsNullOrEmpty(plainText)) data.SetData(DataFormats.UnicodeText, plainText);

            Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 50);
            return true;
        }

        /// <summary>
        /// Last-resort plain text from an HTML fragment: block-level tags become line breaks,
        /// remaining tags are dropped, and the handful of entities that survive a tag strip are
        /// decoded.
        /// </summary>
        /// <remarks>
        /// Deliberately crude, and only used when the caller supplied no
        /// <see cref="TiroClipboardContent.PlainText"/>. It is not an HTML-to-text converter:
        /// it does not resolve numeric entities, lay out tables, or number lists. A caller that
        /// cares should pass its own — an EHR holding the source RTF has a much better one for
        /// free in <c>RichTextBox.Text</c>.
        /// </remarks>
        internal static string DerivePlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;

            var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?\s*>",
                "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", string.Empty);

            // Ampersand last, so a literal "&amp;lt;" doesn't decode twice into "<".
            text = text.Replace("&nbsp;", " ")
                       .Replace("&lt;", "<")
                       .Replace("&gt;", ">")
                       .Replace("&quot;", "\"")
                       .Replace("&#39;", "'")
                       .Replace("&amp;", "&");

            // Collapse the runs of blank lines the block-tag substitution leaves behind.
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }
    }
}
