using System;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Settings for <see cref="FileTelemetrySink"/>. Every property has a working default, so the
    /// usual case needs none of this — pass one of these only to change something.
    /// </summary>
    /// <remarks>
    /// An options object rather than more constructor overloads, and <b>properties rather than
    /// constants</b>. Publishing <c>const</c> limits would have been the worst of both worlds: a
    /// value a consumer can read but not change reads as configuration when it is not, and a
    /// <c>const</c> in a NuGet public surface is inlined at the consumer's compile site, so
    /// changing one later would leave every already-built integrator reporting the old number until
    /// they rebuild. These are the settings a real deployment asks to move — a slower support loop
    /// wants longer retention; a transcript on a roaming profile wants smaller files.
    /// </remarks>
    public sealed class FileTelemetryOptions
    {
        /// <summary>
        /// Where transcripts are written. Defaults to <see cref="FileTelemetrySink.DefaultDirectory"/>
        /// (<c>%LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry</c>).
        /// <para>
        /// The retention sweep only ever deletes files whose names match the ones this component
        /// writes (<c>yyyyMMdd[-n][-p&lt;pid&gt;].jsonl</c>), so pointing this at a directory that
        /// holds other files — including other <c>.jsonl</c> files — is safe.
        /// </para>
        /// </summary>
        public string Directory { get; set; } = FileTelemetrySink.DefaultDirectory;

        /// <summary>
        /// Days of transcripts kept; 7 by default. Sized to how long a support request actually
        /// takes to arrive — a clinician hits a problem on Friday, IT raises a ticket on Monday,
        /// someone asks for the file on Tuesday — rather than to disk pressure, of which there is
        /// none at a few dozen records per session. Zero or less disables the age bound.
        /// </summary>
        public int RetentionDays { get; set; } = 7;

        /// <summary>
        /// Cap per file; 8 MB by default. Reaching it rolls to the next index rather than stopping,
        /// so a full file costs the oldest records and never the newest.
        /// </summary>
        public long MaxBytesPerFile { get; set; } = 8L * 1024 * 1024;

        /// <summary>
        /// Budget for the whole directory; 64 MB by default. The second of two bounds that do not
        /// multiply: with a per-file cap and a file <i>count</i>, the product is the real ceiling
        /// and nobody reads it off the two numbers. Zero or less disables the size bound.
        /// </summary>
        public long MaxTotalBytes { get; set; } = 64L * 1024 * 1024;

        internal FileTelemetryOptions Validated()
        {
            if (string.IsNullOrEmpty(Directory))
                throw new ArgumentException("FileTelemetryOptions.Directory must not be null or empty.", nameof(Directory));

            return new FileTelemetryOptions
            {
                Directory = Directory,
                RetentionDays = RetentionDays,
                MaxBytesPerFile = MaxBytesPerFile > 0 ? MaxBytesPerFile : long.MaxValue,
                MaxTotalBytes = MaxTotalBytes,
            };
        }
    }
}
