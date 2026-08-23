using System;
using System.IO;
using System.Threading;

namespace Tiro.Health.FormFiller.WebView2
{
    internal static class DefaultWebContent
    {
        private const string ResourceName = "Tiro.Health.FormFiller.WebView2.WebAssets.index.html";
        private const string IndexFileName = "index.html";

        private static readonly Lazy<string> _folderPath = new Lazy<string>(
            Extract, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string FolderPath => _folderPath.Value;

        private static string Extract()
        {
            var asm = typeof(DefaultWebContent).Assembly;
            var folder = EmbeddedAssetExtraction.AssemblyVersionFolder(asm);
            return EmbeddedAssetExtraction.PublishResource(asm, ResourceName, folder, IndexFileName);
        }
    }
}
