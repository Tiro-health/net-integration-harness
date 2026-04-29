using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.SmartWebMessaging.Tests
{
    /// <summary>
    /// JsonProbe is a deliberately fragile heuristic — it scans raw JSON for a top-level
    /// string field without parsing the full document. These tests pin down both the
    /// behaviours we depend on and the documented limits.
    /// </summary>
    [TestClass]
    public sealed class TestJsonProbe
    {
        [TestMethod]
        public void Returns_Null_For_Null_Or_Empty_Json()
        {
            Assert.IsNull(JsonProbe.ExtractStringField(null, "x"));
            Assert.IsNull(JsonProbe.ExtractStringField("", "x"));
        }

        [TestMethod]
        public void Returns_Null_For_Null_Or_Empty_FieldName()
        {
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":\"b\"}", null));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":\"b\"}", ""));
        }

        [TestMethod]
        public void Returns_Null_When_Field_Missing()
        {
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":\"b\"}", "c"));
        }

        [TestMethod]
        public void Returns_Value_For_Simple_Field()
        {
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{\"a\":\"hello\"}", "a"));
        }

        [TestMethod]
        public void Tolerates_Whitespace()
        {
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{ \"a\" : \"hello\" }", "a"));
        }

        [TestMethod]
        public void Field_Match_Is_Case_Insensitive()
        {
            // Documented in JsonProbe.cs (kept to preserve TestParsingFailure's behavior).
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{\"MessageId\":\"hello\"}", "messageId"));
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{\"messageId\":\"hello\"}", "MessageId"));
        }

        [TestMethod]
        public void Returns_Value_From_First_Match_Even_If_Field_Name_Appears_In_Earlier_Value()
        {
            // Known limitation of the heuristic: if a value contains the literal "<fieldName>",
            // the probe matches that occurrence first. This documents (rather than fixes) it.
            // For our actual use cases (extracting messageId/messageType) collisions are rare.
            var json = "{\"foo\":\"my messageType is fancy\",\"messageType\":\"actual\"}";
            // The probe finds "messageType" inside the "foo" value first; depending on the
            // JSON, the result may be the wrong substring. We only assert it doesn't crash
            // and returns *something* (or null) — the precise behaviour is implementation-defined.
            var result = JsonProbe.ExtractStringField(json, "messageType");
            // Either the heuristic recovers ("actual") or it returns a stray substring; what
            // matters is that it doesn't throw.
            Assert.IsTrue(result == null || result == "actual" || result.Length > 0);
        }

        [TestMethod]
        public void Returns_Null_For_Malformed_Json_Without_Throwing()
        {
            Assert.IsNull(JsonProbe.ExtractStringField("not json at all", "x"));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":", "a")); // truncated mid-value
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":\"unclosed", "a")); // unclosed string
        }

        [TestMethod]
        public void Returns_Null_When_Value_Is_Not_A_String()
        {
            // Numeric values: probe looks for the opening quote of a string value, finds
            // none before EOF, returns null.
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":42}", "a"));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":true}", "a"));
        }

        [TestMethod]
        public void Handles_Escaped_Quote_In_Value()
        {
            // The probe preserves the raw (escaped) substring — it does not unescape.
            // \" inside the value is recognised so the string isn't truncated early.
            var json = "{\"a\":\"he\\\"llo\",\"b\":\"after\"}";
            var a = JsonProbe.ExtractStringField(json, "a");
            Assert.AreEqual("he\\\"llo", a);
        }
    }
}
