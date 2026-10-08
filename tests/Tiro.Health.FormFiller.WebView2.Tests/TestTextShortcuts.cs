using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <c>TextShortcuts</c>: the list reaches the page at launch and on update. Expansion itself
    /// is the page's — see tests/bridge/text-shortcuts.test.mjs.
    /// </summary>
    [TestClass]
    public class TestTextShortcuts
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

        private int IndexOfPosted(string messageType)
            => _browser.PostedMessages.FindIndex(m => m.Contains($"\"messageType\":\"{messageType}\""));

        /// <summary>The shortcuts in a posted message, parsed (the serializer escapes µ and &lt;).</summary>
        private static JsonElement[] ShortcutsIn(string posted)
            => JsonDocument.Parse(posted).RootElement.GetProperty("payload").GetProperty("shortcuts")
                .EnumerateArray().ToArray();

        [TestMethod]
        public async Task TheListIsSentAtLaunch_BeforeTheQuestionnaire()
        {
            _viewer.TextShortcuts.Add(new TiroTextShortcut("µnka", "No known drug allergies."));
            _viewer.TextShortcuts.Add(new TiroTextShortcut("µfu", "Follow-up.", "<p>Follow-up.</p>"));

            await DisplayForm();

            var shortcuts = IndexOfPosted("ui.form.configureTextShortcuts");
            Assert.IsTrue(shortcuts >= 0, "the list must reach the page");
            Assert.IsTrue(shortcuts < IndexOfPosted("sdc.displayQuestionnaire"),
                "in place before the user can type");
            var sent = ShortcutsIn(_browser.PostedMessages[shortcuts]);
            Assert.AreEqual("µnka", sent[0].GetProperty("abbreviation").GetString());
            Assert.AreEqual("No known drug allergies.", sent[0].GetProperty("text").GetString());
            Assert.AreEqual("<p>Follow-up.</p>", sent[1].GetProperty("html").GetString());
        }

        [TestMethod]
        public async Task NothingIsSentWithoutShortcuts()
        {
            await DisplayForm();

            Assert.AreEqual(-1, IndexOfPosted("ui.form.configureTextShortcuts"));
        }

        [TestMethod]
        public async Task UpdateResendsTheCurrentList()
        {
            await DisplayForm();
            _viewer.TextShortcuts.Add(new TiroTextShortcut("µnew", "New snippet."));

            await _viewer.UpdateTextShortcutsAsync().Within5s();

            var sent = ShortcutsIn(_browser.PostedMessages[IndexOfPosted("ui.form.configureTextShortcuts")]);
            Assert.AreEqual("µnew", sent.Single().GetProperty("abbreviation").GetString());
        }

        [TestMethod]
        public async Task UpdateBeforeAFormIsDisplayedThrows()
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _viewer.UpdateTextShortcutsAsync());
        }

        [TestMethod]
        public async Task TelemetryGetsTheCountNotTheContent()
        {
            _viewer.TextShortcuts.Add(new TiroTextShortcut("µpat", "Patient Jan Peeters"));

            await DisplayForm();

            foreach (var message in _sink.CapturedMessages)
                Assert.IsFalse(message.Contains("Jan Peeters") || message.Contains("µpat"));
        }

        [TestMethod]
        public void AnAbbreviationNeedsNoWhitespaceAndATextRendition()
        {
            Assert.ThrowsException<ArgumentException>(() => new TiroTextShortcut("", "x"));
            Assert.ThrowsException<ArgumentException>(() => new TiroTextShortcut("µ nka", "x"));
            Assert.ThrowsException<ArgumentException>(() => new TiroTextShortcut("µnka", ""));
            Assert.IsNull(new TiroTextShortcut("µnka", "x", "").Html, "empty HTML means plain text");
        }

        [TestMethod]
        public void FromRtfProducesBothRenditions()
        {
            var shortcut = TiroTextShortcut.FromRtf("µb", @"{\rtf1\ansi {\b Bold} text\par}");

            StringAssert.Contains(shortcut.Text, "Bold text");
            StringAssert.Contains(shortcut.Html, "<b>Bold</b>");
        }
    }
}
