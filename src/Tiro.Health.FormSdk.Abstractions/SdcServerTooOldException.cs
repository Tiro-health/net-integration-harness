using System;

namespace Tiro.Health.FormSdk.Abstractions
{
    /// <summary>
    /// Thrown when the SDC server reports a version older than
    /// <see cref="SdcCompatibility.MinimumSdcVersion"/>. The pairing is wrong and every
    /// subsequent operation against that server is suspect, so the harness refuses to start
    /// the session rather than letting the mismatch surface later as a generic operation
    /// failure — or, worse, as a silent behavioural difference in front of a clinician.
    /// </summary>
    /// <remarks>
    /// Only a server that <em>answered</em> with a parseable, too-old version produces this.
    /// An unreachable server, a timeout, or an unparseable version fails open — see
    /// <see cref="SdcVersionCheckOutcome"/>. The remedy is to upgrade the SDC server to
    /// <see cref="SdcCompatibility.MinimumSdcVersion"/> or newer, or to run the harness
    /// release whose minimum that server satisfies.
    /// </remarks>
    public class SdcServerTooOldException : Exception
    {
        /// <summary>Creates the exception from a completed check whose outcome is <see cref="SdcVersionCheckOutcome.TooOld"/>.</summary>
        public SdcServerTooOldException(SdcVersionCheckResult result)
            : base((result ?? throw new ArgumentNullException(nameof(result))).ToString())
        {
            ReportedVersion = result.ReportedVersion;
            MinimumVersion = result.MinimumVersion;
        }

        /// <summary>The version the server reported.</summary>
        public string ReportedVersion { get; }

        /// <summary>The minimum this harness release requires.</summary>
        public string MinimumVersion { get; }
    }
}
