using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <see cref="RollingTelemetryLog"/> — which file records go to, when to move to the next one,
    /// and what gets deleted. Split from <see cref="TestFileTelemetrySink"/> because these are
    /// storage decisions with their own failure modes, and two of them are only reachable through
    /// a clock seam or a second writer holding a file.
    /// <para>
    /// The theme running through all of it: <b>a bounded log must discard the oldest records, never
    /// the newest.</b> The newest are the ones next to whatever went wrong.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestRollingTelemetryLog
    {
        private string _dir;

        [TestInitialize]
        public void CreateTempDirectory()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tiro-rolling-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void RemoveTempDirectory()
        {
            RollingTelemetryLog.ResetForTests();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }

        [TestMethod]
        public void AFullFileRollsToTheNextIndexInsteadOfStopping()
        {
            // A cap small enough that a handful of records fills it.
            var log = RollingTelemetryLog.Acquire(_dir, maxBytesPerFile: 600, maxTotalBytes: 0, retentionDays: 7);
            try
            {
                for (var i = 0; i < 40; i++)
                    log.Write("crumb", "aaaaaaaa", json => json.WriteString("msg", "padding padding padding"));
            }
            finally
            {
                log.Release();
            }

            var files = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).OrderBy(f => f).ToList();
            Assert.IsTrue(files.Count > 1, "reaching the cap must roll, not stop — stopping throws away the records closest to the failure");
            StringAssert.Matches(files[1], new System.Text.RegularExpressions.Regex(@"^\d{8}-2\.jsonl$"),
                "the second file continues the day's series; got " + files[1]);

            var lastFile = Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f).Last();
            Assert.IsTrue(Records(lastFile).Any(r => Type(r) == "crumb"),
                "the newest records are in the newest file");
            Assert.IsTrue(Records(lastFile).Any(r => Type(r) == "header"),
                "each file stands alone, so each carries its own file header");
        }

        [TestMethod]
        public void AFileHeldByAnotherWriterIsSkipped()
        {
            // Stand in for a second process: hold today's file open for writing, which the log's
            // share mode denies. Two writers appending to one path could interleave mid-line,
            // so being refused and moving on is the correct outcome.
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            var taken = Path.Combine(_dir, today + ".jsonl");

            using (new FileStream(taken, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                if (!ShareModesAreEnforced(taken))
                    Assert.Inconclusive("this platform does not enforce FileShare, so a second writer cannot be refused; " +
                                        "the harness ships net48 on Windows, where the sharing violation is real");

                var log = RollingTelemetryLog.Acquire(_dir, maxBytesPerFile: 8192, maxTotalBytes: 0, retentionDays: 7);
                try
                {
                    log.Write("crumb", "aaaaaaaa", json => json.WriteString("msg", "second process"));
                    Assert.AreEqual(today + "-2.jsonl", Path.GetFileName(log.CurrentFilePath),
                        "a taken file must not mean no transcript at all");
                }
                finally
                {
                    log.Release();
                }
            }

            Assert.AreEqual(0, new FileInfo(taken).Length, "and the held file must be left alone");
        }

        [TestMethod]
        public void ThedayFileRollsAtMidnight()
        {
            var day = new DateTime(2026, 8, 28, 23, 59, 0, DateTimeKind.Utc);
            RollingTelemetryLog.UtcNowProvider = () => day;

            var log = RollingTelemetryLog.Acquire(_dir, maxBytesPerFile: 8192, maxTotalBytes: 0, retentionDays: 7);
            try
            {
                log.Write("crumb", "aaaaaaaa", json => json.WriteString("msg", "before midnight"));
                RollingTelemetryLog.UtcNowProvider = () => day.AddMinutes(2);
                log.Write("crumb", "aaaaaaaa", json => json.WriteString("msg", "after midnight"));
            }
            finally
            {
                log.Release();
            }

            var files = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).OrderBy(f => f).ToList();
            CollectionAssert.AreEqual(new[] { "20260828.jsonl", "20260829.jsonl" }, files,
                "a viewer left open across midnight would otherwise keep writing into a file named for the wrong day");
        }

        [TestMethod]
        public void SweepDeletesTranscriptsPastRetention()
        {
            var old = WriteTranscript("20200101.jsonl", 100, DateTime.UtcNow.AddDays(-30));
            var fresh = WriteTranscript("20260828.jsonl", 100, DateTime.UtcNow);

            RollingTelemetryLog.Sweep(_dir, retentionDays: 7, maxTotalBytes: 0);

            Assert.IsFalse(File.Exists(old), "transcripts accumulate on a workstation nobody administers; something has to remove them");
            Assert.IsTrue(File.Exists(fresh), "and the sweep must not take the session someone is about to ask for");
        }

        [TestMethod]
        public void SweepDeletesOldestUntilTheDirectoryFitsItsBudget()
        {
            var oldest = WriteTranscript("20260820.jsonl", 1000, DateTime.UtcNow.AddDays(-3));
            var middle = WriteTranscript("20260825.jsonl", 1000, DateTime.UtcNow.AddDays(-2));
            var newest = WriteTranscript("20260828.jsonl", 1000, DateTime.UtcNow.AddDays(-1));

            // Room for roughly two of the three, none of them old enough for the age bound.
            RollingTelemetryLog.Sweep(_dir, retentionDays: 7, maxTotalBytes: 2200);

            Assert.IsFalse(File.Exists(oldest), "the byte budget is the second bound, so age alone leaving three files is not enough");
            Assert.IsTrue(File.Exists(middle));
            Assert.IsTrue(File.Exists(newest), "and it deletes from the old end — the newest records are the ones worth keeping");
        }

        [TestMethod]
        public void SweepLeavesTheFileCurrentlyBeingWritten()
        {
            var live = WriteTranscript("20260828.jsonl", 5000, DateTime.UtcNow);

            RollingTelemetryLog.Sweep(_dir, retentionDays: 7, maxTotalBytes: 10, keep: live);

            Assert.IsTrue(File.Exists(live), "a budget tighter than the live file must not delete the session in progress");
        }

        [TestMethod]
        public void SweepOnAMissingDirectoryIsHarmless()
        {
            RollingTelemetryLog.Sweep(Path.Combine(_dir, "does", "not", "exist"), 7, 1024);
        }

        [TestMethod]
        public void InnerErrorRecordsAreCappedPerLog()
        {
            var log = RollingTelemetryLog.Acquire(_dir, maxBytesPerFile: 1 << 20, maxTotalBytes: 0, retentionDays: 7);
            try
            {
                for (var i = 0; i < 200; i++)
                    log.WriteInnerError("aaaaaaaa", "Session.AddBreadcrumb", new InvalidOperationException("backend down"));
            }
            finally
            {
                log.Release();
            }

            var count = AllRecords().Count(r => Type(r) == "inner.error");
            Assert.IsTrue(count > 0 && count <= 10,
                "a backend throwing on every call would otherwise bury the sessions the log was opened to record; got " + count);
        }

        // -----------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Whether the platform actually refuses a second writer on a file already open with
        /// <see cref="FileShare.Read"/>. On Windows — the only platform this harness ships to, being
        /// net48 and WinForms — the sharing violation is mandatory and is what lets a second process
        /// discover it needs its own file. POSIX has no mandatory locking, so a developer machine can
        /// happily open it twice and the mechanism under test does not exist there to be tested.
        /// </summary>
        private static bool ShareModesAreEnforced(string alreadyOpenPath)
        {
            try
            {
                using (new FileStream(alreadyOpenPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private string WriteTranscript(string name, int bytes, DateTime lastWriteUtc)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, new string('x', bytes));
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        private static string Type(JsonElement record) => record.GetProperty("type").GetString();

        private System.Collections.Generic.List<JsonElement> AllRecords()
            => Directory.GetFiles(_dir, "*.jsonl").SelectMany(Records).ToList();

        private static System.Collections.Generic.List<JsonElement> Records(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                var records = new System.Collections.Generic.List<JsonElement>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    records.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
                return records;
            }
        }
    }
}
