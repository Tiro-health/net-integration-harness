using System;
using System.Globalization;
using System.IO;
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
        /// <summary>
        /// Copies plain text, with the same privacy hints and ownership tracking as
        /// <see cref="SetHtml"/>. Empty or null is a no-op.
        /// </summary>
        /// <returns>True when something was placed on the clipboard.</returns>
        public static bool SetText(string text) => SetHtml(null, text);

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

            AddPrivacyFormats(data);

            Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 50);
            // Remembered so ClearIfOurs can tell our own copy from one the clinician made
            // afterwards, and clear only the former.
            _lastCopiedText = string.IsNullOrEmpty(plainText) ? null : plainText;
            return true;
        }

        // The plain-text rendition of whatever this class last put on the clipboard, or null.
        // Only ever compared, never shown, so it holds no more than the clipboard already does.
        private static string _lastCopiedText;

        /// <summary>
        /// Marks the content as not to be kept: excluded from Windows clipboard history, from
        /// Cloud Clipboard sync across the user's devices, and from clipboard-monitor tools that
        /// honour the exclusion.
        /// </summary>
        /// <remarks>
        /// This is the half of the privacy story that actually works, because it stops the data
        /// being retained in the first place — unlike <see cref="ClearIfOurs"/>, which can only
        /// tidy up afterwards and cannot recall a copy already synced.
        /// <para>
        /// The two DWORD formats want four raw bytes, so they go through a
        /// <see cref="MemoryStream"/>: WinForms writes a stream's bytes to the clipboard
        /// verbatim, where an <see cref="int"/> would be serialised as a .NET object and ignored.
        /// Best-effort throughout — these formats are Windows 10 1809 and later, and an older
        /// build simply carries three formats nothing reads.
        /// </para>
        /// </remarks>
        private static void AddPrivacyFormats(DataObject data)
        {
            try
            {
                // Presence alone is the signal for this one.
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                // DWORD 0 = "no".
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            }
            catch (Exception)
            {
                // A clipboard copy must not fail because a privacy hint could not be attached.
            }
        }

        /// <summary>
        /// Clears the clipboard, but only when it still holds what this class last put there.
        /// </summary>
        /// <remarks>
        /// The ownership check is the point. A blind <see cref="Clipboard.Clear"/> would throw
        /// away whatever the clinician copied since — a URL, a password out of a manager, a
        /// paragraph they were moving between applications — which is a worse outcome than
        /// leaving a phrase behind. Comparing against the text we set means we only ever discard
        /// our own copy.
        /// <para>
        /// Best-effort, and worth being honest about its limits: it shortens the window during
        /// which patient text sits on a machine-wide clipboard, but it cannot recall what a
        /// clipboard manager, Cloud Clipboard or Remote Desktop redirection already took. That
        /// is what <see cref="AddPrivacyFormats"/> is for, and why the two belong together.
        /// </para>
        /// </remarks>
        /// <returns>True when the clipboard was cleared.</returns>
        public static bool ClearIfOurs()
        {
            var ours = _lastCopiedText;
            if (string.IsNullOrEmpty(ours)) return false;

            try
            {
                // Not ours any more — someone copied over it, and their content stays.
                if (!Clipboard.ContainsText() || !string.Equals(Clipboard.GetText(), ours, StringComparison.Ordinal))
                    return false;

                Clipboard.Clear();
                _lastCopiedText = null;
                return true;
            }
            catch (Exception)
            {
                // Another process holding the clipboard open, or no clipboard at all (a service,
                // a test host). Nothing here is worth failing a caller over — least of all
                // Dispose, which is where this is called from.
                return false;
            }
        }
    }
}
