using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// The page is not running the embedded web-sdk: it loaded its own copy
    /// (source "collision") or the embedded bundle failed to load (source "error").
    /// Terminal for this viewer — fix the page or environment and create a new viewer.
    /// </summary>
    public class WebSdkLoadException : InvalidOperationException
    {
        /// <summary>"collision" or "error", as reported by the bridge at handshake.</summary>
        public string Source { get; }

        public WebSdkLoadException(string source)
            : base(source == "collision"
                ? "The page loads its own tiro-web-sdk copy. Remove the tiro-web-sdk <script> tag from the page — the harness embeds and serves its own validated copy (GH-60)."
                : "The embedded tiro-web-sdk failed to load in the page, so the form cannot render. Check for policy/antivirus blocking the temp extraction or the tiro-sdk.example virtual host.")
        {
            Source = source;
        }
    }
}
