using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using Tiro.Health.SmartWebMessaging;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <c>InsertContentAsync</c>, which a host's own menu items call, and
    /// <see cref="TiroRtf.ToPlainText"/>, promoted out of the Extract sample because every
    /// consumer would otherwise copy it and leak an undisposed <c>RichTextBox</c> (a Win32
    /// handle) on every click.
    /// </summary>
    [TestClass]
    public class TestInsertContent
    {
        private FakeEmbeddedBrowser _browser = null!;
        private FakeTelemetrySink _sink = null!;
        private R5.SmartMessageHandler _handler = null!;
        private TestableTiroFormViewer _viewer = null!;

        [TestInitialize]
        public void Init()
        {
            _browser = new FakeEmbeddedBrowser();
            _sink = new FakeTelemetrySink();
            _handler = new R5.SmartMessageHandler();
            _viewer = new TestableTiroFormViewer(_browser, _handler, _sink);
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { _viewer.Dispose(); } catch { /* not under test */ }
        }

        private async Task DisplayForm()
        {
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var setContext = _viewer.SetContextAsync("http://example.org/q");
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();
        }

        /// <summary>The bridge's ack for an insert, carrying its outcome as extension fields.</summary>
        private static string AckTo(string requestMessageId, bool inserted, string mode) => $@"{{
            ""messageId"": ""resp-{requestMessageId}"",
            ""responseToMessageId"": ""{requestMessageId}"",
            ""additionalResponsesExpected"": false,
            ""payload"": {{
                ""$type"": ""base"",
                ""inserted"": {(inserted ? "true" : "false")},
                ""mode"": ""{mode}""
            }}
        }}";

        /// <summary>Starts an insert and answers it the way the page would.</summary>
        private async Task<TextInsertResult> InsertAnsweredWith(bool inserted, string mode)
        {
            var insert = _viewer.InsertContentAsync("text", "<p>text</p>");
            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("\"messageType\":\"ui.form.insertContent\"")),
                TimeSpan.FromSeconds(5));
            var posted = _browser.PostedMessages.FindLast(m => m.Contains("ui.form.insertContent"));
            _browser.RaiseMessageReceived(
                AckTo(JsonProbe.ExtractStringField(posted, "messageId"), inserted, mode));
            await insert.Within5s();
            return await insert;
        }

        [TestMethod]
        public async Task AFormattedInsertReportsHtmlMode()
        {
            await DisplayForm();

            var result = await InsertAnsweredWith(inserted: true, mode: "html");

            Assert.IsTrue(result.Inserted);
            Assert.IsTrue(result.KeptFormatting, "mode=html must surface as KeptFormatting");
            Assert.AreEqual(TextInsertMode.Html, result.Mode);
        }

        [TestMethod]
        public async Task APlainInsertReportsTextMode_SoTheHostCanTellTheDifference()
        {
            // The distinction that matters: the field took the content but not the formatting,
            // which says that field cannot hold it — no better conversion would change that.
            await DisplayForm();

            var result = await InsertAnsweredWith(inserted: true, mode: "text");

            Assert.IsTrue(result.Inserted);
            Assert.IsFalse(result.KeptFormatting);
            Assert.AreEqual(TextInsertMode.Text, result.Mode);
        }

        [TestMethod]
        public async Task NothingFocusedReportsNotInserted()
        {
            await DisplayForm();

            var result = await InsertAnsweredWith(inserted: false, mode: "none");

            Assert.IsFalse(result.Inserted);
        }

        [TestMethod]
        public void RtfBecomesPlainText()
        {
            // WinForms contains an RTF parser, so this direction needs no library. Runs on the
            // Windows CI agent; it needs a real Win32 handle.
            var rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22"
                      + @"{\b Assessment.} Findings consistent with the clinical picture; "
                      + @"{\i no further imaging indicated}.\par}";

            var text = TiroRtf.ToPlainText(rtf);

            StringAssert.Contains(text, "Assessment.");
            StringAssert.Contains(text, "no further imaging indicated");
            Assert.IsFalse(text.Contains(@"\rtf1"), "the markup must not survive");
            Assert.IsFalse(text.Contains(@"\b"), "nor the formatting control words");
        }

        [TestMethod]
        public void RtfPlainTextHandlesNothingToConvert()
        {
            Assert.AreEqual(string.Empty, TiroRtf.ToPlainText(null));
            Assert.AreEqual(string.Empty, TiroRtf.ToPlainText(""));
        }

        [TestMethod]
        public void RtfPlainTextKeepsNonAsciiIntact()
        {
            // \'hh is a byte in the declared codepage and \uN a Unicode codepoint with a
            // fallback character to skip. Getting either wrong is how accents in a clinical
            // note turn to mojibake, so it is worth pinning that the parser handles them.
            var rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22"
                      + @"Temp\'e9rature 37,8 \'b0C, 5 \u181?mol/L\par}";

            var text = TiroRtf.ToPlainText(rtf);

            StringAssert.Contains(text, "Température");
            StringAssert.Contains(text, "°C");
            StringAssert.Contains(text, "µmol/L");
        }
    }
}
