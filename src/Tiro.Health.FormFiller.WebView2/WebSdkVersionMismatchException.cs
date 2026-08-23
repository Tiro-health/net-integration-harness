using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// The page's <c>&lt;tiro-form-filler&gt;</c> reported a version different from the
    /// bundle embedded in this package (GH-61) — a stale WebView2 cache, a leftover
    /// SDK script tag, or a foreign bundle. The viewer refuses the session rather
    /// than run an unvalidated pairing.
    /// </summary>
    public class WebSdkVersionMismatchException : InvalidOperationException
    {
        public string ExpectedVersion { get; }
        public string ReportedVersion { get; }

        public WebSdkVersionMismatchException(string expectedVersion, string reportedVersion)
            : base($"The page's tiro-web-sdk reported version '{reportedVersion ?? "none"}' but this harness embeds '{expectedVersion}'. " +
                   "Remove any tiro-web-sdk <script> tag from the page (the harness loads its own validated copy) and clear stale WebView2 caches.")
        {
            ExpectedVersion = expectedVersion;
            ReportedVersion = reportedVersion;
        }
    }
}
