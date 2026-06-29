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
        }

        [TestMethod]
        public void Ctor_CarriesInjectedHttpClient()
        {
            var http = new HttpClient();
            var conn = new SdcConnection(BaseAddress, http);
            Assert.AreEqual(BaseAddress, conn.BaseAddress);
            Assert.AreSame(http, conn.HttpClient);
        }

        [TestMethod]
        public async Task SdcClient_FromConnection_UsesInjectedHttpClient()
        {
            const string emptyBundle = """{"resourceType":"Bundle","type":"transaction"}""";
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, emptyBundle);
            var connection = new SdcConnection(BaseAddress, new HttpClient(handler));

            using (var client = new SdcClient(connection))
            {
                var bundle = await client.ExtractAsync(SampleResponse());
                Assert.IsNotNull(bundle);
            }

            // The connection's HttpClient was used: the request reached our fake handler at the
            // resolved $extract path.
            Assert.IsNotNull(handler.LastRequest);
            Assert.IsTrue(handler.LastRequest!.RequestUri!.AbsolutePath.EndsWith(
                "/fhir/r5/QuestionnaireResponse/$extract", StringComparison.Ordinal));
        }

        [TestMethod]
        public void SdcClient_FromNullConnection_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new SdcClient((SdcConnection)null!));
        }
    }
}
