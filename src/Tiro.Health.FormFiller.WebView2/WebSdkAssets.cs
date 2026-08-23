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

        private static readonly Lazy<string> _folderPath = new Lazy<string>(
            Extract, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<VersionManifest> _manifest = new Lazy<VersionManifest>(
            ReadManifest, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Folder holding the extracted bundle, for the SDK virtual-host mapping.</summary>
        public static string FolderPath => _folderPath.Value;

        /// <summary>The embedded bundle's package version. Generated at staging time, never hand-written.</summary>
        public static string Version => _manifest.Value.Version;

        /// <summary>
        /// The element version the host asserts at handshake (GH-61), or <c>null</c> while
        /// the pinned SDK predates a static element version (atticus-frontend#2927). The
        /// assert arms itself on the first pin bump that sets this — no code change.
        /// </summary>
        public static string ExpectedElementVersion => _manifest.Value.ExpectedElementVersion;

        private static string Extract()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            var content = EmbeddedAssetExtraction.ReadResource(asm, BundleResourceName);
            // Own subfolder (not DefaultWebContent's): the SDK is served on its own
            // virtual host regardless of whether the consumer supplies WebContentFolder.
            var folder = Path.Combine(EmbeddedAssetExtraction.AssemblyVersionFolder(asm), "web-sdk");
            return EmbeddedAssetExtraction.Publish(content, folder, BundleFileName);
        }

        private static VersionManifest ReadManifest()
        {
            var asm = typeof(WebSdkAssets).Assembly;
            var bytes = EmbeddedAssetExtraction.ReadResource(asm, VersionResourceName);
            using (var doc = JsonDocument.Parse(bytes))
            {
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
                if (string.IsNullOrEmpty(version))
                    throw new InvalidOperationException(
                        "web-sdk.version.json carries no version — staged bundle metadata is corrupt; re-run build/web-sdk/copy-bundle.mjs.");
                var expected = root.TryGetProperty("expectedElementVersion", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
                return new VersionManifest(version, expected);
            }
        }

        private sealed class VersionManifest
        {
            public string Version { get; }
            public string ExpectedElementVersion { get; }

            public VersionManifest(string version, string expectedElementVersion)
            {
                Version = version;
                ExpectedElementVersion = expectedElementVersion;
            }
        }
    }
}
