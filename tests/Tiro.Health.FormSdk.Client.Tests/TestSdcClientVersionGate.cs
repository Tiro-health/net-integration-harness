using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Tiro.Health.FormSdk.Abstractions;
using Tiro.Health.FormSdk.Client.Fhir.R5;
// Hl7.Fhir.Model also defines a `Task` resource; disambiguate the async return type.
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// The startup version gate on the client (GH-62). A wrong harness↔server pairing used to
    /// surface as a generic <see cref="SdcOperationException"/> on the first <c>$extract</c> —
    /// or, worse, as a behavioural difference nobody noticed — in front of a clinician. It is
    /// now a refusal before the operation is sent.
    /// </summary>
    [TestClass]
    public sealed class TestSdcClientVersionGate
    {
        private static readonly Uri BaseAddress = new("https://sdc.test.local/fhir/r5");
        private const string BundleJson = """{"resourceType":"Bundle","type":"transaction"}""";
        private const string OutcomeJson =
            """{"resourceType":"OperationOutcome","issue":[{"severity":"information","code":"informational"}]}""";

        private static QuestionnaireResponse SampleResponse() => new()
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "http://example.org/Questionnaire/intake|1.0.0",
        };

        private static string OneBelowTheMinimum()
        {
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);
            return patch > 0 ? $"v{major}.{minor}.{patch - 1}"
                 : minor > 0 ? $"v{major}.{minor - 1}.999"
                 : $"v{major - 1}.999.999";
        }

        [TestMethod]
        public async Task TooOldServer_RefusesTheOperation_BeforeItIsSent()
        {
            var older = OneBelowTheMinimum();
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataBody = FakeHttpMessageHandler.CapabilityStatementJson(older),
            };
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            var ex = await Assert.ThrowsExceptionAsync<SdcServerTooOldException>(
                () => client.ExtractAsync(SampleResponse()));

            Assert.AreEqual(older, ex.ReportedVersion);
            Assert.AreEqual(SdcCompatibility.MinimumSdcVersion, ex.MinimumVersion);
            // Fail closed means the operation never leaves: nothing was POSTed.
            Assert.IsNull(handler.LastRequest, "A refused pairing must not send the operation anyway.");
            Assert.IsFalse(handler.RequestedUris.Any(u => u.AbsolutePath.Contains("$extract")));
        }

        [TestMethod]
        public async Task UnreachableVersionRoutes_FailOpen_AndTheOperationRuns()
        {
            // A server predating the /metadata route with no openapi.json either, or a network
            // blip: the version is unknown, and unknown must never brick a deployment.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataStatus = HttpStatusCode.BadRequest,
                OpenApiStatus = HttpStatusCode.NotFound,
            };
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            var bundle = await client.ExtractAsync(SampleResponse());

            Assert.IsNotNull(bundle);
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, client.ServerVersionCheck!.Outcome);
        }

        [TestMethod]
        public async Task TheCheckRunsOncePerClient_NotOncePerOperation()
        {
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson);
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            await client.ExtractAsync(SampleResponse());
            await client.ExtractAsync(SampleResponse());

            var probeRequests = handler.RequestedUris.Count(u => u.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal));
            Assert.AreEqual(1, probeRequests,
                "The result is cached: a second operation must not re-probe the server.");
        }

        [TestMethod]
        public async Task TheCheckTravelsTheInjectedHttpClient_SoCustomTlsProxyAndAuthApply()
        {
            // The probe has to go through the client the host configured, or a server behind
            // custom TLS/proxy/auth would answer the operations but not the check.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OutcomeJson);
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            await client.ValidateAsync(SampleResponse());

            Assert.AreEqual("https://sdc.test.local/fhir/r5/metadata", handler.RequestedUris[0].ToString(),
                "The version check must be the first thing on the wire, on the injected client.");
        }

        [TestMethod]
        public async Task ASupportedServer_LeavesTheResultInspectable()
        {
            // The check's telemetry lands in the customer's own logs — they self-host the
            // server — so a host needs a programmatic way to see what it found.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OutcomeJson);
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            Assert.IsNull(client.ServerVersionCheck, "Nothing is probed before the first operation.");

            await client.ValidateAsync(SampleResponse());

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, client.ServerVersionCheck!.Outcome);
            Assert.AreEqual(SdcVersionCheckResult.CapabilityStatementSource, client.ServerVersionCheck!.Source);
        }
    }
}
