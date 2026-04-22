using System;
using System.IO;
using System.Reflection;
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
            byte[] content;
            using (var stream = asm.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource not found: " + ResourceName);
                using (var ms = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(ms);
                    content = ms.ToArray();
                }
            }

            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";

            var folder = Path.Combine(
                Path.GetTempPath(),
                "Tiro.Health.FormFiller.WebView2",
                version);
            var target = Path.Combine(folder, IndexFileName);

            // Fast path: file already extracted with matching byte length.
            // Length-mismatch re-extract covers dev iteration where version stays fixed but content changes.
            var targetInfo = new FileInfo(target);
            if (targetInfo.Exists && targetInfo.Length == content.Length)
                return folder;

            Directory.CreateDirectory(folder);

            // Unique temp name per writer → temp files never collide across processes/threads.
            // Atomic rename publishes the final file; if another writer wins the race,
            // Move throws and we drop our temp (their bytes are identical by version pinning).
            var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temp, content);
            try
            {
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(temp, target);
            }
            catch (IOException)
            {
                try { File.Delete(temp); } catch { /* best-effort */ }
            }

            return folder;
        }
    }
}
