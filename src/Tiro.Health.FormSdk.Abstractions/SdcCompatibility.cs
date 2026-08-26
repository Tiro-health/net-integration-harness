using System.Globalization;
using System.Text.RegularExpressions;

namespace Tiro.Health.FormSdk.Abstractions
{
    /// <summary>
    /// The SDC-server compatibility contract this harness release ships: the minimum server
    /// version it supports, and the grammar/comparison rules used to decide whether a server
    /// meets it. Pure — no I/O; see <see cref="SdcServerVersionProbe"/> for the check that
    /// reads a live server.
    /// </summary>
    /// <remarks>
    /// The harness embeds the web-sdk bundle it was validated against (GH-60), so the SDC
    /// server is the only component that can still change underneath a frozen release —
    /// customers run and upgrade their own instance. This type is the second and last number
    /// in the integrator story: <em>pin the harness NuGet; run an SDC server at or above
    /// <see cref="MinimumSdcVersion"/></em>.
    /// </remarks>
    public static class SdcCompatibility
    {
        /// <summary>
        /// The oldest SDC server version this harness release supports. A server that reports
        /// an older version is refused at startup (<see cref="SdcServerTooOldException"/>);
        /// a server whose version can't be read or parsed is allowed through. Raise this in
        /// lockstep with the release notes whenever the harness starts to depend on newer
        /// server behaviour.
        /// <para>
        /// <c>v0.9.39</c> is the release that first answers <c>{base}/metadata</c>, which is how
        /// the version is read at all — so it is the honest statement of the requirement: an SDC
        /// server new enough to declare itself. It also means the gate cannot yet <em>refuse</em>
        /// anything: every server able to answer the probe is at or above this floor by
        /// construction, and an older one reads as <see cref="SdcVersionCheckOutcome.Unknown"/>
        /// and is let through. The fail-closed arm arms on the first raise past <c>v0.9.39</c>,
        /// once releases exist that answer the probe and are below the floor.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Deliberately <c>static readonly</c> rather than <c>const</c>. A <c>const</c> is
        /// substituted into every consuming assembly at <em>its</em> compile time, so a host
        /// that reads this value to show integrators or support which floor applies would keep
        /// printing the floor it was built against while this package enforced a newer one —
        /// two copies of a version number drifting, which is the failure this package exists to
        /// prevent.
        /// </remarks>
        public static readonly string MinimumSdcVersion = "v0.9.39";

        /// <summary>
        /// The version string the SDC server reports. It is <b>not</b> plain semver: it comes
        /// from <c>APP_VERSION</c>, which the deploy pipelines set to the git tag, so it is
        /// <c>v</c>-prefixed and routinely carries a prerelease suffix (<c>v0.9.38-rc.0</c>).
        /// Dev builds report <c>dev</c>, PR builds a checkpoint id, and a server with no
        /// <c>version.json</c> reports <c>development</c> — none of which match, and all of
        /// which are therefore treated as "unknown" rather than "too old".
        /// <para>
        /// Deliberately hand-rolled rather than delegating to <c>System.Version</c> (which
        /// handles neither the <c>v</c> nor the <c>-rc.0</c>) or to a semver NuGet package
        /// (which this harness does not need a dependency on).
        /// </para>
        /// </summary>
        private static readonly Regex VersionGrammar =
            new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses a reported server version into its numeric triple. Returns <c>false</c> for
        /// anything that doesn't match the grammar — <c>dev</c>, <c>development</c>, a PR
        /// checkpoint id, a two-part version, an out-of-range number — which callers must
        /// treat as "unknown", never as "too old".
        /// </summary>
        public static bool TryParseVersion(string value, out int major, out int minor, out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrEmpty(value)) return false;

            var match = VersionGrammar.Match(value.Trim());
            if (!match.Success) return false;

            // Parsed into locals and assigned only on full success, so a failed call never
            // leaves half a version in the caller's variables. That is reachable: `\d` matches
            // Unicode digits (there is no RegexOptions.ECMAScript here) while int.TryParse with
            // InvariantCulture does not, so "v0.9.٣٨" matches the grammar and fails the parse.
            // An absurdly long digit run overflows the same way. Both land the caller on
            // "unknown", which is the safe side.
            if (!TryParseComponent(match.Groups[1].Value, out var parsedMajor)) return false;
            if (!TryParseComponent(match.Groups[2].Value, out var parsedMinor)) return false;
            if (!TryParseComponent(match.Groups[3].Value, out var parsedPatch)) return false;

            major = parsedMajor;
            minor = parsedMinor;
            patch = parsedPatch;
            return true;
        }

        private static bool TryParseComponent(string value, out int parsed)
            => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);

        /// <summary>
        /// Evaluates a reported server version against <see cref="MinimumSdcVersion"/>.
        /// </summary>
        /// <remarks>
        /// <b>Prerelease rule (decided, not inherited from a parser):</b> only
        /// <c>(major, minor, patch)</c> is compared — the <c>-rc.N</c> / <c>+build</c> suffix
        /// is ignored on both sides. So <c>v0.9.38-rc.0</c> <em>satisfies</em> a minimum of
        /// <c>v0.9.38</c>, which is laxer than semver, where a prerelease sorts below its
        /// release. That is deliberate: the production deploy accepts any tag, so an rc can
        /// legitimately reach a customer, and failing closed there would brick a deployment
        /// that almost certainly does have the feature.
        /// </remarks>
        public static SdcVersionCheckOutcome Evaluate(string reportedVersion)
        {
            if (!TryParseVersion(reportedVersion, out var major, out var minor, out var patch))
                return SdcVersionCheckOutcome.Unknown;

            // A typo in the constant above would otherwise brick every deployment. Unit-tested
            // to parse, but fail open rather than closed if that test ever stops running.
            if (!TryParseVersion(MinimumSdcVersion, out var minMajor, out var minMinor, out var minPatch))
                return SdcVersionCheckOutcome.Unknown;

            if (major != minMajor) return major > minMajor ? SdcVersionCheckOutcome.Satisfied : SdcVersionCheckOutcome.TooOld;
            if (minor != minMinor) return minor > minMinor ? SdcVersionCheckOutcome.Satisfied : SdcVersionCheckOutcome.TooOld;
            return patch >= minPatch ? SdcVersionCheckOutcome.Satisfied : SdcVersionCheckOutcome.TooOld;
        }
    }
}
