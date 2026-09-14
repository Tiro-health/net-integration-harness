using System;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;
using static Tiro.Health.FormFiller.WebView2.Tests.Fakes.SwmTest;
using R5 = Tiro.Health.SmartWebMessaging.Fhir.R5;
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// Launching with the Questionnaire itself instead of a canonical URL the SDC server
    /// resolves — for templates an EHR keeps privately, or one assembled at runtime.
    /// </summary>
    [TestClass]
    public class TestInlineQuestionnaire
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

        private static Questionnaire Inline(string id = "intake", string url = null) => new Questionnaire
        {
            Id = id,
            Url = url,
            Status = PublicationStatus.Active,
            Title = "Intake",
        };

        /// <summary>Tags on the named transaction, across every session this viewer opened.</summary>
        private System.Collections.Generic.Dictionary<string, string> SpanTags(string name)
        {
            foreach (var session in _sink.Sessions)
                foreach (var span in session.Transactions)
                    if (span.Name == name) return span.Tags;

            Assert.Fail($"No '{name}' transaction was started.");
            return null;
        }

        /// <summary>Drives the launch to completion and returns the displayQuestionnaire message.</summary>
        private async Task<string> LaunchWith(Questionnaire questionnaire)
        {
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var setContext = _viewer.SetContextAsync(questionnaire);
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();
            return _browser.PostedMessages.FindLast(m => m.Contains("sdc.displayQuestionnaire"));
        }

        [TestMethod]
        public async Task TheResourceTravelsInThePayload_NotACanonicalUrl()
        {
            var posted = await LaunchWith(Inline());

            Assert.IsNotNull(posted, "the launch must still send sdc.displayQuestionnaire");
            StringAssert.Contains(posted, "\"resourceType\":\"Questionnaire\"",
                "the questionnaire itself has to reach the page, not a reference to it");
            StringAssert.Contains(posted, "\"id\":\"intake\"");
        }

        [TestMethod]
        public async Task TheLaunchStillConfiguresAndReachesContextSet()
        {
            // The overload changes what is displayed, nothing else: sdc.configure still goes
            // first so readOnly and the endpoints reach the page, and the viewer still
            // transitions, or SendFormRequestSubmitAsync afterwards is in an invalid state.
            _viewer.SdcEndpointAddress = "https://sdc.test.local/fhir/r5";

            await LaunchWith(Inline());

            var configureIndex = _browser.PostedMessages.FindIndex(m => m.Contains("sdc.configure"));
            var displayIndex = _browser.PostedMessages.FindIndex(m => m.Contains("sdc.displayQuestionnaire"));
            Assert.IsTrue(configureIndex >= 0, "sdc.configure must still be sent");
            Assert.IsTrue(configureIndex < displayIndex, "and still before the questionnaire");
            Assert.AreEqual(TiroFormViewerState.ContextSet, _viewer.State);
        }

        [TestMethod]
        public async Task LaunchContextStillTravelsAlongside()
        {
            // The inline overload routes through a different send overload than the canonical
            // one; the patient/encounter/author shorthand must survive that.
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var patient = new Patient { Id = "pat-1" };
            var setContext = _viewer.SetContextAsync(Inline(), patient: patient);
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();

            var posted = _browser.PostedMessages.FindLast(m => m.Contains("sdc.displayQuestionnaire"));
            StringAssert.Contains(posted, "\"name\":\"patient\"", "the shorthand must still become launch context");
            StringAssert.Contains(posted, "pat-1");
        }

        [TestMethod]
        public async Task TheCanonicalOverloadIsUnchanged()
        {
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var setContext = _viewer.SetContextAsync("http://example.org/Questionnaire/intake|1.0.0");
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();

            var posted = _browser.PostedMessages.FindLast(m => m.Contains("sdc.displayQuestionnaire"));
            StringAssert.Contains(posted, "http://example.org/Questionnaire/intake|1.0.0");
            Assert.IsFalse(posted.Contains("\"resourceType\":\"Questionnaire\""),
                "a canonical launch must not start sending the resource");
        }

        [TestMethod]
        public void ANullQuestionnaireIsRejectedUpFront()
        {
            Assert.ThrowsException<ArgumentNullException>(
                () => _viewer.SetContextAsync((Resource)null));
        }

        // ---- Telemetry: identify the questionnaire, never carry it ----------------------------

        [TestMethod]
        public async Task AnInlineQuestionnaireIsTaggedByUrlWhenItHasOne()
        {
            await LaunchWith(Inline(url: "http://ehr.local/Questionnaire/private-intake"));

            var tags = SpanTags("sdc.displayQuestionnaire");
            Assert.AreEqual("http://ehr.local/Questionnaire/private-intake", tags["questionnaire_url"]);
            Assert.AreEqual("inline", tags["questionnaire_source"]);
        }

        [TestMethod]
        public async Task AnInlineQuestionnaireWithNoUrlFallsBackToItsId()
        {
            await LaunchWith(Inline(id: "runtime-assembled"));

            var tags = SpanTags("sdc.displayQuestionnaire");
            Assert.AreEqual("runtime-assembled", tags["questionnaire_url"]);
        }

        [TestMethod]
        public async Task AnAnonymousInlineQuestionnaireIsTaggedInline()
        {
            await LaunchWith(new Questionnaire { Status = PublicationStatus.Active, Title = "Untitled" });

            var tags = SpanTags("sdc.displayQuestionnaire");
            Assert.AreEqual("inline", tags["questionnaire_url"]);
        }

        [TestMethod]
        public async Task TheResourceItselfNeverReachesTelemetry()
        {
            // An inline Questionnaire can be large, and its answerOption text is clinical
            // content. Only a name for it is ever tagged.
            var questionnaire = Inline();
            questionnaire.Item.Add(new Questionnaire.ItemComponent
            {
                LinkId = "q1",
                Type = Questionnaire.QuestionnaireItemType.String,
                Text = "PATIENT-IDENTIFYING-TEXT",
            });

            await LaunchWith(questionnaire);

            foreach (var value in SpanTags("sdc.displayQuestionnaire").Values)
            {
                Assert.IsFalse(value != null && value.Contains("PATIENT-IDENTIFYING-TEXT"),
                    "questionnaire content must never land on a span tag");
                Assert.IsFalse(value != null && value.Contains("resourceType"),
                    "nor a serialized resource");
            }
        }

        [TestMethod]
        public async Task ACanonicalLaunchIsTaggedCanonical()
        {
            await PollFor(() => _handler.SendMessage != null, TimeSpan.FromSeconds(5));
            var setContext = _viewer.SetContextAsync("http://example.org/Questionnaire/intake|1.0.0");
            _browser.RaiseMessageReceived(Handshake("hs-1"));
            await setContext.Within5s();

            var tags = SpanTags("sdc.displayQuestionnaire");
            Assert.AreEqual("http://example.org/Questionnaire/intake|1.0.0", tags["questionnaire_url"]);
            Assert.AreEqual("canonical", tags["questionnaire_source"],
                "the two launch shapes have to be distinguishable in telemetry");
        }
    }
}
