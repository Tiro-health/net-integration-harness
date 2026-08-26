namespace Tiro.Health.FormSdk.Abstractions
{
    /// <summary>The verdict of an SDC-server version check.</summary>
    public enum SdcVersionCheckOutcome
    {
        /// <summary>The server reported a version at or above <see cref="SdcCompatibility.MinimumSdcVersion"/>.</summary>
        Satisfied = 0,

        /// <summary>
        /// The server reported a version below <see cref="SdcCompatibility.MinimumSdcVersion"/>.
        /// The only outcome that fails closed.
        /// </summary>
        TooOld = 1,

        /// <summary>
        /// The version could not be established — unreachable server, timeout, non-success
        /// status, a body without the version field, a document that isn't the SDC server's, or
        /// a version string outside the grammar (a <c>dev</c> build, a PR checkpoint id). Fails
        /// <em>open</em>: a network blip, a server predating the <c>CapabilityStatement</c>
        /// route, or a developer instance must not brick a working deployment.
        /// </summary>
        Unknown = 2,
    }

    /// <summary>
    /// The outcome of one SDC-server version check, with enough detail to explain itself in a
    /// customer's own logs — which is where this lands, since customers self-host the server.
    /// </summary>
    public sealed class SdcVersionCheckResult
    {
        // Longest server-reported string echoed into a message, a log line or a telemetry
        // breadcrumb. The response cap is 2 MB, so without this a server (or anything that can
        // answer as one) could put a megabyte of its choosing into a Sentry breadcrumb on every
        // form launch. A real version is under 20 characters.
        private const int MaxEchoedLength = 64;

        // Backstop on the whole Detail string, applied in both factories so the bound holds for
        // every caller — including a host that overrides the probe — rather than only where
        // someone remembered to clamp an interpolated part. Larger than MaxEchoedLength because
        // Detail legitimately carries a URL and a status alongside any echoed text.
        private const int MaxDetailLength = 512;

        private SdcVersionCheckResult(
            SdcVersionCheckOutcome outcome, string reportedVersion, string detail)
        {
            Outcome = outcome;
            ReportedVersion = reportedVersion;
            Detail = detail;
        }

        /// <summary>
        /// The verdict for a version the server actually reported, evaluated against
        /// <see cref="SdcCompatibility.MinimumSdcVersion"/>. A string outside the version
        /// grammar (a <c>dev</c> build, a PR checkpoint id) yields
        /// <see cref="SdcVersionCheckOutcome.Unknown"/>, never
        /// <see cref="SdcVersionCheckOutcome.TooOld"/>.
        /// </summary>
        /// <param name="reportedVersion">The raw string the server reported.</param>
        public static SdcVersionCheckResult FromReportedVersion(string reportedVersion)
        {
            var outcome = SdcCompatibility.Evaluate(reportedVersion);
            var clamped = Clamp(reportedVersion);
            var detail = outcome == SdcVersionCheckOutcome.Unknown
                ? $"The server reported '{clamped}', which is not a release version " +
                  "(dev builds report 'dev', PR builds a checkpoint id, a server with no version.json 'development')."
                : string.Empty;
            return new SdcVersionCheckResult(outcome, clamped, Truncate(detail, MaxDetailLength));
        }

        /// <summary>
        /// The verdict when no version could be read at all — unreachable server, timeout,
        /// non-success status, a body without the version field, or a document that could not be
        /// attributed to the SDC server. Always <see cref="SdcVersionCheckOutcome.Unknown"/>,
        /// i.e. fails open.
        /// </summary>
        /// <param name="detail">Why, for the customer's logs.</param>
        public static SdcVersionCheckResult Unavailable(string detail)
            => new SdcVersionCheckResult(
                SdcVersionCheckOutcome.Unknown, null, Truncate(detail ?? string.Empty, MaxDetailLength));

        /// <summary>
        /// Truncates a server-supplied string to a length that is safe to put in a log line,
        /// an exception message or a telemetry breadcrumb.
        /// </summary>
        internal static string Clamp(string value) => Truncate(value, MaxEchoedLength);

        private static string Truncate(string value, int maxLength)
        {
            if (value == null || value.Length <= maxLength) return value;

            // Never cut between the halves of a surrogate pair: a lone surrogate is not a valid
            // string, and this text goes on to be serialized (into a Sentry breadcrumb, among
            // other places) by code entitled to assume it is.
            var length = maxLength;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }

        /// <summary>The verdict. See <see cref="SdcVersionCheckOutcome"/> for the failure semantics.</summary>
        public SdcVersionCheckOutcome Outcome { get; }

        /// <summary>
        /// The version string the server reported (truncated if absurdly long), or <c>null</c>
        /// when no version could be read at all. Non-null with
        /// <see cref="SdcVersionCheckOutcome.Unknown"/> means a version was reported but fell
        /// outside the grammar (e.g. <c>dev</c>).
        /// </summary>
        public string ReportedVersion { get; }

        /// <summary>
        /// Why the outcome is what it is, for logs: the failing status code, the transport
        /// error, or the unrecognized version string. Never <c>null</c>.
        /// </summary>
        public string Detail { get; }

        /// <summary>The floor this was evaluated against.</summary>
        public string MinimumVersion => SdcCompatibility.MinimumSdcVersion;


        /// <summary>A single line naming the outcome, both versions, and the source.</summary>
        public override string ToString()
        {
            switch (Outcome)
            {
                case SdcVersionCheckOutcome.Satisfied:
                    return $"SDC server version {ReportedVersion} satisfies the minimum {MinimumVersion} " +
                           "(read from CapabilityStatement.software.version).";
                case SdcVersionCheckOutcome.TooOld:
                    return $"SDC server version {ReportedVersion} is older than the minimum {MinimumVersion} " +
                           "required by this harness (read from CapabilityStatement.software.version).";
                default:
                    return $"SDC server version could not be established (minimum required: {MinimumVersion}). {Detail}";
            }
        }
    }
}
