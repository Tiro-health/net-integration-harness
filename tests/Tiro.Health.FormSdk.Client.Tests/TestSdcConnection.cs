using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Tiro.Health.FormSdk.Client;
using Tiro.Health.FormSdk.Client.Fhir.R5;
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormSdk.Client.Tests
{
    [TestClass]
    public sealed class TestSdcConnection
    {
        private static readonly Uri BaseAddress = new("https://sdc.test.local/fhir/r5");

        private static QuestionnaireResponse SampleResponse() => new()
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "http://example.org/Questionnaire/intake|1.0.0",
        };

        [TestMethod]
        public void Ctor_NullBaseAddress_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new SdcConnection((Uri)null!));
            Assert.ThrowsException<ArgumentNullException>(() => new SdcConnection((string)null!));
        }

        [TestMethod]
        public void Ctor_StringOverload_ParsesAbsoluteUri()
        {
            var conn = new SdcConnection("https://sdc.test.local/fhir/r5");
            Assert.AreEqual(BaseAddress, conn.BaseAddress);
            Assert.IsNull(conn.HttpClient);
            Assert.IsNull(conn.Telemetry);
        }

        [TestMethod]
        public void WithTelemetry_ReturnsCopyCarryingSession_LeavingOriginalUnchanged()
        {
            var http = new HttpClient();
            var original = new SdcConnection(BaseAddress, http);
            var session = new FakeTelemetrySession();

            var withSession = original.WithTelemetry(session);

            // Copy carries the session and preserves the other fields.
            Assert.AreSame(session, withSession.Telemetry);
            Assert.AreEqual(BaseAddress, withSession.BaseAddress);
            Assert.AreSame(http, withSession.HttpClient);
            // Original is untouched (immutability).
            Assert.IsNull(original.Telemetry);
        }

        [TestMethod]
        public async Task SdcClient_FromConnection_UsesInjectedHttpClientAndSession()
        {
            const string emptyBundle = """{"resourceType":"Bundle","type":"transaction"}""";
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, emptyBundle);
            var session = new FakeTelemetrySession();
            var connection = new SdcConnection(BaseAddress, new HttpClient(handler), session);

            using (var client = new SdcClient(connection))
            {
                var bundle = await client.ExtractAsync(SampleResponse());
                Assert.IsNotNull(bundle);
            }

            // The connection's HttpClient was used (request reached our fake handler) ...
            Assert.IsNotNull(handler.LastRequest);
            Assert.IsTrue(handler.LastRequest!.RequestUri!.AbsolutePath.EndsWith(
                "/fhir/r5/QuestionnaireResponse/$extract", StringComparison.Ordinal));
            // ... and the connection's session recorded the operation in its trace.
            Assert.AreEqual(1, session.Transactions.Count);
            Assert.AreEqual("sdc.extract", session.Transactions[0].Name);
        }

        [TestMethod]
        public void SdcClient_FromNullConnection_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new SdcClient((SdcConnection)null!));
        }
    }
}
