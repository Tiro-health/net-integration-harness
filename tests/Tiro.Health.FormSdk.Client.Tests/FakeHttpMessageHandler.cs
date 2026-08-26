using System.Net;
using System.Net.Http;
using System.Text;
using Tiro.Health.FormSdk.Abstractions;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// Test double that records the outgoing request and returns a canned FHIR JSON response,
    /// letting us exercise the client's serialize/POST/parse path with no real server.
    /// </summary>
    /// <remarks>
    /// It also answers the two SDC version-probe routes (GH-62), because every operation now
    /// runs that check first. The defaults stand in for a supported server — <c>/metadata</c>
    /// returns a <c>CapabilityStatement</c> reporting exactly
    /// <see cref="SdcCompatibility.MinimumSdcVersion"/> — so operation tests traverse the same
    /// satisfied path a real supported server puts them on, rather than the fail-open one.
    /// <see cref="LastRequest"/> and friends deliberately ignore the probe requests, so an
    /// operation test still sees the operation.
    /// </remarks>
    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseJson;

        /// <summary>The last non-probe request — i.e. the operation under test.</summary>
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastContentType { get; private set; }

        /// <summary>Every URI asked for, probe requests included, in order.</summary>
        public List<Uri> RequestedUris { get; } = new();

        public FakeHttpMessageHandler(HttpStatusCode status, string responseJson)
        {
            _status = status;
            _responseJson = responseJson;
        }

        /// <summary>What <c>GET {base}/metadata</c> answers.</summary>
        public HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>Body for <c>GET {base}/metadata</c>.</summary>
        public string MetadataBody { get; set; } = CapabilityStatementJson(SdcCompatibility.MinimumSdcVersion);

        /// <summary>
        /// What <c>GET {origin}/openapi.json</c> answers. 404 by default: the primary route is
        /// what a supported server has, and the fallback gets its own tests.
        /// </summary>
        public HttpStatusCode OpenApiStatus { get; set; } = HttpStatusCode.NotFound;

        /// <summary>Body for <c>GET {origin}/openapi.json</c>.</summary>
        public string OpenApiBody { get; set; } = "{}";

        /// <summary>A minimal <c>CapabilityStatement</c> shaped like the real server's (~530 B).</summary>
        public static string CapabilityStatementJson(string version) =>
            $@"{{""resourceType"":""CapabilityStatement"",""status"":""active"",""kind"":""instance"",
                ""software"":{{""name"":""Tiro.health SDC Server"",""version"":""{version}""}},
                ""fhirVersion"":""5.0.0"",""format"":[""json""]}}";

        /// <summary>A minimal FastAPI-shaped <c>openapi.json</c>.</summary>
        public static string OpenApiJson(string version) =>
            $@"{{""openapi"":""3.1.0"",""info"":{{""title"":""Tiro.health SDC Server"",""version"":""{version}""}},""paths"":{{}}}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/metadata", StringComparison.Ordinal))
                return Json(MetadataStatus, MetadataBody, "application/fhir+json");
            if (request.Method == HttpMethod.Get && path == "/openapi.json")
                return Json(OpenApiStatus, OpenApiBody, "application/json");

            LastRequest = request;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            // No ct overload of ReadAsStringAsync on net48; the body is tiny canned JSON anyway.
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();

            return Json(_status, _responseJson, "application/fhir+json");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType) =>
            new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
    }
}
