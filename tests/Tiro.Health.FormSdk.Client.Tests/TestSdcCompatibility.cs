using Tiro.Health.FormSdk.Abstractions;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// The version grammar and the comparison rule (GH-62). The server's version string comes
    /// from <c>APP_VERSION</c>, which the deploy pipelines set to the git tag — so it is
    /// <c>v</c>-prefixed, prereleases are routine, and non-release builds report words. Every
    /// case below is one of those real shapes, and the whole point of the check is that a
    /// shape it doesn't recognise never reads as "too old".
    /// </summary>
    [TestClass]
    public sealed class TestSdcCompatibility
    {
        [TestMethod]
        public void MinimumSdcVersion_ParsesUnderItsOwnGrammar()
        {
            // Evaluate() falls back to Unknown if the constant is unparseable, so a typo in it
            // would silently disarm the gate everywhere rather than fail loudly. This is the
            // test that keeps that from happening.
            Assert.IsTrue(
                SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out _, out _, out _),
                $"MinimumSdcVersion '{SdcCompatibility.MinimumSdcVersion}' does not match the version grammar.");
        }

        [DataTestMethod]
        // v-prefixed and bare, both of which the pipelines can produce.
        [DataRow("v0.9.38", 0, 9, 38)]
        [DataRow("0.9.38", 0, 9, 38)]
        // A prerelease: the suffix parses away, leaving the release triple (see the rule test below).
        [DataRow("v0.9.38-rc.0", 0, 9, 38)]
        [DataRow("v1.2.3+build.5", 1, 2, 3)]
        [DataRow("v10.20.30", 10, 20, 30)]
        public void TryParseVersion_AcceptsTheShapesTheDeployPipelinesProduce(
            string value, int major, int minor, int patch)
        {
            Assert.IsTrue(SdcCompatibility.TryParseVersion(value, out var m, out var n, out var p));
            Assert.AreEqual(major, m);
            Assert.AreEqual(minor, n);
            Assert.AreEqual(patch, p);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("dev")]                    // dev builds
        [DataRow("development")]            // no version.json
        [DataRow("cp-3f9a2b1")]             // a PR build's checkpoint id
        [DataRow("v0.9")]                   // two-part: not the grammar
        [DataRow("v0.9.38.1")]              // four-part: not the grammar
        [DataRow("release-0.9.38")]         // prefixed with something other than 'v'
        [DataRow("99999999999999999999.0.0")] // matches the shape but overflows int
        public void TryParseVersion_RejectsEverythingElse(string value)
        {
            Assert.IsFalse(SdcCompatibility.TryParseVersion(value, out _, out _, out _));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("dev")]
        [DataRow("development")]
        [DataRow("cp-3f9a2b1")]
        public void Evaluate_UnrecognizedVersion_IsUnknownNotTooOld(string value)
        {
            // The whole failure-semantics decision in one assertion: anything we can't read
            // fails OPEN. A dev build or a format change must not brick a deployment.
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, SdcCompatibility.Evaluate(value));
        }

        [TestMethod]
        public void Evaluate_TheMinimumItself_IsSatisfied()
        {
            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied,
                SdcCompatibility.Evaluate(SdcCompatibility.MinimumSdcVersion));
        }

        [TestMethod]
        public void Evaluate_APrereleaseOfTheMinimum_IsSatisfied()
        {
            // The decided prerelease rule, laxer than semver on purpose: the production deploy
            // accepts any tag, so an rc can legitimately reach a customer, and failing closed
            // there would brick a deployment that almost certainly has the feature.
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);
            var rcOfTheMinimum = $"v{major}.{minor}.{patch}-rc.0";

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, SdcCompatibility.Evaluate(rcOfTheMinimum),
                $"'{rcOfTheMinimum}' must satisfy a minimum of '{SdcCompatibility.MinimumSdcVersion}'.");
        }

        [TestMethod]
        public void Evaluate_ComparesTheTripleComponentwise_NotLexically()
        {
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);

            // Written against whatever the constant currently is, so a bump doesn't quietly
            // turn these into assertions about a version nobody ships. Components are only
            // decremented where that stays a valid version.
            Assert.AreEqual(SdcVersionCheckOutcome.TooOld, SdcCompatibility.Evaluate(OneBelow(major, minor, patch)),
                "The version immediately below the floor must fail closed.");

            if (minor > 0)
                Assert.AreEqual(SdcVersionCheckOutcome.TooOld, SdcCompatibility.Evaluate($"v{major}.{minor - 1}.{patch + 500}"),
                    "A lower minor is too old however high its patch — string ordering would say otherwise.");

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, SdcCompatibility.Evaluate($"v{major}.{minor + 1}.0"),
                "A higher minor satisfies the floor even at patch 0.");
            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, SdcCompatibility.Evaluate($"v{major + 1}.0.0"),
                "A higher major satisfies the floor however low its minor/patch.");
        }

        private static string OneBelow(int major, int minor, int patch)
            => patch > 0 ? $"v{major}.{minor}.{patch - 1}"
             : minor > 0 ? $"v{major}.{minor - 1}.999"
             : $"v{major - 1}.999.999";
    }
}
