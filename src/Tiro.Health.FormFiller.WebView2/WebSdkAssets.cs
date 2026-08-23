using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// The embedded <c>@tiro-health/web-sdk</c> bundle (GH-60). The harness ships the
    /// exact SDK it was validated against (pinned in <c>build/web-sdk/package.json</c>)
    /// and serves it to the page over a dedicated virtual host; the bridge injects the
    /// script tag. The page carries no SDK reference — the SDK version is not an
    /// integrator or deployment choice.
    /// </summary>
    internal static class WebSdkAssets
    {
        private const string BundleResourceName = "Tiro.Health.FormFiller.WebView2.WebAssets.tiro-web-sdk.iife.js";
        private const string VersionResourceName = "Tiro.Health.FormFiller.WebView2.WebAssets.web-sdk.version.json";
        internal const string BundleFileName = "tiro-web-sdk.iife.js";

        // Must match SDK_URL in WebAssets/tiro-swm-bridge.js — pinned together by
        // TestEmbeddedWebAssets.Bridge_LoadsSdkFromTheMappedVirtualHost.
        internal const string VirtualHostName = "tiro-sdk.example";

        private static readonly Lazy<string> _folderPath = new Lazy<string>(
            Extract, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<(string Version, string Expected)> _manifest =
            new Lazy<(string, string)>(ReadManifest, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Folder holding the extracted bundle, for the SDK virtual-host mapping.</summary>
        public static string FolderPath => _folderPath.Value;

        /// <summary>The embedded bundle's package version. Generated at staging time, never hand-written.</summary>
        public static string Version => _manifest.Value.Version;

        /// <summary>
        /// The element version the handshake must report (GH-61), or <c>null</c> while
        /// the pinned SDK predates a static element version (atticus-frontend#2927). The
        /// assert arms itself on the first pin bump that sets this — no code change.
        /// </summary>
        public static string ExpectedElementVersion => _manifest.Value.Expected;

        private static string Extract()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            // Subfolder keyed by the SDK version: a pin switch can never be satisfied by
            // a stale byte-length-equal bundle left from another pin.
            var folder = Path.Combine(EmbeddedAssetExtraction.AssemblyVersionFolder(asm), "web-sdk", Version);
            return EmbeddedAssetExtraction.PublishResource(asm, BundleResourceName, folder, BundleFileName);
        }

        private static (string, string) ReadManifest()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            var bytes = EmbeddedAssetExtraction.ReadResource(asm, VersionResourceName);
            using (var doc = JsonDocument.Parse(bytes))
            {
                var version = doc.RootElement.GetStringOrNull("version");
                if (string.IsNullOrEmpty(version))
                    throw new InvalidOperationException(
                        "web-sdk.version.json carries no version — staged bundle metadata is corrupt; re-run build/web-sdk/copy-bundle.mjs.");
                return (version, doc.RootElement.GetStringOrNull("expectedElementVersion"));
            }
        }
    }
}
