using System;
using System.Collections.Generic;
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
    /// <c>AddInsertItem</c> and <see cref="TiroRtf.ToPlainText"/> — the two helpers promoted out
    /// of the Extract sample, because every consumer would otherwise copy them and inherit the
    /// two traps they exist to hide: a missing visibility test (an item that appears over a
    /// checkbox and silently does nothing) and an undisposed <c>RichTextBox</c> (a Win32 handle
    /// leaked on every click).
    /// </summary>
    [TestClass]
    public class TestInsertItemHelpers
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

        private async Task Initialized()
            => await PollFor(() => _browser.ContextMenuItemsProvider != null, TimeSpan.FromSeconds(5));

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

        [TestMethod]
        public async Task TheItemIsAddedWithItsLabel_AndReturnedForFurtherTweaking()
        {
            await Initialized();

            var item = _viewer.AddInsertItem("Insert conclusion", () => "text");

            Assert.AreEqual(1, _viewer.ContextMenuItems.Count);
            Assert.AreSame(item, _viewer.ContextMenuItems[0]);
            Assert.AreEqual("Insert conclusion", item.Label);
        }

        [TestMethod]
        public async Task VisibilityDefaultsToEditableTargetsOnly()
        {
            // The trap this helper exists for. Without it the item shows over a checkbox or a
            // read-only score, where there is no caret to insert at, and does nothing.
            await Initialized();
            _viewer.AddInsertItem("Insert conclusion", () => "text");

            Assert.AreEqual(1, _browser.RequestContextMenu(isEditable: true).Count);
            Assert.AreEqual(0, _browser.RequestContextMenu(isEditable: false).Count);
        }

        [TestMethod]
        public async Task TheDefaultVisibilityCanBeReplaced()
        {
            // Returning the item is what makes that possible — some hosts will want an item
            // everywhere, or gated on something of their own.
            await Initialized();
            var item = _viewer.AddInsertItem("Insert conclusion", () => "text");

            item.IsVisible = _ => true;

            Assert.AreEqual(1, _browser.RequestContextMenu(isEditable: false).Count);
        }

        [TestMethod]
        public async Task ProvidersRunAtClickTime_NotWhenTheItemIsAdded()
        {
            // The reason both are Func: an EHR's conclusion changes while the form is open, and
            // a snapshot taken at menu-build time would insert something stale.
            await Initialized();
            var textCalls = 0;
            _viewer.AddInsertItem("Insert conclusion", () => { textCalls++; return "text"; });

            _browser.RequestContextMenu();
            Assert.AreEqual(0, textCalls, "building the menu must not resolve the content");
        }

        [TestMethod]
        public async Task OnResultReceivesWhatThePageReported()
        {
            await DisplayForm();
            var results = new List<TextInsertResult>();
            _viewer.AddInsertItem("Insert conclusion", () => "text", () => "<p>text</p>",
                onResult: r => results.Add(r));

            _browser.RequestContextMenu()[0].Invoke();
            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("\"messageType\":\"ui.form.insertContent\"")),
                TimeSpan.FromSeconds(5));
            var posted = _browser.PostedMessages.FindLast(m => m.Contains("ui.form.insertContent"));
            _browser.RaiseMessageReceived(
                AckTo(JsonProbe.ExtractStringField(posted, "messageId"), inserted: true, mode: "html"));

            await PollFor(() => results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.IsTrue(results[0].Inserted);
            Assert.IsTrue(results[0].KeptFormatting, "mode=html must surface as KeptFormatting");
            Assert.AreEqual(TextInsertMode.Html, results[0].Mode);
        }

        [TestMethod]
        public async Task APlainInsertReportsTextMode_SoTheHostCanTellTheDifference()
        {
            // The distinction that matters: the field took the content but not the formatting,
            // which says that field cannot hold it — no better conversion would change that.
            await DisplayForm();
            var results = new List<TextInsertResult>();
            _viewer.AddInsertItem("Insert conclusion", () => "text", () => "<p>text</p>",
                onResult: r => results.Add(r));

            _browser.RequestContextMenu()[0].Invoke();
            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("ui.form.insertContent")),
                TimeSpan.FromSeconds(5));
            var posted = _browser.PostedMessages.FindLast(m => m.Contains("ui.form.insertContent"));
            _browser.RaiseMessageReceived(
                AckTo(JsonProbe.ExtractStringField(posted, "messageId"), inserted: true, mode: "text"));

            await PollFor(() => results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.IsFalse(results[0].KeptFormatting);
            Assert.AreEqual(TextInsertMode.Text, results[0].Mode);
        }

        [TestMethod]
        public async Task OmittingOnResultInsertsSilently()
        {
            // Most callers don't care, and a required callback would push every one of them
            // into writing an empty lambda.
            await DisplayForm();
            _viewer.AddInsertItem("Insert conclusion", () => "text");

            _browser.RequestContextMenu()[0].Invoke();

            await PollFor(
                () => _browser.PostedMessages.Exists(m => m.Contains("ui.form.insertContent")),
                TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, _sink.CapturedExceptions.Count, "a missing callback is not a failure");
        }

        [TestMethod]
        public async Task TheContentProviderIsRequired()
        {
            await Initialized();

            Assert.ThrowsException<ArgumentNullException>(
                () => _viewer.AddInsertItem("Insert conclusion", null));
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
