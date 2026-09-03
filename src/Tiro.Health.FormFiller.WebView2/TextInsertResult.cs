using System;
using Tiro.Health.SmartWebMessaging.Message;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// How the page fared with an <see cref="TiroFormViewer{TResource,TQR,TOO}.InsertContentAsync"/>.
    /// </summary>
    public sealed class TextInsertResult
    {
        /// <summary>Nothing reached a field.</summary>
        public static readonly TextInsertResult NotInserted = new TextInsertResult(false, TextInsertMode.None);

        private TextInsertResult(bool inserted, TextInsertMode mode)
        {
            Inserted = inserted;
            Mode = mode;
        }

        /// <summary>
        /// True when the content landed in a field. False when there was nothing to insert into
        /// — the user hasn't clicked into a field, or is standing in one that doesn't accept
        /// free text — or when the page reported a failure.
        /// </summary>
        public bool Inserted { get; }

        /// <summary>Which path the page took.</summary>
        public TextInsertMode Mode { get; }

        /// <summary>
        /// True when HTML was offered and the field took it, so the formatting survived. False
        /// after a fallback to plain text, which is the interesting case: it means that field
        /// cannot store formatting, and no better conversion would have changed the outcome.
        /// </summary>
        public bool KeptFormatting => Mode == TextInsertMode.Html;

        /// <summary>
        /// Reads the bridge's <c>inserted</c> and <c>mode</c> flags off an ack. An absent or
        /// unrecognised value reads as not inserted: "we can't show it landed" is the safe
        /// answer for a caller deciding whether to prompt the user.
        /// </summary>
        internal static TextInsertResult FromResponse(SmartMessageResponse response)
        {
            var extras = response?.Payload?.ExtraFields;
            if (extras == null) return NotInserted;

            var inserted = extras.TryGetValue("inserted", out var insertedValue)
                           && insertedValue.ValueKind == System.Text.Json.JsonValueKind.True;
            if (!inserted) return NotInserted;

            var mode = TextInsertMode.Text;
            if (extras.TryGetValue("mode", out var modeValue)
                && modeValue.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(modeValue.GetString(), "html", StringComparison.Ordinal))
            {
                mode = TextInsertMode.Html;
            }
            return new TextInsertResult(true, mode);
        }

        public override string ToString() => Inserted ? "inserted as " + Mode : "not inserted";
    }

    /// <summary>Which representation the page managed to insert.</summary>
    public enum TextInsertMode
    {
        /// <summary>Nothing was inserted.</summary>
        None = 0,

        /// <summary>Plain text — either no HTML was offered, or the field declined it.</summary>
        Text = 1,

        /// <summary>HTML, so the formatting survived.</summary>
        Html = 2,
    }
}
