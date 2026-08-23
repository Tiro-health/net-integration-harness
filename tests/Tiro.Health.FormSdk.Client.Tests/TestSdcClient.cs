using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Tiro.Health.FormSdk.Client.Fhir.R5;
// Hl7.Fhir.Model also defines a `Task` resource; disambiguate the async return type.
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormSdk.Client.Tests
{
    [TestClass]
    public sealed class TestSdcClient
    {
        private static readonly Uri BaseAddress = new("https://sdc.test.local/fhir/r5");

        // Same FHIR serializer config the client uses, for building/parsing canned payloads in tests.
        private static readonly JsonSerializerOptions FhirJson =
            new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Recoverable);

        private static QuestionnaireResponse SampleResponse() => new()
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "http://example.org/Questionnaire/intake|1.0.0",
        };

        private static (SdcClient client, FakeHttpMessageHandler handler) ClientReturning(HttpStatusCode status, string json)
        {
            var handler = new FakeHttpMessageHandler(status, json);
            var client = new SdcClient(BaseAddress, new HttpClient(handler));
            return (client, handler);
        }

        [TestMethod]
        public async Task ValidateAsync_PostsBareQuestionnaireResponse_AndReturnsOutcome()
        {
            const string outcomeJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"information","code":"informational","diagnostics":"valid"}]}""";
            var (client, handler) = ClientReturning(HttpStatusCode.OK, outcomeJson);

            var outcome = await client.ValidateAsync(SampleResponse());

            // Result is a typed OperationOutcome.
            Assert.IsNotNull(outcome);
            Assert.AreEqual(OperationOutcome.IssueSeverity.Information, outcome.Issue[0].Severity);

            // Request shape: POST to the right path, fhir+json.
            Assert.AreEqual(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.IsTrue(handler.LastRequest!.RequestUri!.AbsolutePath.EndsWith("/fhir/r5/QuestionnaireResponse/$validate", StringComparison.Ordinal),
                $"unexpected path: {handler.LastRequest!.RequestUri!.AbsolutePath}");
            Assert.AreEqual("application/fhir+json", handler.LastContentType);

            // The body is a BARE QuestionnaireResponse — not a Parameters envelope (guards the design decision).
            var sentBody = JsonSerializer.Deserialize<Resource>(handler.LastRequestBody!, FhirJson);
            Assert.IsInstanceOfType(sentBody, typeof(QuestionnaireResponse));
        }

        [TestMethod]
        public async Task ValidateAsync_WithErrorIssues_ReturnsOutcomeWithoutThrowing()
        {
            const string outcomeJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"required","diagnostics":"missing answer"}]}""";
            var (client, _) = ClientReturning(HttpStatusCode.OK, outcomeJson);

            var outcome = await client.ValidateAsync(SampleResponse());

            // A validation failure is data, not an exception.
            Assert.AreEqual(OperationOutcome.IssueSeverity.Error, outcome.Issue[0].Severity);
        }

        [TestMethod]
        public async Task ExtractAsync_PostsToExtract_AndReturnsBundle()
        {
            const string bundleJson = """{"resourceType":"Bundle","type":"transaction"}""";
            var (client, handler) = ClientReturning(HttpStatusCode.OK, bundleJson);

            var bundle = await client.ExtractAsync(SampleResponse());

            Assert.IsNotNull(bundle);
            Assert.AreEqual(Bundle.BundleType.Transaction, bundle.Type);
            Assert.IsTrue(handler.LastRequest!.RequestUri!.AbsolutePath.EndsWith("/QuestionnaireResponse/$extract", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task ValidateAsync_RecoverableBody_ReturnsPartialResult_WithoutThrowing()
        {
            // A 200 whose OperationOutcome carries an element this Firely version doesn't recognize
            // (as a newer server would emit). Recoverable mode should yield the partial POCO, not throw.
            const string outcomeJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"information","code":"informational","diagnostics":"ok"}],"madeUpFutureElement":"x"}""";
            var (client, _) = ClientReturning(HttpStatusCode.OK, outcomeJson);

            var outcome = await client.ValidateAsync(SampleResponse());

            Assert.IsNotNull(outcome);
            Assert.AreEqual(OperationOutcome.IssueSeverity.Information, outcome.Issue[0].Severity);
        }

        [TestMethod]
        public async Task SharedHttpClient_IsNotMutated_AndEachClientTargetsItsOwnBase()
        {
            const string outcomeJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"information","code":"informational"}]}""";
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, outcomeJson);
            var shared = new HttpClient(handler);

            // Same HttpClient instance, two different SDC bases.
            var clientA = new SdcClient(new Uri("https://a.test/fhir/r5"), shared);
            var clientB = new SdcClient(new Uri("https://b.test/fhir/r5"), shared);

            await clientA.ValidateAsync(SampleResponse());
            Assert.AreEqual("https://a.test/fhir/r5/QuestionnaireResponse/$validate", handler.LastRequest!.RequestUri!.AbsoluteUri);

            await clientB.ValidateAsync(SampleResponse());
            Assert.AreEqual("https://b.test/fhir/r5/QuestionnaireResponse/$validate", handler.LastRequest!.RequestUri!.AbsoluteUri);

            // The injected/shared client was never mutated — no first-base-wins footgun.
            Assert.IsNull(shared.BaseAddress);
        }

        [TestMethod]
        public async Task NonSuccessStatus_ThrowsSdcOperationException_CarryingOutcome()
        {
            const string errorJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"processing","diagnostics":"boom"}]}""";
            var (client, _) = ClientReturning(HttpStatusCode.BadRequest, errorJson);

            var ex = await Assert.ThrowsExceptionAsync<SdcOperationException>(() => client.ValidateAsync(SampleResponse()));

            Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
            Assert.IsNotNull(ex.Outcome);
            Assert.AreEqual(OperationOutcome.IssueSeverity.Fatal, ex.Outcome!.Issue[0].Severity);
        }

        [TestMethod]
        public async Task NonSuccessStatus_RecoverableErrorBody_StillSurfacesOutcome()
        {
            // A 4xx whose OperationOutcome carries an element this Firely version doesn't recognize
            // (a newer server). The error path must recover the partial outcome, not drop it.
            const string errorJson =
                """{"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"processing","diagnostics":"boom"}],"madeUpFutureElement":"x"}""";
            var (client, _) = ClientReturning(HttpStatusCode.BadRequest, errorJson);

            var ex = await Assert.ThrowsExceptionAsync<SdcOperationException>(() => client.ValidateAsync(SampleResponse()));

            Assert.IsNotNull(ex.Outcome, "server diagnostics from a recoverable error body must not be dropped");
            Assert.AreEqual(OperationOutcome.IssueSeverity.Fatal, ex.Outcome!.Issue[0].Severity);
            Assert.AreEqual("boom", ex.Outcome!.Issue[0].Diagnostics);
        }

        // GH-63: the SDC server aggregates this to learn which harness versions are
        // deployed, so the product token and a non-placeholder version both matter.
        [TestMethod]
        public async Task Requests_CarryTheHarnessUserAgent()
        {
            const string outcomeJson = """{"resourceType":"OperationOutcome","issue":[]}""";
            var (client, handler) = ClientReturning(HttpStatusCode.OK, outcomeJson);

            await client.ValidateAsync(SampleResponse());

            var products = handler.LastRequest!.Headers.UserAgent;
            Assert.AreEqual(1, products.Count);
            Assert.AreEqual("Tiro.Health.FormSdk.Client", products.Single().Product!.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(products.Single().Product!.Version));
            Assert.IsFalse(products.Single().Product!.Version!.Contains("+"),
                "commit-sha build metadata must be stripped from the UA version token");
        }

        [TestMethod]
        public async Task Requests_PreserveAConsumerConfiguredUserAgent()
        {
            // An injected client's own UA must survive: per-request headers replace defaults,
            // and hospital proxies may allowlist on the consumer's token.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"resourceType":"OperationOutcome","issue":[]}""");
            var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AcmeEhr", "7.3"));
            var client = new SdcClient(BaseAddress, http);

            await client.ValidateAsync(SampleResponse());

            var names = handler.LastRequest!.Headers.UserAgent.Select(p => p.Product!.Name).ToList();
            CollectionAssert.AreEqual(new[] { "AcmeEhr", "Tiro.Health.FormSdk.Client" }, names);
        }

        [TestMethod]
        public async Task Requests_PreserveAConsumerUserAgentAddedWithoutValidation()
        {
            // TryAddWithoutValidation is a common idiom precisely because it bypasses the
            // UA parser — such a value never appears in the typed UserAgent collection, so
            // copying that collection alone would silently drop it.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"resourceType":"OperationOutcome","issue":[]}""");
            var http = new HttpClient(handler);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Acme EHR v7.3 [prod]");
            var client = new SdcClient(BaseAddress, http);

            await client.ValidateAsync(SampleResponse());

            var sent = string.Join(" ", handler.LastRequest!.Headers.GetValues("User-Agent"));
            StringAssert.Contains(sent, "Acme EHR v7.3 [prod]");
            StringAssert.Contains(sent, "Tiro.Health.FormSdk.Client/");
        }

        [TestMethod]
        public async Task SharedHttpClient_IsNotMutatedByTheUserAgent()
        {
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"resourceType":"OperationOutcome","issue":[]}""");
            var http = new HttpClient(handler);
            var client = new SdcClient(BaseAddress, http);

            await client.ValidateAsync(SampleResponse());

            Assert.AreEqual(0, http.DefaultRequestHeaders.UserAgent.Count,
                "an IHttpClientFactory-managed client is shared; setting defaults would leak onto unrelated requests");
        }

        [TestMethod]
        public void Constructor_RejectsBaseAddressWithQueryOrFragment()
        {
            // A query/fragment can't survive relative-URI resolution, so it must fail fast rather
            // than be silently dropped (which would send auth/routing params nowhere).
            Assert.ThrowsException<ArgumentException>(
                () => new SdcClient(new Uri("https://sdc.test.local/fhir/r5?key=abc")));
        }
    }
}
