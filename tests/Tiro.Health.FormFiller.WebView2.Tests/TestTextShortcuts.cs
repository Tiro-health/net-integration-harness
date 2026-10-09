using System;
using System.Collections.Generic;
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

        /// <summary>The abbreviations in a posted message, parsed (the serializer may escape µ).</summary>
        private static string[] Abbreviations(string posted)
            => JsonDocument.Parse(posted).RootElement.GetProperty("payload").GetProperty("abbreviations")
                .EnumerateArray().Select(e => e.GetString()).ToArray();

        [TestMethod]
        public async Task TheTriggersAreSentAtLaunch_BeforeTheQuestionnaire()
        {
            _viewer.TextShortcutTriggers = new[] { "µnka", "µpat" };

            await DisplayForm();

            var index = IndexOfPosted("ui.form.configureTextShortcuts");
            Assert.IsTrue(index >= 0, "the triggers must reach the page");
            Assert.IsTrue(index < IndexOfPosted("sdc.displayQuestionnaire"), "in place before the user can type");
            CollectionAssert.AreEqual(new[] { "µnka", "µpat" }, Abbreviations(_browser.PostedMessages[index]));
        }

        [TestMethod]
        public async Task NothingIsSentWithoutTriggers()
        {
            await DisplayForm();

            Assert.AreEqual(-1, IndexOfPosted("ui.form.configureTextShortcuts"));
        }

        [TestMethod]
        public async Task AssigningTriggersOnceTheFormIsShownSendsThem()
        {
            await DisplayForm();

            _viewer.TextShortcutTriggers = new List<string> { "µnew" };
            await PollFor(() => IndexOfPosted("ui.form.configureTextShortcuts") >= 0, TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "µnew" },
                Abbreviations(_browser.PostedMessages[IndexOfPosted("ui.form.configureTextShortcuts")]));
        }

        [TestMethod]
        public void AssigningTriggersBeforeTheFormIsShownSendsNothingYet()
        {
            _viewer.TextShortcutTriggers = new[] { "µpat" };

            Assert.AreEqual(-1, IndexOfPosted("ui.form.configureTextShortcuts"));
            CollectionAssert.AreEqual(new[] { "µpat" }, _viewer.TextShortcutTriggers.ToArray());
        }

        [TestMethod]
        public async Task TelemetryGetsNoTriggerOrSnippet()
        {
            _viewer.TextShortcutTriggers = new[] { "µpat" };
            _viewer.ResolveTextShortcut = _ => Task.FromResult(new TiroSnippet("Patient Jan Peeters"));
            await DisplayForm();
            await AnswerTo("µpat");

            foreach (var message in _sink.CapturedMessages)
                Assert.IsFalse(message.Contains("Jan Peeters") || message.Contains("µpat"));
        }

        // ---- Resolved on demand -------------------------------------------------------------------

        private static string ShortcutRequest(string requestId, string abbreviation) => $@"{{
            ""messageId"": ""req-{requestId}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""ui.form.textShortcutRequested"",
            ""payload"": {{ ""requestId"": ""{requestId}"", ""abbreviation"": ""{abbreviation}"" }}
        }}";

        private async Task<JsonElement> AnswerTo(string abbreviation)
        {
            _browser.RaiseMessageReceived(ShortcutRequest("ts-1", abbreviation));
            await PollFor(() => IndexOfPosted("ui.form.resolveTextShortcut") >= 0, TimeSpan.FromSeconds(5));
            return JsonDocument.Parse(_browser.PostedMessages[IndexOfPosted("ui.form.resolveTextShortcut")])
                .RootElement.GetProperty("payload");
        }

        [TestMethod]
        public async Task TheResolverAnswersTheRequest()
        {
            string asked = null;
            _viewer.ResolveTextShortcut = async abbreviation =>
            {
                asked = abbreviation;
                await Task.Yield();
                return new TiroSnippet("Leonardo da Vinci", "<b>Leonardo</b>");
            };
            await DisplayForm();

            var answer = await AnswerTo("µpat");

            Assert.AreEqual("µpat", asked);
            Assert.AreEqual("ts-1", answer.GetProperty("requestId").GetString());
            Assert.AreEqual("Leonardo da Vinci", answer.GetProperty("text").GetString());
            Assert.AreEqual("<b>Leonardo</b>", answer.GetProperty("html").GetString());
        }

        [TestMethod]
        public async Task NoSnippetStillAnswers_SoThePageCanLetGo()
        {
            _viewer.ResolveTextShortcut = _ => Task.FromResult<TiroSnippet>(null);
            await DisplayForm();

            var answer = await AnswerTo("µunknown");

            Assert.IsFalse(answer.TryGetProperty("text", out _));
        }

        [TestMethod]
        public async Task AThrowingResolverIsReportedAndAnswersWithNothing()
        {
            _viewer.ResolveTextShortcut = _ => throw new InvalidOperationException("database down");
            await DisplayForm();

            var answer = await AnswerTo("µpat");

            Assert.IsFalse(answer.TryGetProperty("text", out _));
            Assert.AreEqual(1, _sink.CapturedExceptions.Count);
        }

        [TestMethod]
        public void ASnippetNeedsATextRendition()
        {
            Assert.ThrowsException<ArgumentException>(() => new TiroSnippet(""));
            Assert.IsNull(new TiroSnippet("x", "").Html, "empty HTML means plain text");
        }

        [TestMethod]
        public void FromRtfProducesBothRenditions_AndPassesNothingThrough()
        {
            var snippet = TiroSnippet.FromRtf(@"{\rtf1\ansi {\b Bold} text\par}");

            StringAssert.Contains(snippet.Text, "Bold text");
            StringAssert.Contains(snippet.Html, "<b>Bold</b>");
            Assert.IsNull(TiroSnippet.FromRtf(null), "a lookup that found nothing passes straight through");
        }
    }
}
