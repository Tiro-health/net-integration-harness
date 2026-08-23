using System;
using System.IO;
using System.Reflection;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Shared machinery for publishing embedded WebAssets into a per-version temp
    /// folder that WebView2 virtual hosts can map (used by <see cref="DefaultWebContent"/>
    /// and <see cref="WebSdkAssets"/>).
    /// </summary>
    internal static class EmbeddedAssetExtraction
    {
        internal static byte[] ReadResource(Assembly asm, string resourceName)
        {
            using (var stream = OpenResource(asm, resourceName))
            using (var ms = new MemoryStream((int)stream.Length))
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>%TEMP%\Tiro.Health.FormFiller.WebView2\{assembly version}</summary>
        internal static string AssemblyVersionFolder(Assembly asm)
        {
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";
            return Path.Combine(Path.GetTempPath(), "Tiro.Health.FormFiller.WebView2", version);
        }

        /// <summary>
        /// Publishes an embedded resource as <paramref name="fileName"/> inside
        /// <paramref name="folder"/> and returns the folder. The fast path compares
        /// stream length only, so warm starts never materialize the bytes. Race-safe
        /// across processes: unique temp name per writer, atomic publish.
        /// </summary>
        internal static string PublishResource(Assembly asm, string resourceName, string folder, string fileName)
        {
            using (var stream = OpenResource(asm, resourceName))
            {
                var target = Path.Combine(folder, fileName);

                // Length-mismatch re-extract covers dev iteration where version stays
                // fixed but content changes.
                var targetInfo = new FileInfo(target);
                if (targetInfo.Exists && targetInfo.Length == stream.Length)
                    return folder;

                byte[] content;
                using (var ms = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(ms);
                    content = ms.ToArray();
                }

                Directory.CreateDirectory(folder);

                // File.Replace is atomic on NTFS and POSIX, so concurrent navigations to
                // `target` never observe a missing file. Falls back to File.Move on the
                // first publish (when target doesn't yet exist).
                var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(temp, content);
                try
                {
                    if (File.Exists(target))
                        File.Replace(temp, target, destinationBackupFileName: null);
                    else
                        File.Move(temp, target);
                }
                catch (IOException)
                {
                    // Lost a race to another writer — their bytes are identical by
                    // version pinning, so the existing target is correct.
                    try { File.Delete(temp); } catch { /* best-effort */ }
                }

                return folder;
            }
        }

        private static Stream OpenResource(Assembly asm, string resourceName)
        {
            var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new InvalidOperationException("Embedded resource not found: " + resourceName);
            return stream;
        }
    }
}
