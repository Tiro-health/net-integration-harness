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
            var asm = typeof(TiroFormViewerState).Assembly;
            using var stream = asm.GetManifestResourceStream(
                "Tiro.Health.FormFiller.WebView2.WebAssets.index.html");
            Assert.IsNotNull(stream);
            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();

            StringAssert.Contains(html, "id=\"sample-banner\"");
            StringAssert.Contains(html, "id=\"copy-template-btn\"");
            StringAssert.Contains(html, "data-sample-only");
        }
    }
}
