using System;
using System.IO;
using System.Reflection;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Shared machinery for publishing embedded WebAssets into a per-version temp
    /// folder that WebView2 virtual hosts can map (used by <see cref="DefaultWebContent"/>
    /// and <see cref="WebSdkAssets"/>). Publishing is idempotent and race-safe across
    /// processes: unique temp name per writer, atomic <see cref="File.Replace(string,string,string)"/>,
    /// and identical bytes guaranteed by version pinning.
    /// </summary>
    internal static class EmbeddedAssetExtraction
    {
        internal static byte[] ReadResource(Assembly asm, string resourceName)
        {
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource not found: " + resourceName);
                using (var ms = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
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
        /// Publishes an embedded resource into <paramref name="folder"/>. The fast path
        /// compares stream length only, so warm starts never materialize the bytes.
        /// </summary>
        internal static string PublishResource(Assembly asm, string resourceName, string folder, string fileName)
        {
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource not found: " + resourceName);

                var targetInfo = new FileInfo(Path.Combine(folder, fileName));
                if (targetInfo.Exists && targetInfo.Length == stream.Length)
                    return folder;

                using (var ms = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(ms);
                    return Publish(ms.ToArray(), folder, fileName);
                }
            }
        }

        /// <summary>
        /// Publishes <paramref name="content"/> as <paramref name="fileName"/> inside
        /// <paramref name="folder"/> and returns the folder.
        /// </summary>
        internal static string Publish(byte[] content, string folder, string fileName)
        {
            var target = Path.Combine(folder, fileName);

            // Fast path: file already extracted with matching byte length.
            // Length-mismatch re-extract covers dev iteration where version stays fixed but content changes.
            var targetInfo = new FileInfo(target);
            if (targetInfo.Exists && targetInfo.Length == content.Length)
                return folder;

            Directory.CreateDirectory(folder);

            // Unique temp name per writer → temp files never collide across processes/threads.
            // Publish atomically: File.Replace is atomic on NTFS and POSIX, so concurrent
            // navigations to `target` never observe a missing file. Falls back to File.Move
            // on the first publish (when target doesn't yet exist).
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
                // Lost a race to another writer — their bytes are identical by version
                // pinning, so the existing target is correct. Drop our temp.
                try { File.Delete(temp); } catch { /* best-effort */ }
            }

            return folder;
        }
    }
}
