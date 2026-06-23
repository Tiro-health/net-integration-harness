using System.Net;
using System.Net.Http;
using System.Text;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// Test double that records the outgoing request and returns a canned FHIR JSON response,
    /// letting us exercise the client's serialize/POST/parse path with no real server.
    /// </summary>
    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseJson;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastContentType { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode status, string responseJson)
        {
            _status = status;
            _responseJson = responseJson;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            // No ct overload of ReadAsStringAsync on net48; the body is tiny canned JSON anyway.
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/fhir+json"),
            };
        }
    }
}
