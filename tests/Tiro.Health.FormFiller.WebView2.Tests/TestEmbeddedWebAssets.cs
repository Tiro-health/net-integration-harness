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
    }
}
