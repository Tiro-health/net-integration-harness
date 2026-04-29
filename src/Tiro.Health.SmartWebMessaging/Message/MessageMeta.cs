using System.Text.Json.Serialization;

namespace Tiro.Health.SmartWebMessaging.Message
{
    /// <summary>
    /// Optional transport-layer metadata attached to every SMART Web Messaging envelope.
    /// Currently carries trace-propagation info; SMART-spec consumers that don't recognise
    /// the field will ignore it (JSON forward-compat).
    /// </summary>
    public class MessageMeta
    {
        /// <summary>Sentry trace propagation context (optional).</summary>
        [JsonPropertyName("sentry")]
        public SentryTraceMeta Sentry { get; set; }
    }

    /// <summary>
    /// Sentry trace propagation: a <c>sentry-trace</c> header value (<c>"&lt;traceId&gt;-&lt;spanId&gt;-&lt;sampled&gt;"</c>)
    /// plus optional baggage. Receivers continue the trace by parsing <see cref="Trace"/>.
    /// </summary>
    public class SentryTraceMeta
    {
        /// <summary>The <c>sentry-trace</c> header value.</summary>
        [JsonPropertyName("trace")]
        public string Trace { get; set; }

        /// <summary>The <c>baggage</c> header value (optional; carries DSC for sampling decisions).</summary>
        [JsonPropertyName("baggage")]
        public string Baggage { get; set; }
    }
}
