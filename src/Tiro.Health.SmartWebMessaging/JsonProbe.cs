using System;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// Cheap, allocation-free scans over raw JSON without a full parse.
    /// Used on hot paths (message correlation, telemetry span naming) where
    /// pulling a single top-level string field out of a message is all we need.
    /// </summary>
    public static class JsonProbe
    {
        /// <summary>
        /// Returns the value of the first JSON string field named
        /// <paramref name="fieldName"/> (case-insensitive), or null if the field
        /// is absent, the JSON is malformed, or the value is not a string.
        /// Handles escaped quotes inside the value (<c>\"</c>).
        /// </summary>
        public static string ExtractStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return null;

            try
            {
                var searchKey = "\"" + fieldName + "\"";
                var keyIndex = json.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0) return null;

                var colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
                if (colonIndex < 0) return null;

                var startQuote = json.IndexOf('"', colonIndex + 1);
                if (startQuote < 0) return null;

                var endQuote = startQuote + 1;
                while (endQuote < json.Length)
                {
                    if (json[endQuote] == '"' && json[endQuote - 1] != '\\')
                        break;
                    endQuote++;
                }

                if (endQuote >= json.Length) return null;

                return json.Substring(startQuote + 1, endQuote - startQuote - 1);
            }
            catch
            {
                return null;
            }
        }
    }
}
