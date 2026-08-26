using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
    /// The startup version check on the client (GH-62): the server's version is established
    /// once, before the first operation, and reported. Nothing is refused yet — see the note at
    /// the end of <c>EnsureServerVersionSupportedAsync</c> for what to add and when.
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
        public async Task TooOldServer_IsReportedAndTheOperationStillRuns()
        {
            // Reported, not refused. Enforcement and the floor live in the same assembly, so a
            // throw fielded now would protect nobody — and at the current floor (the first
            // server version that can answer the probe at all) it could only ever fire on a
            // mistake. The verdict is what a host acts on; see the note at the call site.
            var older = OneBelowTheMinimum();
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataBody = FakeHttpMessageHandler.CapabilityStatementJson(older),
            };
            var listener = new CapturingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                using var client = new SdcClient(BaseAddress, new HttpClient(handler));

                var bundle = await client.ExtractAsync(SampleResponse());

                Assert.IsNotNull(bundle, "A too-old server is a warning, not a blocked operation.");
                Assert.AreEqual(SdcVersionCheckOutcome.TooOld, client.ServerVersionCheck!.Outcome);
                Assert.AreEqual(older, client.ServerVersionCheck!.ReportedVersion);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }

            // Actionable and distinguishable from the "couldn't tell" warning: this one names
            // both versions and tells the reader to upgrade the server.
            Assert.IsTrue(listener.Messages.Exists(m => m.Contains("is older than the minimum")),
                "A too-old server must be reported. Captured: " + string.Join(" | ", listener.Messages));
        }

        [TestMethod]
        public async Task AnUnreadableVersion_FailsOpen_AndTheOperationRuns()
        {
            // A server predating the /metadata route (it answers 400), or a network blip: the
            // version is unknown, and unknown must never brick a deployment.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataStatus = HttpStatusCode.BadRequest,
            };
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            var bundle = await client.ExtractAsync(SampleResponse());

            Assert.IsNotNull(bundle);
            Assert.AreEqual(SdcVersionCheckOutcome.Unknown, client.ServerVersionCheck!.Outcome);
        }

        [TestMethod]
        public async Task AFailOpenIsTraced_SoItIsVisibleInTheCustomersOwnLogs()
        {
            // The client is deliberately telemetry-free, so Trace is the whole of "loud" here —
            // and loudness is the only thing standing between a silently disarmed check and
            // nobody ever knowing. Customers self-host the server; these are their logs.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataStatus = HttpStatusCode.BadRequest,
            };
            var listener = new CapturingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                using var client = new SdcClient(BaseAddress, new HttpClient(handler));
                await client.ExtractAsync(SampleResponse());
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }

            Assert.IsTrue(listener.Messages.Exists(m => m.Contains("could not be established")),
                "A fail-open must leave a warning behind. Captured: " + string.Join(" | ", listener.Messages));
        }

        [TestMethod]
        public async Task ACancelledFirstOperation_DoesNotLeaveTheGateUnarmed()
        {
            // The probe is started with CancellationToken.None precisely so that the first
            // caller's cancellation cannot poison the shared verdict. If it could, cancelling
            // one $extract would disarm the gate for every later operation on that client.
            var older = OneBelowTheMinimum();
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson)
            {
                MetadataBody = FakeHttpMessageHandler.CapabilityStatementJson(older),
            };
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                // Asserted, not swallowed: a caller who has already cancelled must get their own
                // cancellation back — not a 3 s wait on a probe and then a pairing verdict.
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                    () => client.ExtractAsync(SampleResponse(), cts.Token));
            }

            await client.ExtractAsync(SampleResponse());
            Assert.AreEqual(SdcVersionCheckOutcome.TooOld, client.ServerVersionCheck!.Outcome,
                "A cancelled first operation must not leave the shared verdict unset.");
        }

        [TestMethod]
        public async Task ConcurrentFirstOperations_ProbeOnce_AndShareOneVerdict()
        {
            // The reason the TASK is cached rather than the result: one verdict for everyone. A
            // previous revision published the task with a CAS, which looked like it gated the
            // start and did not — CheckAsync is an async method, so calling it issues the GET
            // and the CAS only chose which in-flight task to keep. Eight concurrent first
            // operations made eight requests, and could have disagreed with each other.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, BundleJson);
            using var client = new SdcClient(BaseAddress, new HttpClient(handler));
            using var gate = new SemaphoreSlim(0, 8);

            var operations = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await gate.WaitAsync();
                await client.ExtractAsync(SampleResponse());
            })).ToArray();
            gate.Release(8);
            await Task.WhenAll(operations);

            var probeRequests = handler.RequestedUris.Count(u => u.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal));
            Assert.AreEqual(1, probeRequests, $"Eight concurrent first operations issued {probeRequests} probes.");
        }

        private sealed class CapturingTraceListener : TraceListener
        {
            public List<string> Messages { get; } = new();
            public override void Write(string? message) { if (message is not null) Messages.Add(message); }
            public override void WriteLine(string? message) { if (message is not null) Messages.Add(message); }
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
                "The probe is cached: a second operation must not re-probe the server.");
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
        }
    }
}
