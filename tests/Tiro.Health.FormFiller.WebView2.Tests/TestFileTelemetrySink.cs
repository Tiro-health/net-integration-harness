using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.FormFiller.WebView2.Tests.Fakes;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <see cref="FileTelemetrySink"/> — the local JSONL transcript for deployments where the
    /// hospital network will not let Sentry out.
    /// <para>
    /// Two things are being pinned, and they pull in different directions. One is the
    /// <b>decorator contract</b>: every call reaches the inner sink, no inner failure escapes,
    /// and the two single-valued session members pass through rather than being re-decided.
    /// The other is the <b>file's readability</b>, which is a real requirement rather than a
    /// nicety — the reader is hospital IT in Notepad, or a model asked to explain a session, so
    /// self-contained lines, names instead of enum integers, and no DSN are load-bearing.
    /// </para>
    /// <para>
    /// Several tests assert on a span that is deliberately never finished. That is not an
    /// oversight in the test: a wedged viewer is the failure this file exists to explain, and
    /// anything the sink defers to <c>Finish</c> is precisely what such a session loses.
    /// </para>
    /// <para>
    /// The transcript is one rolling file per day shared by every sink in the process, so these
    /// tests give each case its own directory and reset the process-wide log in cleanup.
    /// <see cref="TestRollingTelemetryLog"/> covers the rolling and retention mechanics.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestFileTelemetrySink
    {
        private string _dir;

        [TestInitialize]
        public void CreateTempDirectory()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tiro-telemetry-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void RemoveTempDirectory()
        {
            // The log is process-wide and reference-counted; a test that leaves a sink undisposed
            // would otherwise hold a handle into the next one.
            RollingTelemetryLog.ResetForTests();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }

        // -----------------------------------------------------------------------------
        // The file itself
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void OneFilePerDay_NamedForTheDay_WithAFileHeaderFirst()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                sink.BeginSession("11112222-3333-4444-5555-666677778888").Dispose();
                sink.BeginSession("aaaabbbb-cccc-dddd-eeee-ffff00001111").Dispose();
            }

            var file = Directory.GetFiles(_dir, "*.jsonl").Single();
            StringAssert.Matches(Path.GetFileName(file), new Regex(@"^\d{8}\.jsonl$"),
                "the ordinary case — one process, one day — should be one obviously-named file, with no suffix to explain");

            var first = Records(file).First();
            Assert.AreEqual("header", Type(first), "file-level metadata comes first so a reader knows what the file is before reading it");
            Assert.AreEqual(1, first.GetProperty("v").GetInt32(), "schema version, for a reader meeting the format later");
            Assert.IsTrue(first.TryGetProperty("pid", out _), "which process wrote it — the thing you need when two are running");
            Assert.AreEqual(2, Records(file).Count(r => Type(r) == "session.start"),
                "both sessions belong to the day's file; they are delimited by records, not by separate files");
        }

        [TestMethod]
        public void SinksSharingADirectoryShareTheTranscript()
        {
            using (var first = new FileTelemetrySink(_dir))
            using (var second = new FileTelemetrySink(_dir))
            {
                first.BeginSession(NewSessionId()).Dispose();
                second.BeginSession(NewSessionId()).Dispose();
            }

            Assert.AreEqual(1, Directory.GetFiles(_dir, "*.jsonl").Length,
                "two viewers open at once is the common case; a handle each would mean all but the first silently losing its transcript");
            Assert.AreEqual(2, AllRecords().Count(r => Type(r) == "session.start"));
        }

        [TestMethod]
        public void TheFirstSinkToCloseDoesNotTakeTheTranscriptFromTheOthers()
        {
            using (var longLived = new FileTelemetrySink(_dir))
            {
                using (var shortLived = new FileTelemetrySink(_dir))
                    shortLived.BeginSession(NewSessionId()).Dispose();

                var session = longLived.BeginSession(NewSessionId());
                session.AddBreadcrumb("lifecycle", "still writing after the other sink closed");
                session.Dispose();
            }

            Assert.IsTrue(AllRecords().Any(r => Type(r) == "crumb" &&
                r.GetProperty("msg").GetString().Contains("still writing")),
                "the log is reference-counted: the last sink out closes it, not the first");
        }

        [TestMethod]
        public void SessionStartCarriesTheFullSessionIdSoTheFilePairsWithASentryEvent()
        {
            const string sessionId = "504ccc66-bcae-4dc5-83b6-fc4f7d98e1c2";

            using (var sink = new FileTelemetrySink(_dir))
                sink.BeginSession(sessionId).Dispose();

            var records = AllRecords();
            Assert.AreEqual(sessionId, records.Single(r => Type(r) == "session.start").GetProperty("session").GetString(),
                "form.session.id in full, once per session — it is the key a Sentry event is found by");
            Assert.IsTrue(records.Where(r => Type(r) != "header").All(r => r.GetProperty("sid").GetString() == "504ccc66"),
                "and the short form everywhere else, which is what a grep for one session out of a day matches");
        }

        [TestMethod]
        public void StackTracesAreNotHtmlEscaped()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("page-error", "frame at <Main>$ failed");
                session.Dispose();
            }

            var text = File.ReadAllText(Directory.GetFiles(_dir, "*.jsonl").Single());
            Assert.IsFalse(text.Contains("\\u003C"),
                "the default encoder escapes < and > so output is safe for HTML, which nothing here is, at the cost of the readability this format is for");
            StringAssert.Contains(AllRecords().Single(r => Type(r) == "crumb").GetProperty("msg").GetString(), "<Main>$");
        }

        [TestMethod]
        public void SessionEndMarksACompletedSession()
        {
            using (var sink = new FileTelemetrySink(_dir))
                sink.BeginSession(NewSessionId()).Dispose();

            Assert.AreEqual("session.end", Type(AllRecords().Last()),
                "the terminator is the only way a reader can tell a session that ended from one cut short by a process that died");
        }

        [TestMethod]
        public void SpanRecordsCarryTheTreeAndAPrecomputedDuration()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var parent = session.StartTransaction("Initialize WebView", "swm.lifecycle.init");
                var child = parent.StartChild("swm.handshake", "waiting for handshake");
                child.Finish(TelemetrySpanStatus.Ok);
                parent.Finish(TelemetrySpanStatus.Ok);
                session.Dispose();
            }

            var starts = AllRecords().Where(r => Type(r) == "span.start").ToList();
            Assert.AreEqual(2, starts.Count);

            var root = starts.Single(r => r.GetProperty("parent").ValueKind == JsonValueKind.Null);
            var nested = starts.Single(r => r.GetProperty("parent").ValueKind == JsonValueKind.String);
            Assert.AreEqual(root.GetProperty("span").GetString(), nested.GetProperty("parent").GetString(),
                "parent ids are what let the tree be rebuilt without holding state across the file");

            foreach (var end in AllRecords().Where(r => Type(r) == "span.end"))
                Assert.IsTrue(end.TryGetProperty("ms", out var ms) && ms.GetInt64() >= 0,
                    "ms is precomputed so nothing reading the file has to subtract two timestamps");
        }

        [TestMethod]
        public void StatusesAreWrittenAsNamesNotEnumIntegers()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.StartTransaction("a", "op").Finish(TelemetrySpanStatus.DeadlineExceeded);
                session.StartTransaction("b", "op").Finish(TelemetrySpanStatus.Ok);
                session.Dispose();
            }

            var statuses = AllRecords().Where(r => Type(r) == "span.end")
                .Select(r => r.GetProperty("status").GetString()).ToList();

            CollectionAssert.AreEquivalent(new[] { "deadline_exceeded", "ok" }, statuses,
                "Ok is the enum's zero value, so integers would make the commonest outcome indistinguishable from an unset field");
        }

        // -----------------------------------------------------------------------------
        // The wedged-viewer case: nothing may be deferred to Finish
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void AnUnfinishedSpanStillLeavesASpanStart()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.StartTransaction("sdc.displayQuestionnaire", "swm.send");
                // No Finish, no Dispose — the viewer wedged waiting for a handshake.
                session.Dispose();
            }

            var starts = AllRecords().Where(r => Type(r) == "span.start").ToList();
            Assert.AreEqual(1, starts.Count, "finish-only records would leave a log that goes quiet and never says what it was waiting on");
            Assert.AreEqual("sdc.displayQuestionnaire", starts[0].GetProperty("name").GetString());
            Assert.IsFalse(AllRecords().Any(r => Type(r) == "span.end"), "the span never finished, so nothing may claim it did");
        }

        [TestMethod]
        public void TagsOnAnUnfinishedSpanAreStillRecorded()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("sdc.displayQuestionnaire", "swm.send");
                span.SetTag("questionnaire_url", "http://tiro.health/Questionnaire/mammo");
                session.Dispose();
            }

            var tag = AllRecords().Single(r => Type(r) == "span.tag");
            Assert.AreEqual("questionnaire_url", tag.GetProperty("k").GetString(),
                "accumulating tags onto the end record would lose exactly the tags a wedged span never gets to write");
        }

        // -----------------------------------------------------------------------------
        // Caller intent vs. the inner span's outcome
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void ARepeatFinishIsRecordedAndFlagged_WhileTheInnerSpanKeepsTheFirstOutcome()
        {
            var inner = new FakeTelemetrySink();
            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("form.submitted", "swm.receive");
                span.Finish(TelemetrySpanStatus.InvalidArgument);
                span.Finish(TelemetrySpanStatus.Ok);
                session.Dispose();
            }

            var ends = AllRecords().Where(r => Type(r) == "span.end").ToList();
            Assert.AreEqual(2, ends.Count, "the transcript records what the caller asked for — the second call is the diagnostic");
            Assert.IsFalse(ends[0].TryGetProperty("repeat", out _), "the first finish is not a repeat");
            Assert.IsTrue(ends[1].GetProperty("repeat").GetBoolean(),
                "the flag is how a reader tells caller intent from the outcome the backend kept");

            Assert.AreEqual(TelemetrySpanStatus.InvalidArgument, inner.Sessions.Single().Transactions.Single().FinalStatus,
                "the file records intent; first-finish-wins still belongs to the real backend's span");
        }

        [TestMethod]
        public void DisposeAfterAnExplicitFinishAddsNoRecord()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                using (var span = session.StartTransaction("form.submitted", "swm.receive"))
                    span.Finish(TelemetrySpanStatus.InvalidArgument);
                session.Dispose();
            }

            var ends = AllRecords().Where(r => Type(r) == "span.end").ToList();
            Assert.AreEqual(1, ends.Count,
                "every span in the viewer is scope-wrapped around an explicit Finish; recording the scope exit too would put a redundant line after every span in the file");
            Assert.AreEqual("invalid_argument", ends.Single().GetProperty("status").GetString());
        }

        [TestMethod]
        public void DisposeWithoutAFinishRecordsTheScopeExit()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                using (session.StartTransaction("sdc.configure", "swm.send")) { }
                session.Dispose();
            }

            Assert.AreEqual("ok", AllRecords().Single(r => Type(r) == "span.end").GetProperty("status").GetString(),
                "ITelemetrySpan.Dispose finishes with Ok when nothing else did");
        }

        [TestMethod]
        public void FinishWithAnExceptionRecordsBothTheOutcomeAndTheStack()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.StartTransaction("sdc.displayQuestionnaire", "swm.send").Finish(Thrown("handshake timed out"));
                session.Dispose();
            }

            var end = AllRecords().Single(r => Type(r) == "span.end");
            Assert.AreEqual("internal_error", end.GetProperty("status").GetString());

            var error = AllRecords().Single(r => Type(r) == "error");
            Assert.AreEqual(end.GetProperty("span").GetString(), error.GetProperty("span").GetString(),
                "the error record links to its span, which is what connects a failure to where it happened");
            StringAssert.Contains(error.GetProperty("msg").GetString(), "handshake timed out");
            Assert.IsTrue(error.GetProperty("stack").GetString().Length > 0,
                "the stack is kept off the line a reader scans for outcomes, but it is kept");
        }

        // -----------------------------------------------------------------------------
        // The decorator contract
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void EveryCallReachesTheInnerSink()
        {
            var inner = new FakeTelemetrySink();
            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());
                session.SetTag("form.session.id", "abc");
                session.AddBreadcrumb("lifecycle", "constructed");

                var span = session.StartTransaction("form.submitted", "swm.receive");
                span.SetTag("messageType", "form.submitted");
                span.SetExtra("retry", 2);
                span.StartChild("swm.response", "awaiting ack").Finish(TelemetrySpanStatus.Ok);
                span.Finish(TelemetrySpanStatus.Ok);

                sink.CaptureException(Thrown("boom"));
                sink.CaptureMessage("SDC server version check: Unknown");
                sink.Flush(TimeSpan.FromSeconds(1));
                session.Dispose();
            }

            var innerSession = inner.Sessions.Single();
            Assert.AreEqual("abc", innerSession.Tags["form.session.id"]);
            Assert.IsTrue(innerSession.Breadcrumbs.Any(b => b.Category == "lifecycle"));

            var innerSpan = innerSession.Transactions.Single();
            Assert.AreEqual("form.submitted", innerSpan.Tags["messageType"]);
            Assert.AreEqual(2, innerSpan.Extras["retry"]);
            Assert.AreEqual(1, innerSpan.Children.Count, "a child span must be a child of the INNER span, not a second root");
            Assert.AreEqual(TelemetrySpanStatus.Ok, innerSpan.FinalStatus);

            Assert.AreEqual(1, inner.CapturedExceptions.Count);
            Assert.AreEqual(1, inner.CapturedMessages.Count);
            Assert.IsTrue(inner.Flushed);
            Assert.IsTrue(innerSession.Disposed);
            Assert.IsTrue(inner.Disposed, "the inner sink is owned by default, so the decorator disposes it");
        }

        [TestMethod]
        public void AnUnownedInnerSinkIsNotDisposed()
        {
            var shared = new FakeTelemetrySink();
            using (var sink = new FileTelemetrySink(_dir, shared, ownsInner: false))
                sink.BeginSession(NewSessionId()).Dispose();

            Assert.IsFalse(shared.Disposed,
                "one inner instance shared across viewers must survive the first viewer closing");
        }

        [TestMethod]
        public void TheTwoSingleValuedSessionMembersPassStraightThrough()
        {
            var inner = new StubInnerSink
            {
                TraceHeader = "9c1d4e0a7b2f4c8100000000000000aa-1234567890abcdef-1",
                BootstrapConfig = new Dictionary<string, string>
                {
                    ["dsn"] = "https://key@o1.ingest.de.sentry.io/2",
                    ["environment"] = "staging",
                    ["release"] = "Tiro.Health.FormFiller.WebView2@9.9.9",
                }
            };

            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());

                Assert.AreEqual(inner.TraceHeader, session.GetSentryTraceHeader(),
                    "a composite would have needed a precedence rule here; a decorator has one answer");
                Assert.AreSame(inner.BootstrapConfig, session.GetEmbeddedBootstrapConfig(),
                    "the embedded page must still get the inner sink's DSN, unaltered");

                session.Dispose();
            }

            var header = AllRecords().Single(r => Type(r) == "session.start");
            Assert.AreEqual("9c1d4e0a7b2f4c8100000000000000aa", header.GetProperty("trace").GetString(),
                "recording the inner trace id makes the file and the Sentry trace the same trace, not two things correlated after the fact");
            Assert.AreEqual("staging", header.GetProperty("env").GetString());
            Assert.AreEqual("Tiro.Health.FormFiller.WebView2@9.9.9", header.GetProperty("release").GetString());
        }

        [TestMethod]
        public void WithNoInnerSinkThereIsNoTraceAndNoPageBootstrap()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                Assert.IsNull(session.GetSentryTraceHeader());
                Assert.IsNull(session.GetEmbeddedBootstrapConfig(),
                    "null is what stops TiroFormViewer injecting window.__tiroSentryConfig — a page with no DSN must not be told to start a Sentry SDK");
                session.Dispose();
            }

            var header = AllRecords().Single(r => Type(r) == "session.start");
            Assert.IsFalse(header.TryGetProperty("trace", out _), "no inner sink, no trace to name");
            StringAssert.Contains(header.GetProperty("release").GetString(), "Tiro.Health.FormFiller.WebView2@",
                "the file-only case still names the build, the way a Sentry event would");
        }

        // -----------------------------------------------------------------------------
        // Isolation: a blocked or broken backend is the whole premise
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void AThrowingInnerSinkNeverEscapes_AndItsFailureIsRecorded()
        {
            var inner = new ThrowingInnerSink();

            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("lifecycle", "constructed");
                session.SetTag("form.session.id", "abc");

                var span = session.StartTransaction("form.submitted", "swm.receive");
                span.SetTag("messageType", "form.submitted");
                span.SetExtra("retry", 1);
                span.StartChild("swm.response", "ack").Finish(TelemetrySpanStatus.Ok);
                span.Finish(TelemetrySpanStatus.Ok);

                sink.CaptureException(Thrown("boom"));
                sink.CaptureMessage("warning");
                sink.Flush(TimeSpan.FromSeconds(1));
                session.Dispose();
            }

            var records = AllRecords();
            Assert.IsTrue(records.Any(r => Type(r) == "span.end"),
                "a dead backend must not take the local transcript with it — that is what the file is for");
            Assert.IsTrue(records.Any(r => Type(r) == "inner.error"),
                "and the backend's own failure has to land somewhere instead of vanishing");
        }

        [TestMethod]
        public void InnerFailureRecordsAreCappedSoTheyCannotBuryTheSession()
        {
            var inner = new ThrowingInnerSink();

            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());
                for (var i = 0; i < 200; i++) session.AddBreadcrumb("lifecycle", "crumb " + i);
                session.Dispose();
            }

            var innerErrors = AllRecords().Count(r => Type(r) == "inner.error");
            Assert.IsTrue(innerErrors > 0 && innerErrors <= 10,
                "a backend that throws on every call would otherwise fill the file with its own failure; got " + innerErrors);
            Assert.AreEqual(200, AllRecords().Count(r => Type(r) == "crumb"),
                "and every breadcrumb still has to be recorded");
        }

        [TestMethod]
        public void FlushGivesTheInnerSinkWhatIsLeftOfTheBudget()
        {
            var inner = new StubInnerSink();
            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                sink.BeginSession(NewSessionId()).Dispose();
                sink.Flush(TimeSpan.FromSeconds(1));
            }

            Assert.AreEqual(1, inner.FlushTimeouts.Count);
            Assert.IsTrue(inner.FlushTimeouts[0] <= TimeSpan.FromSeconds(1) && inner.FlushTimeouts[0] >= TimeSpan.Zero,
                "the file is flushed first and the remainder handed on: Sentry's flush burns its whole timeout when egress is blocked, which is this sink's whole reason to exist");
        }

        [TestMethod]
        public void AnUnwritableDirectoryDegradesToTheInnerSinkInsteadOfThrowing()
        {
            // A file where the directory should be: opening the transcript cannot succeed.
            var blocked = Path.Combine(_dir, "not-a-directory");
            File.WriteAllText(blocked, "");

            var inner = new FakeTelemetrySink();
            using (var sink = new FileTelemetrySink(Path.Combine(blocked, "telemetry"), inner))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("lifecycle", "constructed");
                session.StartTransaction("a", "op").Finish(TelemetrySpanStatus.Ok);
                sink.Flush(TimeSpan.FromSeconds(1));
                session.Dispose();
            }

            Assert.AreEqual(1, inner.Sessions.Count, "losing the file must not lose the backend as well");
            Assert.IsTrue(inner.Sessions.Single().Breadcrumbs.Any());
        }

        // -----------------------------------------------------------------------------
        // PHI
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void TheDsnIsNeverWritten()
        {
            var inner = new StubInnerSink
            {
                BootstrapConfig = new Dictionary<string, string>
                {
                    ["dsn"] = "https://sup3rsecret@o4507651309043712.ingest.de.sentry.io/4510703529820240",
                    ["environment"] = "production",
                }
            };

            using (var sink = new FileTelemetrySink(_dir, inner))
                sink.BeginSession(NewSessionId()).Dispose();

            var text = File.ReadAllText(Directory.GetFiles(_dir, "*.jsonl").Single());
            Assert.IsFalse(text.Contains("sup3rsecret"), "a DSN is a credential and has no business in a file built to be emailed");
            Assert.IsFalse(text.Contains("dsn"), "not the key either — session.start reads two fields by name rather than looping the config");
        }

        [TestMethod]
        public void SetExtraIsNeverReflectedOverAnObjectGraph()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("a", "op");
                span.SetExtra("resource", new PretendResource());
                session.Dispose();
            }

            var value = AllRecords().Single(r => Type(r) == "span.extra").GetProperty("v").GetString();
            Assert.AreEqual("a stand-in for something PHI-shaped", value,
                "SetExtra takes object; serializing the graph would put whatever a caller attached into a file whose purpose is to be sent somewhere");
        }

        [TestMethod]
        public void LongValuesAreCapped()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("page-error", new string('x', 10_000));
                session.Dispose();
            }

            var msg = AllRecords().Single(r => Type(r) == "crumb").GetProperty("msg").GetString();
            Assert.IsTrue(msg.Length < 10_000, "an unbounded value turns a readable transcript into an unreadable one");
            StringAssert.EndsWith(msg, "[trimmed]", "and says that it was cut rather than looking complete");
        }

        [TestMethod]
        public void TheUserProfilePathIsRedacted()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) Assert.Inconclusive("no user profile path on this platform");

            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("page-error", "could not read " + Path.Combine(profile, "report.json"));
                session.Dispose();
            }

            var msg = AllRecords().Single(r => Type(r) == "crumb").GetProperty("msg").GetString();
            StringAssert.Contains(msg, "%USERPROFILE%",
                "Windows account names are routinely a person's name, and this log is built to leave the hospital");
            Assert.IsFalse(msg.Contains(profile));
        }

        // -----------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------

        private static string NewSessionId() => Guid.NewGuid().ToString();

        private static string Type(JsonElement record) => record.GetProperty("type").GetString();

        private List<JsonElement> AllRecords()
            => Directory.GetFiles(_dir, "*.jsonl").SelectMany(Records).ToList();

        private static List<JsonElement> Records(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                var records = new List<JsonElement>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    // Parsing every line is itself the assertion that the format survived: a
                    // half-written record would fail here rather than somewhere subtler.
                    records.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
                return records;
            }
        }

        private static Exception Thrown(string message)
        {
            try { throw new InvalidOperationException(message); }
            catch (Exception ex) { return ex; }
        }

        private sealed class PretendResource
        {
            public string Patient { get; } = "Jane Doe";
            public string Nhs { get; } = "943-476-5919";
            public override string ToString() => "a stand-in for something PHI-shaped";
        }

        /// <summary>
        /// An inner sink whose single-valued members return exactly what a test sets, and which
        /// records the flush budget it was handed. <see cref="FakeTelemetrySink"/> covers the
        /// call-forwarding assertions; this one covers pass-through and the flush ordering.
        /// </summary>
        private sealed class StubInnerSink : ITelemetrySink
        {
            public string TraceHeader { get; set; }
            public IReadOnlyDictionary<string, string> BootstrapConfig { get; set; }
            public List<TimeSpan> FlushTimeouts { get; } = new List<TimeSpan>();

            public ITelemetrySession BeginSession(string sessionId) => new Session(this);
            public void CaptureException(Exception ex) { }
            public void CaptureMessage(string message) { }
            public void Flush(TimeSpan timeout) => FlushTimeouts.Add(timeout);
            public void Dispose() { }

            private sealed class Session : ITelemetrySession
            {
                private readonly StubInnerSink _owner;
                public Session(StubInnerSink owner) { _owner = owner; }

                public void SetTag(string key, string value) { }
                public void AddBreadcrumb(string category, string message) { }
                public ITelemetrySpan StartTransaction(string name, string operation) => new FakeTelemetrySpan(name, operation);
                public string GetSentryTraceHeader() => _owner.TraceHeader;
                public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig() => _owner.BootstrapConfig;
                public void Dispose() { }
            }
        }

        /// <summary>Throws from every member — a backend that is present but broken.</summary>
        private sealed class ThrowingInnerSink : ITelemetrySink
        {
            public ITelemetrySession BeginSession(string sessionId) => new Session();
            public void CaptureException(Exception ex) => throw new InvalidOperationException("backend down");
            public void CaptureMessage(string message) => throw new InvalidOperationException("backend down");
            public void Flush(TimeSpan timeout) => throw new InvalidOperationException("backend down");
            public void Dispose() => throw new InvalidOperationException("backend down");

            private sealed class Session : ITelemetrySession
            {
                public void SetTag(string key, string value) => throw new InvalidOperationException("backend down");
                public void AddBreadcrumb(string category, string message) => throw new InvalidOperationException("backend down");
                public ITelemetrySpan StartTransaction(string name, string operation) => throw new InvalidOperationException("backend down");
                public string GetSentryTraceHeader() => throw new InvalidOperationException("backend down");
                public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig() => throw new InvalidOperationException("backend down");
                public void Dispose() => throw new InvalidOperationException("backend down");
            }
        }
    }
}
