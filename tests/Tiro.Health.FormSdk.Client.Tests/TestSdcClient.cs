using System;
using System.Net;
using System.Net.Http;
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
    }
}
