using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tiro.Health.FormSdk.Abstractions;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// The two-source version read (GH-62): <c>CapabilityStatement.software.version</c> first,
    /// <c>openapi.json</c> <c>info.version</c> as the fallback that covers the whole
    /// currently-deployed fleet. Both steps ship now because this harness goes inside frozen
    /// EHR binaries — a fallback omitted here cannot be added later.
    /// </summary>
    [TestClass]
    public sealed class TestSdcServerVersionProbe
    {
        private static readonly Uri SdcBase = new("https://sdc.test.local/fhir/r5");

        /// <summary>Routes the two probe URLs independently, and records what was asked for.</summary>
        private sealed class ProbeServer : HttpMessageHandler
        {
            public HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;
            public string MetadataBody { get; set; } = "";
            public HttpStatusCode OpenApiStatus { get; set; } = HttpStatusCode.OK;
            public string OpenApiBody { get; set; } = "";
            public List<Uri> RequestedUris { get; } = new();
            public List<string> AcceptHeaders { get; } = new();
            public Exception? ThrowInstead { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestedUris.Add(request.RequestUri!);
                AcceptHeaders.Add(request.Headers.Accept.ToString());
                if (ThrowInstead is not null) throw ThrowInstead;

                var isMetadata = request.RequestUri!.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal);
                var status = isMetadata ? MetadataStatus : OpenApiStatus;
                var body = isMetadata ? MetadataBody : OpenApiBody;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private static string Capability(string version) => FakeHttpMessageHandler.CapabilityStatementJson(version);
        private static string OpenApi(string version) => FakeHttpMessageHandler.OpenApiJson(version);

        private static Task<SdcVersionCheckResult> Probe(ProbeServer server)
            => SdcServerVersionProbe.CheckAsync(SdcBase, new HttpClient(server));

        [TestMethod]
        public async Task CapabilityStatement_IsReadFirst_AndOpenApiIsNotFetchedAtAll()
        {
            var server = new ProbeServer { MetadataBody = Capability(SdcCompatibility.MinimumSdcVersion) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
            Assert.AreEqual(SdcCompatibility.MinimumSdcVersion, result.ReportedVersion);
            Assert.AreEqual(SdcVersionCheckResult.CapabilityStatementSource, result.Source);

            // The fallback is ~235 KB against the CapabilityStatement's ~530 B, on the path to
            // showing a clinician a form. It must not be fetched when step 1 answered.
            Assert.AreEqual(1, server.RequestedUris.Count, "openapi.json must not be fetched once /metadata answered.");
        }

        [TestMethod]
        public async Task Metadata_IsResolvedBaseRelative_SoAGatewayPathPrefixSurvives()
        {
            var server = new ProbeServer { MetadataBody = Capability(SdcCompatibility.MinimumSdcVersion) };
            var behindGateway = new Uri("https://gw.test.local/sdc-service/fhir/r5");

            await SdcServerVersionProbe.CheckAsync(behindGateway, new HttpClient(server));

            Assert.AreEqual("https://gw.test.local/sdc-service/fhir/r5/metadata", server.RequestedUris[0].ToString(),
                "Concatenating onto the configured base is the whole reason CapabilityStatement is the primary source.");
        }

        [TestMethod]
        public async Task Metadata_BaseWithoutTrailingSlash_DoesNotDropTheLastSegment()
        {
            var server = new ProbeServer { MetadataBody = Capability(SdcCompatibility.MinimumSdcVersion) };

            // Without normalization, relative resolution replaces "r5" instead of appending.
            await SdcServerVersionProbe.CheckAsync(new Uri("https://sdc.test.local/fhir/r5"), new HttpClient(server));

            Assert.AreEqual("https://sdc.test.local/fhir/r5/metadata", server.RequestedUris[0].ToString());
        }

        [TestMethod]
        public async Task Metadata_IsRequestedAsFhirJson()
        {
            var server = new ProbeServer { MetadataBody = Capability(SdcCompatibility.MinimumSdcVersion) };

            await Probe(server);

            StringAssert.Contains(server.AcceptHeaders[0], "application/fhir+json");
        }

        [TestMethod]
        public async Task Metadata400_FallsBackToOpenApi_WhichEveryDeployedServerAnswers()
        {
            // Exactly production as of writing: /fhir/r5/metadata 400s (it had no local route
            // and fell into the data tunnel), while /openapi.json reports the version.
            var server = new ProbeServer
            {
                MetadataStatus = HttpStatusCode.BadRequest,
                MetadataBody = @"{""resourceType"":""OperationOutcome"",""issue"":[{""severity"":""error""}]}",
                OpenApiBody = OpenApi(SdcCompatibility.MinimumSdcVersion),
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
            Assert.AreEqual(SdcVersionCheckResult.OpenApiSource, result.Source);
            Assert.AreEqual("https://sdc.test.local/openapi.json", server.RequestedUris[1].ToString(),
                "openapi.json is origin-relative — it is a fallback for the pre-/metadata installed base.");
        }

        [TestMethod]
        public async Task Metadata200WithoutSoftwareVersion_FallsBackToOpenApi()
        {
            // A 200 that isn't usable is not the same as a 200 that is: a CapabilityStatement
            // with no software.version must not end the search.
            var server = new ProbeServer
            {
                MetadataBody = @"{""resourceType"":""CapabilityStatement"",""status"":""active""}",
                OpenApiBody = OpenApi(SdcCompatibility.MinimumSdcVersion),
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
            Assert.AreEqual(SdcVersionCheckResult.OpenApiSource, result.Source);
        }

        [TestMethod]
        public async Task NeitherSourceAnswers_IsUnknownWithBothReasons()
        {
            var server = new ProbeServer
            {
                MetadataStatus = HttpStatusCode.NotFound,
                OpenApiStatus = HttpStatusCode.NotFound,
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            Assert.IsNull(result.ReportedVersion);
            Assert.IsNull(result.Source);
            // Both attempts are named, because this lands in the customer's own logs.
            StringAssert.Contains(result.Detail, "/metadata");
            StringAssert.Contains(result.Detail, "/openapi.json");
            StringAssert.Contains(result.Detail, "404");
        }

        [TestMethod]
        public async Task TransportFailure_IsUnknown_NotAnException()
        {
            // A DNS failure, a refused connection, a TLS error: all of it fails open. The
            // probe never turns a network blip into a thrown exception at the call site.
            var server = new ProbeServer { ThrowInstead = new HttpRequestException("no such host") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "no such host");
        }

        [TestMethod]
        public async Task MalformedJson_IsUnknown_NotAnException()
        {
            var server = new ProbeServer { MetadataBody = "<html>proxy error</html>", OpenApiBody = "not json" };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
        }

        [TestMethod]
        public async Task ATooOldServer_IsReportedTooOld_WithBothVersionsInTheMessage()
        {
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);
            var older = patch > 0 ? $"v{major}.{minor}.{patch - 1}"
                      : minor > 0 ? $"v{major}.{minor - 1}.999"
                      : $"v{major - 1}.999.999";
            var server = new ProbeServer { MetadataBody = Capability(older) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.TooOld, result.Outcome);
            Assert.AreEqual(older, result.ReportedVersion);
            StringAssert.Contains(result.ToString(), older);
            StringAssert.Contains(result.ToString(), SdcCompatibility.MinimumSdcVersion);
        }

        [TestMethod]
        public async Task ADevBuild_IsUnknown_EvenThoughTheServerAnswered()
        {
            var server = new ProbeServer { MetadataBody = Capability("dev") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            Assert.AreEqual("dev", result.ReportedVersion,
                "The unusable version is still reported, so a log reader can see what the server said.");
            StringAssert.Contains(result.Detail, "dev");
        }

        [TestMethod]
        public async Task CallerCancellation_Propagates_RatherThanBecomingUnknown()
        {
            // A caller's own cancellation is their intent, not a probe failure — the one case
            // that is allowed out of CheckAsync.
            var server = new ProbeServer { MetadataBody = Capability(SdcCompatibility.MinimumSdcVersion) };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Asserted on the base type rather than with ThrowsExceptionAsync (which demands an
            // exact match): HttpClient surfaces a cancelled send as TaskCanceledException here
            // and net48 has surfaced it as either over the years.
            try
            {
                await SdcServerVersionProbe.CheckAsync(SdcBase, new HttpClient(server), cts.Token);
                Assert.Fail("A cancelled caller token must not come back as a fail-open result.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        public async Task ANullBaseAddress_IsACallerBug_NotAFailOpen()
        {
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                () => SdcServerVersionProbe.CheckAsync(null!));
        }
    }
}
