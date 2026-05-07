using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    [TestClass]
    public class TestEmbeddedWebAssets
    {
        // Logical resource names must match the <LogicalName> entries in
        // Tiro.Health.FormFiller.WebView2.csproj. Renaming a WebAsset file
        // without updating the project file (or vice-versa) silently breaks
        // BridgeJs.cs and DefaultWebContent.cs at runtime — this test fails
        // build-time instead.
        [DataTestMethod]
        [DataRow("Tiro.Health.FormFiller.WebView2.WebAssets.index.html")]
        [DataRow("Tiro.Health.FormFiller.WebView2.WebAssets.tiro-swm-bridge.js")]
        public void WebAsset_IsEmbeddedAndNonEmpty(string resourceName)
        {
            var asm = typeof(TiroFormViewerState).Assembly;

            using var stream = asm.GetManifestResourceStream(resourceName);

            Assert.IsNotNull(stream, $"Resource '{resourceName}' was not embedded in {asm.GetName().Name}.");
            Assert.IsTrue(stream.Length > 0, $"Resource '{resourceName}' is empty.");
        }

        // The shipped index.html is a placeholder: it carries a banner
        // identifying itself as the library default plus a "Copy starter
        // template" button. Removing either silently regresses issue #7's
        // discovery UX, so we pin the markers here.
        [TestMethod]
        public void IndexHtml_ContainsPlaceholderBannerMarkers()
        {
            var html = ReadEmbeddedString("Tiro.Health.FormFiller.WebView2.WebAssets.index.html");

            StringAssert.Contains(html, "id=\"sample-banner\"");
            StringAssert.Contains(html, "id=\"copy-template-btn\"");
            StringAssert.Contains(html, "data-sample-only");
        }

        // Issue #6: endpoints are configured from the .NET host via
        // TiroFormViewer.SdcEndpointAddress / DataEndpointAddress, not hardcoded
        // in the page. Pinning this keeps a future "small fix" from re-adding
        // a baked-in URL that would silently override host configuration.
        [TestMethod]
        public void IndexHtml_DoesNotHardcodeEndpointAttributes()
        {
            var html = ReadEmbeddedString("Tiro.Health.FormFiller.WebView2.WebAssets.index.html");

            Assert.IsFalse(html.Contains("sdc-endpoint-address="),
                "index.html must not hardcode sdc-endpoint-address; let the .NET host configure it.");
            Assert.IsFalse(html.Contains("data-endpoint-address="),
                "index.html must not hardcode data-endpoint-address; let the .NET host configure it.");
        }

        // The bridge applies window.__tiroFormFillerConfig to every <tiro-form-filler>
        // it wires. The .NET host's TiroFormViewer.SdcEndpointAddress / DataEndpointAddress
        // pipeline injects that object via AddScriptToExecuteOnDocumentCreatedAsync.
        // Pinning the contract here keeps the JS and C# halves from drifting apart.
        [TestMethod]
        public void BridgeJs_ReadsTiroFormFillerConfig()
        {
            var js = ReadEmbeddedString("Tiro.Health.FormFiller.WebView2.WebAssets.tiro-swm-bridge.js");

            StringAssert.Contains(js, "window.__tiroFormFillerConfig");
            StringAssert.Contains(js, "sdc-endpoint-address");
            StringAssert.Contains(js, "data-endpoint-address");
        }

        private static string ReadEmbeddedString(string resourceName)
        {
            var asm = typeof(TiroFormViewerState).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName);
            Assert.IsNotNull(stream, $"Resource '{resourceName}' was not embedded.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
