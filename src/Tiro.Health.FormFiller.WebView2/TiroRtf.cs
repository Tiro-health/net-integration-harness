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
    }
}
