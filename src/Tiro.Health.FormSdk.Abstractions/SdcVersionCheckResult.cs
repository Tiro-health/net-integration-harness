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
        /// status, a body without the version field, or a version string outside the grammar
        /// (a <c>dev</c> build, a PR checkpoint id). Fails <em>open</em>: a network blip, a
        /// server predating the <c>CapabilityStatement</c> route, or a developer build must
        /// not brick a working deployment.
        /// </summary>
        Unknown = 2,
    }

    /// <summary>
    /// The outcome of one SDC-server version check, with enough detail to explain itself in a
    /// customer's own logs — which is where this lands, since customers self-host the server.
    /// </summary>
    public sealed class SdcVersionCheckResult
    {
        /// <summary>The <c>CapabilityStatement</c> route, tried first (base-relative <c>metadata</c>).</summary>
        public const string CapabilityStatementSource = "CapabilityStatement.software.version";

        /// <summary>The OpenAPI fallback (origin-relative <c>/openapi.json</c>).</summary>
        public const string OpenApiSource = "openapi.json info.version";

        private SdcVersionCheckResult(
            SdcVersionCheckOutcome outcome, string reportedVersion, string source, string detail)
        {
            Outcome = outcome;
            ReportedVersion = reportedVersion;
            Source = source;
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
        /// <param name="source">Where it was read from — see the <c>*Source</c> constants.</param>
        public static SdcVersionCheckResult FromReportedVersion(string reportedVersion, string source)
        {
            var outcome = SdcCompatibility.Evaluate(reportedVersion);
            var detail = outcome == SdcVersionCheckOutcome.Unknown
                ? $"The server reported '{reportedVersion}', which is not a release version " +
                  "(dev builds report 'dev', PR builds a checkpoint id, a server with no version.json 'development')."
                : string.Empty;
            return new SdcVersionCheckResult(outcome, reportedVersion, source, detail);
        }

        /// <summary>
        /// The verdict when no version could be read at all — unreachable server, timeout,
        /// non-success status, or a body without the version field. Always
        /// <see cref="SdcVersionCheckOutcome.Unknown"/>, i.e. fails open.
        /// </summary>
        /// <param name="detail">Why, for the customer's logs.</param>
        public static SdcVersionCheckResult Unavailable(string detail)
            => new SdcVersionCheckResult(SdcVersionCheckOutcome.Unknown, null, null, detail ?? string.Empty);

        /// <summary>The verdict. See <see cref="SdcVersionCheckOutcome"/> for the failure semantics.</summary>
        public SdcVersionCheckOutcome Outcome { get; }

        /// <summary>
        /// The raw version string the server reported, or <c>null</c> when no version could be
        /// read at all. Non-null with <see cref="SdcVersionCheckOutcome.Unknown"/> means a
        /// version was reported but fell outside the grammar (e.g. <c>dev</c>).
        /// </summary>
        public string ReportedVersion { get; }

        /// <summary>
        /// Which document the version came from — <see cref="CapabilityStatementSource"/> or
        /// <see cref="OpenApiSource"/> — or <c>null</c> when neither answered.
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Why the outcome is what it is, for logs: the failing status codes, the transport
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
                    return $"SDC server version {ReportedVersion} satisfies the minimum {MinimumVersion} (read from {Source}).";
                case SdcVersionCheckOutcome.TooOld:
                    return $"SDC server version {ReportedVersion} is older than the minimum {MinimumVersion} required by this harness (read from {Source}).";
                default:
                    return $"SDC server version could not be established (minimum required: {MinimumVersion}). {Detail}";
            }
        }
    }
}
