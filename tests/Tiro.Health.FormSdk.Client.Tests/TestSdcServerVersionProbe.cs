using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tiro.Health.FormSdk.Abstractions;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// The version read (GH-62): one <c>GET {base}/metadata</c> for
    /// <c>CapabilityStatement.software.version</c>. Base-relative on purpose — whatever answers
    /// it is the same server the operations talk to, so the read is attributable by
    /// construction. An earlier revision fell back to an origin-relative
    /// <c>/openapi.json</c>; that source followed the host rather than the server and could
    /// refuse a healthy deployment on a neighbouring app's version, so it is gone.
    /// </summary>
    [TestClass]
    public sealed class TestSdcServerVersionProbe
    {
        private static readonly Uri SdcBase = new("https://sdc.test.local/fhir/r5");

        /// <summary>Answers the probe URL, and records what was asked for.</summary>
        private sealed class ProbeServer : HttpMessageHandler
        {
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public string Body { get; set; } = "";
            public HttpContent? RawContent { get; set; }
            public TimeSpan Delay { get; set; } = TimeSpan.Zero;
            public List<Uri> RequestedUris { get; } = new();
            public List<string> AcceptHeaders { get; } = new();
            public Exception? ThrowInstead { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestedUris.Add(request.RequestUri!);
                AcceptHeaders.Add(request.Headers.Accept.ToString());
                if (ThrowInstead is not null) throw ThrowInstead;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

                return new HttpResponseMessage(Status)
                {
                    Content = RawContent ?? new StringContent(Body, Encoding.UTF8, "application/fhir+json"),
                };
            }
        }

        private static string Capability(string version, string softwareName = "Tiro.health SDC Server")
            => FakeHttpMessageHandler.CapabilityStatementJson(version, softwareName);

        private static Task<SdcVersionCheckResult> Probe(ProbeServer server)
            => SdcServerVersionProbe.CheckAsync(SdcBase, new HttpClient(server));

        private static string OneBelowTheMinimum()
        {
            SdcCompatibility.TryParseVersion(SdcCompatibility.MinimumSdcVersion, out var major, out var minor, out var patch);
            return patch > 0 ? $"v{major}.{minor}.{patch - 1}"
                 : minor > 0 ? $"v{major}.{minor - 1}.999"
                 : $"v{major - 1}.999.999";
        }

        [TestMethod]
        public async Task ASupportedServer_IsSatisfied_FromOneRequest()
        {
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
            Assert.AreEqual(SdcCompatibility.MinimumSdcVersion, result.ReportedVersion);
            Assert.AreEqual(SdcVersionCheckResult.CapabilityStatementSource, result.Source);
            Assert.AreEqual(1, server.RequestedUris.Count, "One source means one request.");
        }

        [TestMethod]
        public async Task TheRequestIsResolvedBaseRelative_SoAGatewayPathPrefixSurvives()
        {
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion) };
            var behindGateway = new Uri("https://gw.test.local/sdc-service/fhir/r5");

            await SdcServerVersionProbe.CheckAsync(behindGateway, new HttpClient(server));

            // Appending to the configured base is what makes the read attributable: whatever
            // answers this is the server the forms and operations use.
            Assert.AreEqual("https://gw.test.local/sdc-service/fhir/r5/metadata", server.RequestedUris[0].ToString());
        }

        [TestMethod]
        public async Task ABaseWithoutTrailingSlash_DoesNotDropTheLastSegment()
        {
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion) };

            // Without normalization, relative resolution replaces "r5" instead of appending.
            await SdcServerVersionProbe.CheckAsync(new Uri("https://sdc.test.local/fhir/r5"), new HttpClient(server));

            Assert.AreEqual("https://sdc.test.local/fhir/r5/metadata", server.RequestedUris[0].ToString());
        }

        [TestMethod]
        public async Task TheRequestAsksForFhirJson()
        {
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion) };

            await Probe(server);

            StringAssert.Contains(server.AcceptHeaders[0], "application/fhir+json");
        }

        [TestMethod]
        public async Task ATooOldServer_IsTooOld_WithBothVersionsInTheMessage()
        {
            var older = OneBelowTheMinimum();
            var server = new ProbeServer { Body = Capability(older) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.TooOld, result.Outcome);
            Assert.AreEqual(older, result.ReportedVersion);
            StringAssert.Contains(result.ToString(), older);
            StringAssert.Contains(result.ToString(), SdcCompatibility.MinimumSdcVersion);
        }

        [TestMethod]
        public async Task AForeignSoftwareName_IsUnknown_NotTooOld()
        {
            // The attribution guard. A gateway routing {base}/metadata to a different FHIR
            // server must not let that server's version refuse this session — a document we
            // cannot attribute has to fail open, whatever number it carries.
            var server = new ProbeServer { Body = Capability(OneBelowTheMinimum(), softwareName: "Some Other FHIR Server") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "Some Other FHIR Server");
            StringAssert.Contains(result.Detail, "was not used");
        }

        [TestMethod]
        public async Task AMissingSoftwareName_IsStillTrusted()
        {
            // Absence is tolerated rather than treated as a mismatch: requiring the name would
            // add a way for a future server-side change to silently disarm the whole check.
            var server = new ProbeServer
            {
                Body = $@"{{""resourceType"":""CapabilityStatement"",""software"":{{""version"":""{SdcCompatibility.MinimumSdcVersion}""}}}}",
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
        }

        [TestMethod]
        public async Task ANonSuccessStatus_IsUnknownNamingTheStatus()
        {
            // Exactly a server predating the /metadata route: it has no local route for it and
            // falls into its data tunnel, which answers 400.
            var server = new ProbeServer
            {
                Status = HttpStatusCode.BadRequest,
                Body = @"{""resourceType"":""OperationOutcome"",""issue"":[{""severity"":""error""}]}",
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            Assert.IsNull(result.ReportedVersion);
            Assert.IsNull(result.Source);
            StringAssert.Contains(result.Detail, "/metadata");
            StringAssert.Contains(result.Detail, "400");
        }

        [TestMethod]
        public async Task A200WithoutSoftwareVersion_IsUnknown()
        {
            var server = new ProbeServer { Body = @"{""resourceType"":""CapabilityStatement"",""status"":""active""}" };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "without a string software.version");
        }

        [TestMethod]
        public async Task AnEmptyBody_IsReportedAsEmpty_NotAsAMissingField()
        {
            // Worth distinguishing: "nothing came back" and "JSON came back without the field"
            // point at different problems when someone is triaging a proxy.
            var server = new ProbeServer { Body = "" };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "empty body");
        }

        [TestMethod]
        public async Task ATransportFailure_IsUnknown_NotAnException()
        {
            // A DNS failure, a refused connection, a TLS error: all of it fails open. The probe
            // never turns a network blip into a thrown exception at the call site.
            var server = new ProbeServer { ThrowInstead = new HttpRequestException("no such host") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "no such host");
        }

        [TestMethod]
        public async Task MalformedJson_IsUnknown_NotAnException()
        {
            var server = new ProbeServer { Body = "<html>proxy error</html>" };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
        }

        [TestMethod]
        public async Task ADevBuild_IsUnknown_EvenThoughTheServerAnswered()
        {
            var server = new ProbeServer { Body = Capability("dev") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            Assert.AreEqual("dev", result.ReportedVersion,
                "The unusable version is still reported, so a log reader can see what the server said.");
            StringAssert.Contains(result.Detail, "dev");
        }

        [TestMethod]
        public async Task ALeadingByteOrderMark_IsSkipped()
        {
            // JsonDocument rejects a BOM; a proxy or a hand-written server can prepend one.
            var json = Encoding.UTF8.GetBytes(Capability(SdcCompatibility.MinimumSdcVersion));
            var withBom = new byte[json.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(json, 0, withBom, 3, json.Length);
            var server = new ProbeServer { RawContent = new ByteArrayContent(withBom) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome);
        }

        [TestMethod]
        public async Task AnOversizedBody_IsUnknown_AndIsNotBuffered()
        {
            // The cap is a safety valve against a hostile or runaway stream: the real document
            // is ~530 bytes. Sent without a Content-Length so the streaming check is what has
            // to catch it — the header is deliberately not consulted, since a proxy advertising
            // a wrong oversized length would otherwise disarm the check on a fine body.
            var server = new ProbeServer { RawContent = new StreamContent(new EndlessStream()) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "exceeded");
        }

        [TestMethod]
        public async Task ALyingOversizedContentLength_DoesNotDisarmTheCheck()
        {
            var body = new ByteArrayContent(Encoding.UTF8.GetBytes(Capability(SdcCompatibility.MinimumSdcVersion)));
            body.Headers.ContentLength = 3_000_000;   // a proxy that cannot count
            var server = new ProbeServer { RawContent = body };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Satisfied, result.Outcome,
                "The body is small and valid; only what actually arrives may trip the cap.");
        }

        [TestMethod]
        public async Task AStalledServer_TimesOutWithinItsOwnDeadline_AndFailsOpen()
        {
            // The deadline is what keeps a startup check from becoming the reason a form takes
            // long to appear — and what the viewer's launch budget relies on.
            var server = new ProbeServer { Delay = TimeSpan.FromMilliseconds(SdcServerVersionProbe.TimeoutMilliseconds * 4) };
            var stopwatch = Stopwatch.StartNew();

            var result = await Probe(server);
            stopwatch.Stop();

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "timed out");
            Assert.IsTrue(
                stopwatch.ElapsedMilliseconds < SdcServerVersionProbe.TimeoutMilliseconds * 3,
                $"Took {stopwatch.ElapsedMilliseconds} ms; the probe must give up near its own {SdcServerVersionProbe.TimeoutMilliseconds} ms deadline.");
        }

        [TestMethod]
        public async Task CallerCancellation_Propagates_RatherThanBecomingUnknown()
        {
            // A caller's own cancellation is their intent, not a probe failure — the one server
            // condition that is allowed out of CheckAsync.
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion) };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Asserted on the base type rather than with ThrowsExceptionAsync (which demands an
            // exact match): HttpClient surfaces a cancelled send as TaskCanceledException.
            try
            {
                await SdcServerVersionProbe.CheckAsync(SdcBase, new HttpClient(server), cts.Token);
                Assert.Fail("A cancelled caller token must not come back as a fail-open result.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.AreEqual(cts.Token, ex.CancellationToken,
                    "It must be rethrown against the caller's token, or their own `when (e.CancellationToken == mine)` filter cannot match.");
            }
        }

        [TestMethod]
        public async Task ABadBaseAddress_IsACallerBug_NotAFailOpen()
        {
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                () => SdcServerVersionProbe.CheckAsync(null!));
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => SdcServerVersionProbe.CheckAsync(new Uri("fhir/r5", UriKind.Relative)));
        }

        /// <summary>A body that never ends, for the response-size cap.</summary>
        private sealed class EndlessStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                // Valid JSON-ish filler, so the cap is what stops this rather than a parse error.
                for (var i = 0; i < count; i++) buffer[offset + i] = (byte)' ';
                return count;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
