using System;
using System.IO;
using System.Threading;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Loads the embedded SMART Web Messaging bridge JS that
    /// <see cref="TiroFormViewer{TResource,TQR,TOO}"/> injects into every page via
    /// WebView2's <c>AddScriptToExecuteOnDocumentCreatedAsync</c>. The page itself is
    /// UI-only; this script provides protocol, transport, telemetry, and form-filler
    /// auto-wiring on the page side.
    /// </summary>
    internal static class BridgeJs
    {
        private const string ResourceName = "Tiro.Health.FormFiller.WebView2.WebAssets.tiro-swm-bridge.js";

        private static readonly Lazy<string> _swmBridge = new Lazy<string>(
            () => Read(ResourceName), LazyThreadSafetyMode.ExecutionAndPublication);

        public static string SwmBridge => _swmBridge.Value;

        private static string Read(string name)
        {
            var asm = typeof(BridgeJs).Assembly;
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource not found: " + name);
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }
    }
}
