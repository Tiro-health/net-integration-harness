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
        // The virtual host serves this by name, so the name is what the browser caches
        // against. Versioned: the bytes change with every pin bump while the host stays
        // constant, and a constant URL let WebView2 serve a previous release's bundle after
        // an upgrade. Cache-busting by URL prevents that rather than detecting it.
        internal static string BundleFileName => $"tiro-web-sdk.{Version}.iife.js";

        internal const string VirtualHostName = "tiro-sdk.example";

        /// <summary>
        /// Absolute URL the bridge loads the bundle from. Injected into the page as
        /// <c>window.__tiroSdkUrl</c> before the bridge runs, because the bridge is a static
        /// asset and cannot know the version.
        /// </summary>
        internal static string BundleUrl => $"https://{VirtualHostName}/{BundleFileName}";

        private static readonly Lazy<string> _folderPath = new Lazy<string>(
            Extract, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<string> _manifest =
            new Lazy<string>(ReadManifest, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Folder holding the extracted bundle, for the SDK virtual-host mapping.</summary>
        public static string FolderPath => _folderPath.Value;

        /// <summary>The embedded bundle's package version. Generated at staging time, never hand-written.</summary>
        public static string Version => _manifest.Value;


        private static string Extract()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            // Also keyed by version — belt-and-braces. The versioned FILE NAME is the real
            // guard (it is what the browser caches against); this keeps a pin switch from
            // reusing a stale byte-length-equal file on disk.
            var folder = Path.Combine(EmbeddedAssetExtraction.AssemblyVersionFolder(asm), "web-sdk", Version);
            return EmbeddedAssetExtraction.PublishResource(asm, BundleResourceName, folder, BundleFileName);
        }

        private static string ReadManifest()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            var bytes = EmbeddedAssetExtraction.ReadResource(asm, VersionResourceName);
            using (var doc = JsonDocument.Parse(bytes))
            {
                var version = doc.RootElement.GetStringOrNull("version");
                if (string.IsNullOrEmpty(version))
                    throw new InvalidOperationException(
                        "web-sdk.version.json carries no version — staged bundle metadata is corrupt; re-run build/web-sdk/copy-bundle.mjs.");
                return version;
            }
        }
    }
}
