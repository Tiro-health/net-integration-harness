using System;
using System.Text;
using System.Text.Json;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// Cheap top-level field probes over raw JSON. Used on hot paths (message
    /// correlation, telemetry span naming) where pulling a single root-level
    /// property out of a message is all we need — without paying for a full
    /// model deserialization.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="Utf8JsonReader"/>: only inspects direct children of
    /// the root object, so nested occurrences of the same field name (inside
    /// payloads, FHIR resources, free-text answers) are ignored. Field-name
    /// matching is case-insensitive to mirror the handler's
    /// <c>PropertyNameCaseInsensitive = true</c>.
    /// </remarks>
    public static class JsonProbe
    {
        /// <summary>
        /// Returns the value of the top-level JSON string field
        /// <paramref name="fieldName"/> (case-insensitive), or null if the field
        /// is absent at the root, the JSON is malformed, or the value is not a
        /// string. The returned value is unescaped per JSON rules.
        /// </summary>
        public static string ExtractStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return null;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var reader = new Utf8JsonReader(bytes);
                if (!TryFindTopLevelProperty(ref reader, fieldName)) return null;
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns true if the root object has a top-level property named
        /// <paramref name="fieldName"/> (case-insensitive), regardless of its
        /// value type — including null. False for malformed JSON or a non-object
        /// root. Use this for routing decisions (e.g. request vs response)
        /// where the value isn't needed.
        /// </summary>
        public static bool HasTopLevelField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return false;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var reader = new Utf8JsonReader(bytes);
                return TryFindTopLevelProperty(ref reader, fieldName);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // Walks the root object's direct children. On a match, leaves the reader
        // positioned on the value token and returns true. On no match (or
        // non-object root), returns false. Nested objects/arrays are skipped via
        // Utf8JsonReader.Skip so we never visit their property names.
        private static bool TryFindTopLevelProperty(ref Utf8JsonReader reader, string fieldName)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return false;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                bool match = reader.ValueTextEquals(fieldName)
                    || string.Equals(reader.GetString(), fieldName, StringComparison.OrdinalIgnoreCase);

                if (match)
                {
                    return reader.Read();
                }

                // Advance past the value (primitive: stays on it; container: skips to End).
                reader.Skip();
            }
            return false;
        }
    }
}
