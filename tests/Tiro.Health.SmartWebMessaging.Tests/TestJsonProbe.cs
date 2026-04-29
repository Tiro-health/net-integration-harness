using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.SmartWebMessaging.Tests
{
    /// <summary>
    /// JsonProbe walks the root object's direct children with Utf8JsonReader.
    /// Tests pin down: top-level-only matching (no nested collisions), proper
    /// JSON unescaping, case-insensitive field-name matching, and null returns
    /// on malformed input.
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
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{\"MessageId\":\"hello\"}", "messageId"));
            Assert.AreEqual("hello", JsonProbe.ExtractStringField("{\"messageId\":\"hello\"}", "MessageId"));
        }

        [TestMethod]
        public void Ignores_Field_Name_Inside_String_Value()
        {
            // Critical: a value containing the literal field name must not match.
            // Routing depends on this — a form.submitted answer mentioning
            // "responseToMessageId" must not be misclassified as a response.
            var json = "{\"foo\":\"my messageType is fancy\",\"messageType\":\"actual\"}";
            Assert.AreEqual("actual", JsonProbe.ExtractStringField(json, "messageType"));
        }

        [TestMethod]
        public void Ignores_Nested_Field_With_Same_Name()
        {
            // Nested occurrences in payloads / FHIR resources must be ignored.
            var json = "{\"payload\":{\"responseToMessageId\":\"nested\"},\"messageType\":\"top\"}";
            Assert.IsNull(JsonProbe.ExtractStringField(json, "responseToMessageId"));
            Assert.AreEqual("top", JsonProbe.ExtractStringField(json, "messageType"));
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
            // ExtractStringField is type-strict: only string values are returned.
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":42}", "a"));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":true}", "a"));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":null}", "a"));
            Assert.IsNull(JsonProbe.ExtractStringField("{\"a\":{\"b\":\"c\"}}", "a"));
        }

        [TestMethod]
        public void Returns_Null_For_Non_Object_Root()
        {
            Assert.IsNull(JsonProbe.ExtractStringField("[\"a\",\"b\"]", "a"));
            Assert.IsNull(JsonProbe.ExtractStringField("\"just a string\"", "a"));
        }

        [TestMethod]
        public void Unescapes_Json_Value()
        {
            // Returned value is the proper JSON-decoded form, not the raw substring.
            var json = "{\"a\":\"he\\\"llo\",\"b\":\"after\"}";
            Assert.AreEqual("he\"llo", JsonProbe.ExtractStringField(json, "a"));
            Assert.AreEqual("after", JsonProbe.ExtractStringField(json, "b"));
        }

        [TestMethod]
        public void Unescapes_Backslash_And_Unicode()
        {
            // Trailing escaped backslash before closing quote — the classic
            // even/odd-backslash bug in substring scanners. New parser handles
            // this correctly.
            Assert.AreEqual("\\", JsonProbe.ExtractStringField("{\"a\":\"\\\\\",\"b\":\"x\"}", "a"));
            Assert.AreEqual("x", JsonProbe.ExtractStringField("{\"a\":\"\\\\\",\"b\":\"x\"}", "b"));
            Assert.AreEqual("é", JsonProbe.ExtractStringField("{\"a\":\"\\u00e9\"}", "a"));
        }

        [TestMethod]
        public void HasTopLevelField_Detects_Presence_Even_For_Null_And_NonString_Values()
        {
            Assert.IsTrue(JsonProbe.HasTopLevelField("{\"a\":\"x\"}", "a"));
            Assert.IsTrue(JsonProbe.HasTopLevelField("{\"a\":null}", "a"));
            Assert.IsTrue(JsonProbe.HasTopLevelField("{\"a\":42}", "a"));
            Assert.IsTrue(JsonProbe.HasTopLevelField("{\"a\":{\"b\":1}}", "a"));
            Assert.IsTrue(JsonProbe.HasTopLevelField("{\"A\":\"x\"}", "a")); // case-insensitive
            Assert.IsFalse(JsonProbe.HasTopLevelField("{\"a\":\"x\"}", "b"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("{\"payload\":{\"a\":\"x\"}}", "a"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("{\"a\":\"contains \\\"b\\\":\\\"y\\\"\"}", "b"));
        }

        [TestMethod]
        public void HasTopLevelField_Returns_False_For_Malformed_Or_Non_Object()
        {
            Assert.IsFalse(JsonProbe.HasTopLevelField(null, "a"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("", "a"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("not json", "a"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("[\"a\"]", "a"));
            Assert.IsFalse(JsonProbe.HasTopLevelField("{\"a\":", "a"));
        }
    }
}
