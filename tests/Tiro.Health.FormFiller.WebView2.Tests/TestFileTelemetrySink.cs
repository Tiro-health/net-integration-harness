using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
                Assert.IsTrue(end.TryGetProperty("ms", out _),
                    "ms is precomputed so nothing reading the file has to subtract two timestamps");
        }

        [TestMethod]
        public void SpanDurationIsTheRealElapsedTime()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("slow", "op");
                Thread.Sleep(40);
                span.Finish(TelemetrySpanStatus.Ok);
                session.Dispose();
            }

            var ms = AllRecords().Single(r => Type(r) == "span.end").GetProperty("ms").GetInt64();
            Assert.IsTrue(ms >= 25, "a >= 0 assertion passes on a hardcoded zero; the field has to carry the actual duration. Got " + ms);
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
        public void FlushWritesTheFileBeforeHandingTheRemainderToTheInnerSink()
        {
            var inner = new StubInnerSink();
            using (var sink = new FileTelemetrySink(_dir, inner))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb("lifecycle", "before the flush");

                // Observed from inside the inner sink's own Flush: by the time the budget reaches
                // it, the transcript must already be on disk. A timeout-range assertion alone
                // passes with no arithmetic at all, and passed against an implementation that
                // simply forwarded the original budget.
                inner.OnFlush = () => inner.RecordsOnDiskAtFlush = AllRecords().Count(r => Type(r) == "crumb");

                sink.Flush(TimeSpan.FromSeconds(1));
                session.Dispose();
            }

            Assert.AreEqual(1, inner.RecordsOnDiskAtFlush,
                "cheap-first ordering is the whole reason a decorator needs no budget arithmetic");
            Assert.AreEqual(1, inner.FlushTimeouts.Count);
            Assert.IsTrue(inner.FlushTimeouts[0] <= TimeSpan.FromSeconds(1) && inner.FlushTimeouts[0] >= TimeSpan.Zero,
                "and what is left of the budget is what Sentry gets, since its flush burns the lot when egress is blocked");
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
            Assert.IsFalse(text.Contains("ingest.de.sentry.io"));

            // On property names rather than a whole-file substring scan, which would fire the day a
            // legitimate value happened to contain "dsn".
            foreach (var record in AllRecords())
                foreach (var property in record.EnumerateObject())
                    Assert.AreNotEqual("dsn", property.Name.ToLowerInvariant(),
                        "session.start reads two fields out of the bootstrap config by name rather than looping it");
        }

        [TestMethod]
        public void SetExtraWritesATypeNameForAnythingThatIsNotAStringOrPrimitive()
        {
            // Every one of these leaks its whole state through ToString(). That is the point: the
            // fixture must NOT override ToString, or the test proves only that ToString is called —
            // which is the defect. An earlier version of this test used a fixture that did, and it
            // passed green against an implementation that wrote the payload.
            var leaky = new object[]
            {
                new PatientSummary("Jane Doe", "943-476-5919", "BIRADS 5"),
                new { Patient = "Jane Doe", Nhs = "943-476-5919" },
                new StringBuilder("Jane Doe 943-476-5919"),
                new Uri("https://data.hospital.example/Patient/123?name=Jane%20Doe"),
                new InvalidOperationException("failed for patient Jane Doe (943-476-5919)"),
            };

            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("a", "op");
                for (var i = 0; i < leaky.Length; i++) span.SetExtra("extra" + i, leaky[i]);
                session.Dispose();
            }

            var text = File.ReadAllText(Directory.GetFiles(_dir, "*.jsonl").Single());
            Assert.IsFalse(text.Contains("Jane Doe"), "SetExtra takes object, and this file is built to be emailed to a vendor");
            Assert.IsFalse(text.Contains("943-476-5919"));
            Assert.IsFalse(text.Contains("BIRADS"));

            var written = AllRecords().Where(r => Type(r) == "span.extra")
                .Select(r => r.GetProperty("v").GetString()).ToList();
            Assert.AreEqual(leaky.Length, written.Count, "every extra is still recorded — the key and the type are the diagnostic");
            foreach (var value in written)
            {
                StringAssert.StartsWith(value, "<");
                StringAssert.EndsWith(value, ">", "a type name answers 'what was attached?' without carrying its contents; got " + value);
                Assert.IsTrue(value.Length <= 258,
                    "Type.FullName spells generic arguments out with version, culture and public key token — 200-odd characters " +
                    "of metadata that tells a reader nothing. Got " + value.Length + ": " + value);
                Assert.IsFalse(value.Contains("PublicKeyToken"), "assembly qualification is noise in a log line");
            }

            // The namespace is the part that identifies what was attached, so it has to survive.
            Assert.IsTrue(written.Any(v => v.Contains("System.Text.StringBuilder")),
                "actual: " + string.Join(" | ", written));
        }

        [TestMethod]
        public void SetExtraStillWritesStringsAndPrimitives()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("a", "op");
                span.SetExtra("retry", 2);
                span.SetExtra("reason", "handshake timeout");
                span.SetExtra("readonly", true);
                session.Dispose();
            }

            var extras = AllRecords().Where(r => Type(r) == "span.extra")
                .ToDictionary(r => r.GetProperty("k").GetString(), r => r.GetProperty("v"));

            Assert.AreEqual(2, extras["retry"].GetInt32(), "the allowlist must not cost the values that are safe and useful");
            Assert.AreEqual("handshake timeout", extras["reason"].GetString());
            Assert.IsTrue(extras["readonly"].GetBoolean());
        }

        [TestMethod]
        public void LongValuesAreCappedAtTheDocumentedLengths()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.AddBreadcrumb(new string('c', 5000), new string('m', 5000));
                var span = session.StartTransaction("a", "op");
                span.SetTag(new string('k', 5000), new string('v', 5000));
                session.Dispose();
            }

            var crumb = AllRecords().Single(r => Type(r) == "crumb");
            var tag = AllRecords().Single(r => Type(r) == "span.tag");

            // The README publishes these numbers, so pin them rather than "shorter than the input".
            Assert.AreEqual(2048, LengthBeforeMarker(crumb.GetProperty("msg").GetString()), "message cap");
            Assert.AreEqual(2048, LengthBeforeMarker(tag.GetProperty("v").GetString()), "tag value cap");
            Assert.AreEqual(256, LengthBeforeMarker(crumb.GetProperty("cat").GetString()), "breadcrumb category cap");
            Assert.AreEqual(256, LengthBeforeMarker(tag.GetProperty("k").GetString()), "tag key cap");

            foreach (var value in new[] { crumb.GetProperty("msg").GetString(), crumb.GetProperty("cat").GetString(),
                                          tag.GetProperty("v").GetString(), tag.GetProperty("k").GetString() })
                StringAssert.EndsWith(value, "[trimmed]", "a cut value must not read as complete");
        }

        [TestMethod]
        public void NameLikeFieldsAreRedactedToo_NotJustValues()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) Assert.Inconclusive("no user profile path on this platform");

            var path = Path.Combine(profile, "case.json");

            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                // A category is a natural place for a subject label, and the PHI paragraph promises
                // it is scrubbed. It used to be the one unscrubbed field sitting next to a scrubbed
                // one on the same line.
                session.AddBreadcrumb(path, path);
                var span = session.StartTransaction("a", "op");
                span.SetTag(path, path);
                session.Dispose();
            }

            var text = File.ReadAllText(Directory.GetFiles(_dir, "*.jsonl").Single());
            Assert.IsFalse(text.Contains(profile),
                "redaction that covers half the fields on a line follows no rule a caller could reason about");
            StringAssert.Contains(text, "%USERPROFILE%");
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
        // Lifetime — a session or span can outlive the sink that made it
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void WritingAfterTheSinkIsDisposedIsDropped_NotReopened()
        {
            ITelemetrySpan span;
            ITelemetrySession session;

            var sink = new FileTelemetrySink(_dir);
            session = sink.BeginSession(NewSessionId());
            span = session.StartTransaction("sdc.displayQuestionnaire", "swm.send");
            sink.Dispose();

            var recordsAtDispose = AllRecords().Count;

            // This is not hypothetical: TiroFormViewer cancels in-flight work in Dispose, and the
            // continuation that calls Finish(Cancelled) is posted to the WinForms pump, so it runs
            // after Dispose has returned. A second viewer.Dispose() reaches the session the same way.
            span.Finish(TelemetrySpanStatus.Cancelled);
            span.Dispose();
            session.Dispose();

            Assert.AreEqual(recordsAtDispose, AllRecords().Count,
                "a released log must not resurrect its file: the instance is de-registered and can never be released again, " +
                "so reopening leaks the handle and forks the transcript");
        }

        [TestMethod]
        public void ANewSinkAfterTheLastOneClosedGetsThePlainDayFileBack()
        {
            for (var i = 0; i < 5; i++)
            {
                var sink = new FileTelemetrySink(_dir);
                var session = sink.BeginSession(NewSessionId());
                var span = session.StartTransaction("a", "op");
                sink.Dispose();
                span.Finish(TelemetrySpanStatus.Cancelled);   // the late write, every cycle
                session.Dispose();
            }

            Assert.AreEqual(1, Directory.GetFiles(_dir, "*.jsonl").Length,
                "an open/close cycle must not cost a file — on Windows the zombie handle would push each new sink onto -2, -3, " +
                "and after ~102 cycles telemetry would stop for the day");
        }

        [TestMethod]
        public void DisposingASessionTwiceWritesOneTerminator()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.Dispose();
                session.Dispose();
            }

            Assert.AreEqual(1, AllRecords().Count(r => Type(r) == "session.end"),
                "TiroFormViewer.Dispose has no re-entry guard of its own, so a second dispose reaches here");
        }

        [TestMethod]
        public void RecordsAfterASessionEndsAreNotAttributedToIt()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.Dispose();
                sink.CaptureException(Thrown("after the session ended"));
            }

            var records = AllRecords();
            var end = records.FindIndex(r => Type(r) == "session.end");
            var error = records.FindIndex(r => Type(r) == "error");

            Assert.IsTrue(error > end, "sanity: the capture really did happen after the terminator");
            Assert.AreEqual("process", records[error].GetProperty("sid").GetString(),
                "session.end is documented as that session's terminator, so nothing may claim to belong to it afterwards");
        }

        // -----------------------------------------------------------------------------
        // Degrading without a writable directory
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void AnUnwritableDirectoryCostsNothingPerRecord()
        {
            var blocked = Path.Combine(_dir, "not-a-directory");
            File.WriteAllText(blocked, "");

            var clock = Stopwatch.StartNew();
            using (var sink = new FileTelemetrySink(Path.Combine(blocked, "telemetry")))
            {
                var session = sink.BeginSession(NewSessionId());
                for (var i = 0; i < 500; i++) session.AddBreadcrumb("lifecycle", "record " + i);
                session.Dispose();
            }
            clock.Stop();

            // Every record used to re-walk all 102 candidate names, each a thrown-and-caught
            // exception, on whatever thread the viewer reports from — the UI thread for anything
            // driven by a browser message. Measured at 1.42 ms per record before the backoff.
            Assert.IsTrue(clock.ElapsedMilliseconds < 200,
                "telemetry that cannot write must cost less than telemetry that can, not more. Took " + clock.ElapsedMilliseconds + " ms for 500 records");
        }

        // -----------------------------------------------------------------------------
        // The file support is meant to hand over
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void TheCurrentFilePathIsDiscoverable()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                Assert.IsNull(sink.CurrentFilePath, "nothing is opened until there is something to write");

                var session = sink.BeginSession(NewSessionId());
                var path = sink.CurrentFilePath;

                Assert.IsNotNull(path, "an application offering an 'attach diagnostics' button cannot reconstruct this — " +
                                       "the suffix rules are exactly the cases where guessing is wrong");
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(Directory.GetFiles(_dir, "*.jsonl").Single(), path);

                session.Dispose();
            }
        }

        [TestMethod]
        public void EveryErrorRecordCarriesASpanFieldEvenWhenThereIsNoSpan()
        {
            using (var sink = new FileTelemetrySink(_dir))
            {
                var session = sink.BeginSession(NewSessionId());
                session.StartTransaction("a", "op").Finish(Thrown("from a span"));
                sink.CaptureException(Thrown("out of band"));
                session.Dispose();
            }

            var errors = AllRecords().Where(r => Type(r) == "error").ToList();
            Assert.AreEqual(2, errors.Count);
            foreach (var error in errors)
                Assert.IsTrue(error.TryGetProperty("span", out _),
                    "a reader keying error -> span must not have to guess which of two shapes it got");

            Assert.AreEqual(1, errors.Count(e => e.GetProperty("span").ValueKind == JsonValueKind.Null),
                "the out-of-band capture belongs to no span, and says so explicitly");
        }

        // -----------------------------------------------------------------------------
        // Options
        // -----------------------------------------------------------------------------

        [TestMethod]
        public void OptionsCanChangeTheFileSizeCap()
        {
            var options = new FileTelemetryOptions { Directory = _dir, MaxBytesPerFile = 900 };

            using (var sink = new FileTelemetrySink(options))
            {
                var session = sink.BeginSession(NewSessionId());
                for (var i = 0; i < 40; i++) session.AddBreadcrumb("lifecycle", "padding padding padding");
                session.Dispose();
            }

            Assert.IsTrue(Directory.GetFiles(_dir, "*.jsonl").Length > 1,
                "a published limit a consumer can read but not change reads as configuration when it is not");
            Assert.AreEqual(40, AllRecords().Count(r => Type(r) == "crumb"), "and changing it must not cost records");
        }

        [TestMethod]
        public void OptionsAreCopiedSoLaterMutationDoesNothing()
        {
            var options = new FileTelemetryOptions { Directory = _dir };

            using (var sink = new FileTelemetrySink(options))
            {
                options.Directory = Path.Combine(_dir, "moved");
                var session = sink.BeginSession(NewSessionId());
                StringAssert.StartsWith(sink.CurrentFilePath, _dir);
                Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "moved")));
                session.Dispose();
            }
        }

        [TestMethod]
        public void AnEmptyOptionsDirectoryIsRejectedAtConstruction()
        {
            Assert.ThrowsException<ArgumentException>(
                () => new FileTelemetrySink(new FileTelemetryOptions { Directory = "" }),
                "better here than on the first record, which lands inside a viewer catch block");
        }

        // -----------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------

        private static string NewSessionId() => Guid.NewGuid().ToString();

        /// <summary>Length of a capped value with the trim marker removed.</summary>
        private static int LengthBeforeMarker(string value)
        {
            const string marker = "…[trimmed]";
            return value.EndsWith(marker, StringComparison.Ordinal) ? value.Length - marker.Length : value.Length;
        }

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

        /// <summary>
        /// PHI-shaped and, deliberately, <b>without</b> a ToString override — the compiler-generated
        /// one for a positional type dumps every member, which is exactly the leak being guarded.
        /// </summary>
        private sealed class PatientSummary
        {
            public PatientSummary(string name, string nhs, string diagnosis)
            {
                Name = name; Nhs = nhs; Diagnosis = diagnosis;
            }

            public string Name { get; }
            public string Nhs { get; }
            public string Diagnosis { get; }
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

            /// <summary>Runs inside <see cref="Flush"/>, so a test can observe the world at that moment.</summary>
            public Action OnFlush { get; set; }

            public int RecordsOnDiskAtFlush { get; set; } = -1;

            public ITelemetrySession BeginSession(string sessionId) => new Session(this);
            public void CaptureException(Exception ex) { }
            public void CaptureMessage(string message) { }

            public void Flush(TimeSpan timeout)
            {
                OnFlush?.Invoke();
                FlushTimeouts.Add(timeout);
            }
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
