using System.Text.Json;

namespace Tiro.Health.FormFiller.WebView2
{
    internal static class JsonElementExtensions
    {
        /// <summary>The named property as a string, or null when absent or not a string.</summary>
        internal static string GetStringOrNull(this JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
