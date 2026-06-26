using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Tiro.Health.FormSdk.Client.Fhir.R5;
using Tiro.Health.Telemetry;
// Hl7.Fhir.Model also defines a `Task` resource; disambiguate the async return type.
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormSdk.Client.Tests
{
    [TestClass]
    public sealed class TestSdcClientTelemetry
    {
        private static readonly Uri BaseAddress = new("https://sdc.test.local/fhir/r5");

        private static QuestionnaireResponse SampleResponse() => new()
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "http://example.org/Questionnaire/intake|1.0.0",
        };

        private const string OutcomeOk =
            """{"resourceType":"OperationOutcome","issue":[{"severity":"information","code":"informational","diagnostics":"valid"}]}""";

        private static SdcClient ClientWith(FakeTelemetrySession session, HttpStatusCode status, string json)
            => new(BaseAddress, new HttpClient(new FakeHttpMessageHandler(status, json)), session);

        [TestMethod]
        public async Task ValidateAsync_RecordsOneTransaction_TaggedAndFinishedOk()
        {
            var session = new FakeTelemetrySession();
            var client = ClientWith(session, HttpStatusCode.OK, OutcomeOk);

            await client.ValidateAsync(SampleResponse());

            Assert.AreEqual(1, session.Transactions.Count, "exactly one transaction per operation");
            var span = session.Transactions[0];
            Assert.AreEqual("sdc.validate", span.Name);
            Assert.AreEqual("http.client", span.Operation);
            Assert.AreEqual("QuestionnaireResponse/$validate", span.Tags["sdc.operation"]);
            Assert.AreEqual("POST", span.Tags["http.request.method"]);
            Assert.AreEqual("https://sdc.test.local/fhir/r5/QuestionnaireResponse/$validate", span.Tags["url.full"]);
            Assert.AreEqual("200", span.Tags["http.response.status_code"]);

            // Success path closes the span Ok via Dispose (the using-scope exit).
            Assert.IsTrue(span.Finished);
            Assert.IsTrue(span.Disposed);
            Assert.AreEqual(TelemetrySpanStatus.Ok, span.FinalStatus);
            Assert.IsNull(span.FinalException);
        }

        [TestMethod]
        public async Task ExtractAsync_NamesTransactionSdcExtract()
        {
            var session = new FakeTelemetrySession();
            var client = ClientWith(session, HttpStatusCode.OK, """{"resourceType":"Bundle","type":"transaction"}""");

            await client.ExtractAsync(SampleResponse());

            Assert.AreEqual("sdc.extract", session.Transactions[0].Name);
            Assert.AreEqual("QuestionnaireResponse/$extract", session.Transactions[0].Tags["sdc.operation"]);
        }

        [TestMethod]
        public async Task NonSuccessStatus_FinishesSpanWithException_AndTagsStatusCode()
        {
            const string errorJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"processing","diagnostics":"boom"}]}""";
            var session = new FakeTelemetrySession();
            var client = ClientWith(session, HttpStatusCode.BadRequest, errorJson);

            await Assert.ThrowsExceptionAsync<SdcOperationException>(() => client.ValidateAsync(SampleResponse()));

            var span = session.Transactions[0];
            Assert.IsTrue(span.Finished);
            Assert.AreEqual("400", span.Tags["http.response.status_code"]);
            // Recorded as an exception, not an Ok status.
            Assert.IsInstanceOfType(span.FinalException, typeof(SdcOperationException));
            Assert.IsNull(span.FinalStatus);
        }

        [TestMethod]
        public async Task UnparseableSuccessBody_FinishesSpanWithException()
        {
            // 200 with a body that isn't FHIR JSON → SdcOperationException on the parse path.
            var session = new FakeTelemetrySession();
            var client = ClientWith(session, HttpStatusCode.OK, "not json at all");

            await Assert.ThrowsExceptionAsync<SdcOperationException>(() => client.ValidateAsync(SampleResponse()));

            var span = session.Transactions[0];
            Assert.IsTrue(span.Finished);
            Assert.IsInstanceOfType(span.FinalException, typeof(SdcOperationException));
            Assert.IsNull(span.FinalStatus);
        }

        [TestMethod]
        public async Task Cancellation_FinishesSpanCancelled()
        {
            var session = new FakeTelemetrySession();
            var client = new SdcClient(BaseAddress, new HttpClient(new CancelingHandler()), session);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The exact cancellation type (OperationCanceledException vs TaskCanceledException) varies
            // by runtime; assert on the recorded span instead of the thrown type.
            try
            {
                await client.ValidateAsync(SampleResponse(), cts.Token);
                Assert.Fail("expected the operation to be cancelled");
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            var span = session.Transactions[0];
            Assert.IsTrue(span.Finished);
            Assert.AreEqual(TelemetrySpanStatus.Cancelled, span.FinalStatus);
            Assert.IsNull(span.FinalException);
        }

        [TestMethod]
        public async Task MultipleCalls_ShareOneSession_ForTraceCorrelation()
        {
            var session = new FakeTelemetrySession();
            var client = ClientWith(session, HttpStatusCode.OK, OutcomeOk);

            await client.ValidateAsync(SampleResponse());
            await client.ValidateAsync(SampleResponse());

            // Both round-trips land as transactions in the same caller-supplied session, so a backend
            // groups them under one trace.
            Assert.AreEqual(2, session.Transactions.Count);
        }

        [TestMethod]
        public async Task NoTelemetrySession_DoesNotThrow_AndStillReturnsResult()
        {
            // The default (no session) must be a complete no-op with no behavioral change.
            var client = new SdcClient(BaseAddress, new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, OutcomeOk)));

            var outcome = await client.ValidateAsync(SampleResponse());

            Assert.IsNotNull(outcome);
            Assert.AreEqual(OperationOutcome.IssueSeverity.Information, outcome.Issue[0].Severity);
        }

        /// <summary>Handler that honors cancellation so the client's cancel path is exercised.</summary>
        private sealed class CancelingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
