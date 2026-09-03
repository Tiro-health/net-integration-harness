using System;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Puts formatted content on the Windows clipboard so it pastes into the form's rich-text
    /// answers with its formatting intact, and builds the CF_HTML envelope Windows requires for
    /// that.
    /// </summary>
    /// <remarks>
    /// HTML is the format that carries formatting into a form field. The rich-text answers store
    /// their value as HTML, and the embedded editor reads <c>text/html</c> on paste. RTF is
    /// deliberately absent: Chromium never reads the Windows RTF flavour, so putting it on the
    /// clipboard would do nothing for a paste into the form.
    /// <para>
    /// The harness converts nothing. An EHR holding RTF converts it with whatever library it
    /// already trusts and passes the HTML in. Use a converter that emits semantic tags
    /// (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>) or inline styles: a clipboard HTML flavour is a
    /// fragment, so a converter emitting CSS classes plus a stylesheet loses the stylesheet and
    /// every rule with it — bold and italic survive while underline and colour quietly vanish.
    /// </para>
    /// <para>
    /// What is copied leaves the application: the Windows clipboard is readable by every process
    /// on the machine, and clipboard managers, Remote Desktop redirection and Windows Cloud
    /// Clipboard may persist or sync it.
    /// </para>
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
        /// through the markup. Hence <see cref="Encoding.GetByteCount(string)"/> throughout, and
        /// the non-ASCII test beside this.
        /// <para>
        /// A fragment that already looks framed is returned untouched, so passing this method's
        /// own output back into it is harmless.
        /// </para>
        /// </remarks>
        /// <param name="fragment">Body-level HTML. Null or empty returns null.</param>
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
        /// Puts <paramref name="html"/> and <paramref name="plainText"/> on the clipboard
        /// together. A rich-text answer takes the HTML; a plain-string answer, or anything else
        /// that reads only text, takes the plain text.
        /// </summary>
        /// <remarks>
        /// Both are required rather than derived. Stripping tags out of HTML gives a poor
        /// rendition, and an EHR holding the source RTF has a much better one for free in
        /// <c>New RichTextBox() With {.Rtf = rtf}.Text</c> — so asking for it produces a better
        /// result than guessing, and a copy that silently pastes nothing into a plain field is
        /// worse than either.
        /// <para>
        /// Goes through the retrying <see cref="Clipboard.SetDataObject(object, bool, int, int)"/>
        /// overload because the clipboard is a machine-wide single-owner resource another
        /// process may be holding open. <c>copy: true</c> leaves the data there after this
        /// application exits, so a copy made just before the form closes still pastes
        /// afterwards. Must be called on an STA thread — menu items and button handlers already
        /// are.
        /// </para>
        /// </remarks>
        /// <returns>True when something was placed on the clipboard.</returns>
        public static bool SetHtml(string html, string plainText)
        {
            // Nothing to copy is a no-op: the clipboard API rejects empty content, and clearing
            // the clinician's clipboard is worse than doing nothing.
            if (string.IsNullOrEmpty(html) && string.IsNullOrEmpty(plainText)) return false;

            var data = new DataObject();

            var cfHtml = ToCfHtml(html);
            if (!string.IsNullOrEmpty(cfHtml)) data.SetData(DataFormats.Html, cfHtml);
            if (!string.IsNullOrEmpty(plainText)) data.SetData(DataFormats.UnicodeText, plainText);

            Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 50);
            return true;
        }
    }
}
