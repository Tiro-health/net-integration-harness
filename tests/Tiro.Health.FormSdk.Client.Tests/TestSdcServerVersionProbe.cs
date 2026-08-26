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
    /// <c>CapabilityStatement.software.version</c>, accepted only from a document whose
    /// <c>software.name</c> says it is the SDC server. Base-relativity gets the request to the
    /// right host; it does not say who composed the answer — a server predating the route
    /// tunnels <c>{base}/metadata</c> to the configured data endpoint — so attribution is a
    /// property of the body, and the tests below hold it there. An earlier revision also fell
    /// back to an origin-relative <c>/openapi.json</c>, which followed the host rather than the
    /// server and could refuse a healthy deployment on a neighbouring app's version.
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
        public async Task AMissingSoftwareName_IsUnknown_NotTrusted()
        {
            // software.name is 1..1 whenever software is present in R4/R5, so a document that
            // omits it is non-conformant — which is exactly the class that must not be trusted:
            // a response tunnelled to the customer's data endpoint, a hand-written server, a
            // proxy. Trusting it was the one remaining path by which an unattributed document
            // could reach TooOld and refuse a healthy server.
            var server = new ProbeServer
            {
                Body = $@"{{""resourceType"":""CapabilityStatement"",""software"":{{""version"":""{OneBelowTheMinimum()}""}}}}",
            };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "absent");
        }

        [TestMethod]
        public async Task ATunnelledDataServerCapabilityStatement_CannotRefuseTheSession()
        {
            // The topology this guard exists for. On a server predating the metadata route,
            // {base}/metadata falls into the SDC server's data tunnel, so a deployment with
            // DEFAULT_DATA_ENDPOINT configured answers with the HOSPITAL's CapabilityStatement.
            // Base-relativity got the request to the right host; only the body says who wrote
            // it. Whatever version that server reports, it must not decide this session.
            var server = new ProbeServer { Body = Capability("0.4.1", softwareName: "HAPI FHIR Server") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome,
                "A version from a server we cannot attribute must never fail closed.");
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
        public async Task AnOversizedBody_TripsTheCap_AndIsUnknown()
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
        public async Task AServerThatStallsBeforeTheHeaders_TimesOut_AndFailsOpen()
        {
            var server = new ProbeServer { Delay = TimeSpan.FromMilliseconds(SdcServerVersionProbe.TimeoutMilliseconds * 4) };
            var stopwatch = Stopwatch.StartNew();

            var result = await Probe(server);
            stopwatch.Stop();

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "timed out");
            Assert.IsTrue(
                stopwatch.ElapsedMilliseconds < SdcServerVersionProbe.TimeoutMilliseconds * 2,
                $"Took {stopwatch.ElapsedMilliseconds} ms against a {SdcServerVersionProbe.TimeoutMilliseconds} ms deadline.");
        }

        [TestMethod]
        public async Task AServerThatStallsAFTERTheHeaders_TimesOut_AndFailsOpen()
        {
            // The case the response-dispose registration exists for, and the one the previous
            // revision of this test did NOT cover: it stalled inside SendAsync, so the deadline
            // cancelled the *send* — behaviour that needed no registration at all. Here the
            // headers arrive and the BODY stalls, which on net48 is unreachable by the read's
            // own CancellationToken (that overload is the base Begin/EndRead wrapper and ignores
            // it) and no longer covered by SendAsync's token (ResponseHeadersRead already
            // returned). Disposing the response is what unblocks it. Delete the registration and
            // this test hangs for its full 30 s.
            using var blocking = new BlockingStream();
            var server = new ProbeServer { RawContent = new StreamContent(blocking) };
            var stopwatch = Stopwatch.StartNew();

            var result = await Probe(server);
            stopwatch.Stop();

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "timed out");
            Assert.IsTrue(
                stopwatch.ElapsedMilliseconds < SdcServerVersionProbe.TimeoutMilliseconds * 2,
                $"Took {stopwatch.ElapsedMilliseconds} ms against a {SdcServerVersionProbe.TimeoutMilliseconds} ms deadline; " +
                "the body read must be interrupted, not merely the send.");
        }

        [TestMethod]
        public async Task AGenuineDefectIsNotRelabelledAsATimeout()
        {
            // Both cancellation-shaped catch filters are narrowed to the exception types the
            // dispose registration can actually produce. A NullReferenceException from a broken
            // handler that merely coincides with the deadline is a defect and has to read as
            // one, not as "timed out after 3000 ms".
            var server = new ProbeServer { ThrowInstead = new NullReferenceException("handler bug") };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            StringAssert.Contains(result.Detail, "NullReferenceException");
            Assert.IsFalse(result.Detail.Contains("timed out"), "A defect must not be dressed up as a deadline.");
        }

        [TestMethod]
        public async Task AnAbsurdlyLongServerString_IsTruncatedBeforeItReachesAnyMessage()
        {
            // The response cap is 2 MB, so without truncation a server could put a megabyte of
            // its choosing into a Sentry breadcrumb on every form launch. The surrogate pair at
            // the cut point is deliberate: a lone surrogate is not a valid string, and this text
            // gets serialized downstream by code entitled to assume it is.
            var name = new string('x', 63) + "\uD83D\uDE00" + new string('y', 5000);
            var server = new ProbeServer { Body = Capability(SdcCompatibility.MinimumSdcVersion, softwareName: name) };

            var result = await Probe(server);

            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome);
            Assert.IsTrue(result.Detail.Length < 600, $"Detail was {result.Detail.Length} chars.");
            foreach (var c in result.Detail)
                Assert.IsFalse(char.IsSurrogate(c) && !char.IsHighSurrogate(c), "A lone low surrogate escaped truncation.");
            Assert.IsFalse(char.IsHighSurrogate(result.Detail[result.Detail.Length - 1]));
        }

        [TestMethod]
        public async Task MidFlightCallerCancellation_Propagates()
        {
            // The pre-cancelled case short-circuits before a request is issued; this is the one
            // where the catch-filter ordering actually decides the outcome, because the caller's
            // token and the probe's own deadline are linked and both read as cancelled.
            var server = new ProbeServer { Delay = TimeSpan.FromMilliseconds(SdcServerVersionProbe.TimeoutMilliseconds * 4) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            try
            {
                await SdcServerVersionProbe.CheckAsync(SdcBase, new HttpClient(server), cts.Token);
                Assert.Fail("A cancelled caller must not come back as a fail-open result.");
            }
            catch (OperationCanceledException)
            {
            }
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
        public async Task TheHostCanDisableTheCheck_AndNothingIsProbed()
        {
            // Break-glass. The fail-closed arm is triggered by a string another team writes, in a
            // binary that cannot be patched — so if software.version ever stops meaning "the SDC
            // server's application version", a value that still matches the grammar could refuse
            // every form launch everywhere at once. This is the flag that unblocks a site without
            // an EHR release. It also means no request is issued at all.
            var server = new ProbeServer { Body = Capability(OneBelowTheMinimum()) };
            SdcCompatibility.RefuseUnsupportedServers = false;
            try
            {
                var result = await Probe(server);

                Assert.AreEqual(SdcVersionCheckOutcome.Unknown, result.Outcome,
                    "A disabled check must report unknown, which is the outcome that fails open.");
                Assert.AreEqual(0, server.RequestedUris.Count, "A disabled check must not probe.");
                StringAssert.Contains(result.Detail, "disabled by the host");
            }
            finally
            {
                SdcCompatibility.RefuseUnsupportedServers = true;
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

        /// <summary>
        /// Blocks in <c>ReadAsync</c> until disposed — a server that sends its headers and then
        /// stalls. Only disposal releases it, which is exactly what the probe's deadline does.
        /// </summary>
        private sealed class BlockingStream : Stream
        {
            private readonly SemaphoreSlim _released = new SemaphoreSlim(0, 1);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // Deliberately ignores the token: on net48 this overload's base implementation
                // does too, which is the whole reason the probe disposes the response instead.
                await _released.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                throw new ObjectDisposedException(nameof(BlockingStream));
            }

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && _released.CurrentCount == 0) { try { _released.Release(); } catch { } }
                base.Dispose(disposing);
            }
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
