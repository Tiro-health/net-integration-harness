using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Fhir.R5;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.FormSdk.Abstractions;
using Tiro.Health.FormSdk.Client.Fhir.R5;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
// Hl7.Fhir.Model.Task is a FHIR resource, and it shadows the bare Task used below.
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormFiller.WebView2.E2E
{
    /// <summary>
    /// Layer 2 of GH-26: the whole stack in one process — real WebView2, the embedded
    /// web-sdk served over its virtual host, the real bridge, and the real .NET host —
    /// modelled on the ExtractSample (fill, submit, then $extract the result).
    ///
    /// Stage A needs no server and proves GH-60/GH-61: the bundle is served, the element
    /// upgrades, the bridge runs before page scripts, and the handshake reaches the host.
    /// Stage B needs a server and covers what layer 1 structurally cannot: the QR the real
    /// element produced, deserialized by Firely into a typed POCO, driving the .NET state
    /// machine and the SdcClient.
    ///
    /// Stage C reads back the file telemetry transcript this run wrote. Unit tests drive
    /// ITelemetrySink through a hand-written imitation of the viewer's call sequence, which
    /// cannot notice if the imitation is wrong; this is the only place the sink sees a real
    /// WebView2 session, a real handshake and a real Dispose on a message pump. Exit 0 = pass.
    /// </summary>
    internal static class Program
    {
        // Staging, never the production demo instance the viewer defaults to: see
        // tests/e2e/README.md.
        private static readonly string SdcEndpoint =
            Environment.GetEnvironmentVariable("SDC_ENDPOINT") ?? "https://sdc-staging.tiro.health/fhir/r5";
        // Version-pinned, exactly as layer 1 is. The same canonical carries a mutable draft-1 and
        // staging's Questionnaire search ignores the version parameter (draft first), so without
        // the pin this layer rendered whatever the draft says today — while the README claimed the
        // suite was pinned. Half a pinned suite reads as a pinned suite.
        private static readonly string Questionnaire =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE")
            ?? "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699|1.0.0";
        private static readonly bool ServerStagesEnabled =
            Environment.GetEnvironmentVariable("PROBE_SKIP_SERVER_STAGES") != "1";

        // Which verdict this cell expects from the SDC version check. "satisfied" for a released
        // server; "dev" for sdc-dev, which reports software.version "dev" — outside the version
        // grammar by design, so Satisfied is unreachable there and demanding it would make that
        // cell red forever.
        //
        // The default is the STRICT value: an unset variable can only ever make this stricter,
        // never weaker. An unrecognised value fails the run rather than falling back, because a
        // parameter that quietly selects a weaker assertion is how a suite stops testing anything.
        private static readonly string ExpectedVerdict =
            (Environment.GetEnvironmentVariable("SDC_EXPECTED_VERDICT") ?? "satisfied").Trim().ToLowerInvariant();

        private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Where stage C's transcript goes. Beside probe-report.log, so the workflow step that
        /// already knows how to find one can print and upload the other.
        /// </summary>
        private static readonly string TelemetryDirectory =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telemetry");

        [STAThread]
        private static int Main()
        {
            // FIRST, before anything else touches WinForms. "Thread exception mode cannot be
            // changed once any Controls are created on the thread" — and constructing the Form is
            // enough to count, so this call sitting one line below it threw at startup and the
            // probe died before writing a verdict. The gate caught it, which is the only reason
            // this was a red run rather than a green one.
            //
            // Why it is here at all: anything thrown on the UI thread OUTSIDE RunAsync's try — a
            // WebView2 callback, an event handler — otherwise reaches WinForms' default modal
            // error dialog, which nobody dismisses on a headless runner. The probe would hang to
            // the job's 25-minute ceiling at 2x Windows billing and write nothing. A hang that
            // costs money and says nothing is the worst failure mode available.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();

            // File telemetry, with no inner sink: the air-gapped configuration, so this exercises
            // the transcript without needing a DSN or any egress from the runner. Registered here
            // because TiroFormViewerDefaults is sampled by each viewer's constructor — after the
            // viewer below exists it would be too late, which is the ordering integrators get wrong.
            PrepareTelemetryDirectory();
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new FileTelemetrySink(TelemetryDirectory);

            var exitCode = 1;
            // Declared before the handler so the closure can reach it, assigned after: the mode
            // above must be set while no Control exists.
            Form form = null;

            Application.ThreadException += (_, e) =>
            {
                Report("FAIL", "unhandled UI-thread exception: " + e.Exception);
                exitCode = 1;
                try { form?.Close(); } catch { /* already going down */ }
            };
            // Non-UI threads cannot be rescued on .NET Framework — the CLR tears the process down
            // regardless — but the report is written first, so the log says why instead of the
            // step just reporting a non-zero exit.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Report("FAIL", "unhandled exception on a non-UI thread: " + e.ExceptionObject);

            form = new Form { Text = "WebView2 probe", Width = 1000, Height = 800 };
            var viewer = new TiroFormViewerR5 { Dock = DockStyle.Fill, SdcEndpointAddress = SdcEndpoint };
            form.Controls.Add(viewer);

            form.Shown += async (_, __) =>
            {
                try { exitCode = await RunAsync(viewer); }
                catch (Exception ex) { Report("FAIL", "unhandled: " + ex); exitCode = 1; }
                finally { form.Close(); }
            };

            Application.Run(form);
            try { viewer.Dispose(); } catch { /* best-effort; the process is exiting anyway */ }

            // After the dispose, deliberately: session.end and the final flush are part of what
            // stage C checks, and they only happen when the viewer goes away. Only run it if the
            // stages above passed — a transcript of a failed run has nothing to say.
            if (exitCode == 0) exitCode = CheckTranscript();

            Report("INFO", "exiting with " + exitCode);
            return exitCode;
        }

        private static async Task<int> RunAsync(TiroFormViewerR5 viewer)
        {
            // RunAsync is started from Form.Shown and every await below resumes on the UI
            // thread, so these lists are only ever touched by one thread.
            var submissions = new List<QuestionnaireResponse>();
            viewer.FormSubmitted += (_, e) => submissions.Add(e.Response);

            var dirtyChanges = new List<bool>();
            viewer.FormDirtyChanged += (_, e) => dirtyChanges.Add(e.IsDirty);

            // Checked here rather than where it is used: a typo in the workflow would otherwise
            // spend a whole WebView2 session before reporting that the run was never going to
            // assert anything meaningful.
            if (ExpectedVerdict != "satisfied" && ExpectedVerdict != "dev")
            {
                Report("FAIL", "SDC_EXPECTED_VERDICT='" + ExpectedVerdict + "' is not a value this "
                    + "probe knows. Expected 'satisfied' (a released server) or 'dev' (sdc-dev, "
                    + "which reports the version string 'dev'). Unset means 'satisfied'.");
                return 1;
            }

            // --- Stage A: the page comes up (no server needed) --------------------------
            using (var cts = new CancellationTokenSource(StageTimeout))
            {
                try
                {
                    await viewer.SetContextAsync(Questionnaire, patient: SamplePatient(), cancellationToken: cts.Token);
                }
                catch (WebSdkLoadException ex)
                {
                    Report("FAIL", "web-sdk load refused: reason=" + ex.Reason + " :: " + ex.Message);
                    return 1;
                }
                catch (TimeoutException)
                {
                    Report("FAIL", "handshake timeout — the bridge never reached the host");
                    return 1;
                }
            }

            // No state check here: SetContextAsync only returns once the handshake has landed, so
            // a surviving Initializing is unreachable and reading this as THE handshake assertion
            // would be wrong — the assertion is that the await above did not throw. The state is
            // reported below because it is useful, not because it is checked.
            Report("PASS", "stage A — embedded web-sdk served over the virtual host, bridge injected, "
                + "handshake received (state=" + viewer.State + ", pageWebSdkVersion="
                + (viewer.PageWebSdkVersion ?? "(null)") + ")");

            // Reported here, asserted in stage B. The viewer starts the probe inside
            // SetContextAsync, so the verdict is already available and worth printing — but
            // reaching it requires the server to answer, which makes the assertion
            // server-dependent even though the stage around it isn't.
            var versionCheck = viewer.SdcServerVersionCheck;
            Report("INFO", "SDC version check — " + (versionCheck?.ToString() ?? "(not run)")
                + " [minimum " + SdcCompatibility.MinimumSdcVersion + "]");

            if (!ServerStagesEnabled)
            {
                Report("INFO", "stage B skipped (PROBE_SKIP_SERVER_STAGES=1)");
                return 0;
            }

            // Asserted, not just logged. This is the only place the version check runs against a
            // real server, so it is the only thing standing behind three contracts nothing else
            // in this repo can hold: that {base}/metadata stays locally routed rather than
            // tunnelled to the data endpoint, that software.version keeps meaning the SDC
            // server's application version, and that software.name keeps saying what it says. A
            // change to any of them turns this green run red within a day. An "unknown" verdict
            // is precisely the failure to catch — it means the suite would otherwise keep passing
            // with the gate silently disarmed.
            //
            // Below the bail on purpose. It used to sit in stage A, which is labelled "no server
            // needed" and is the only part a pull request gates on — so a staging outage reddened
            // pull requests that could not have caused it, which e2e.yml explicitly sets out not
            // to do. A suite that goes red for reasons outside the author's control gets ignored,
            // and this assertion is far too valuable to spend that way.
            if (versionCheck == null)
            {
                Report("FAIL", "the SDC version check never ran against " + SdcEndpoint + ".");
                return 1;
            }

            if (ExpectedVerdict == "satisfied")
            {
                if (versionCheck.Outcome != SdcVersionCheckOutcome.Satisfied)
                {
                    Report("FAIL", "the SDC version check did not reach a Satisfied verdict against "
                        + SdcEndpoint + " — the server changed something this check depends on, "
                        + "or the check itself is broken. Either way it is no longer protecting anyone.");
                    return 1;
                }
                Report("PASS", "SDC version check satisfied against a real server");
            }
            else if (ExpectedVerdict == "dev")
            {
                // Not a relaxation — a different assertion of the same strength. Unknown is not one
                // bucket: a version outside the grammar keeps ReportedVersion (here, "dev"), while
                // an unreachable server, a tunnelled /metadata, or a document that cannot be
                // attributed to the SDC server all leave it null. Requiring both therefore still
                // proves the route is locally handled, the document is attributable, and the
                // version is read from the field it is supposed to be read from. The one thing it
                // cannot prove is that the grammar and comparison work on a real version string —
                // dev never emits one, and the staging cell covers it.
                if (versionCheck.Outcome != SdcVersionCheckOutcome.Unknown
                    || versionCheck.ReportedVersion != "dev")
                {
                    Report("FAIL", "expected the dev server to report an Unknown verdict carrying the "
                        + "version string 'dev', got outcome=" + versionCheck.Outcome
                        + " reportedVersion=" + (versionCheck.ReportedVersion ?? "(null)")
                        + ". A null reportedVersion means no version could be read at all — the "
                        + "route is no longer locally handled, or the document is no longer "
                        + "attributable to the SDC server. A different string means the dev build's "
                        + "version sentinel changed, and this assertion needs updating with it.");
                    return 1;
                }
                Report("PASS", "SDC version check reached the expected dev verdict (Unknown, 'dev')");
            }
            else
            {
                // Unreachable given the guard at the top of this method, and kept anyway: if a
                // third verdict is ever added there, this is what stops it reaching here and
                // asserting nothing at all.
                Report("FAIL", "no assertion is defined for SDC_EXPECTED_VERDICT='" + ExpectedVerdict + "'.");
                return 1;
            }

            // --- Stage B: save-draft, keep filling, finalize, extract ------------------
            // Mirrors the EhrShell + Extract samples. Every assertion here is on a typed
            // FHIR POCO, so it also proves Firely can deserialize what the element emits.
            var draftMark = submissions.Count;
            var draft = await FirstSubmit(viewer, "save-draft", submissions,
                QuestionnaireResponse.QuestionnaireResponseStatus.InProgress);
            if (draft == null)
            {
                // Both failures land here now that the wait matches on status: nothing came back,
                // or something came back that was not a draft. Observed() is what tells them
                // apart, and "saw: completed" IS the silent-finalize signature — the bug this
                // suite exists for — so the diagnosis has to be in the message, not inferred from
                // its absence.
                Report("FAIL", "no in-progress response after save-draft" + Observed(submissions, draftMark));
                return 1;
            }
            if (viewer.State != TiroFormViewerState.ContextSet)
            {
                Report("FAIL", "a saved draft ended the session (state=" + viewer.State + "); the doctor cannot keep filling");
                return 1;
            }
            Report("PASS", "stage B1 — save-draft returned in-progress and the session stayed usable");

            var finalMark = submissions.Count;
            var final = await SubmitOnce(viewer, null, submissions,
                QuestionnaireResponse.QuestionnaireResponseStatus.Completed);
            if (final == null)
            {
                Report("FAIL", "no completed response after finalize" + Observed(submissions, finalMark));
                return 1;
            }
            if (viewer.State != TiroFormViewerState.Submitted)
            {
                Report("FAIL", "a completed response did not end the session (state=" + viewer.State + ")");
                return 1;
            }
            // The pin was honoured, not merely requested: staging's search ignores the version
            // parameter, so it is the SDK that has to respect it. The QR echoes the canonical it
            // was filled from, which is where a dropped pin shows up.
            if (final.Questionnaire != Questionnaire)
            {
                Report("FAIL", "the QR was filled from " + (final.Questionnaire ?? "(none)")
                    + ", not the pinned " + Questionnaire);
                return 1;
            }
            Report("PASS", "stage B2 — finalize returned completed, ended the session, "
                + "and the QR echoes the pinned canonical");

            // $extract over the QR the real element produced, against the same server the
            // form rendered against — the ExtractSample's flow.
            using (var client = new SdcClient(new Uri(SdcEndpoint)))
            using (var cts = new CancellationTokenSource(StageTimeout))
            {
                try
                {
                    var bundle = await client.ExtractAsync(final, cts.Token);
                    Report("PASS", "stage B3 — $extract returned a " + bundle.Type + " bundle with "
                        + (bundle.Entry?.Count ?? 0) + " entries");
                }
                catch (Exception ex)
                {
                    Report("FAIL", "$extract failed: " + ex.GetType().Name + ": " + ex.Message);
                    return 1;
                }
            }

            Report("INFO", "dirty transitions observed: " + string.Join(",", dirtyChanges));
            Report("PASS", "all stages");
            return 0;
        }

        /// <summary>
        /// The first submit of a session, retried until one lands. A submit requested before
        /// the questionnaire has rendered is silently dropped: the bridge's
        /// ui.form.requestSubmit handler returns early when formFiller.questionnaire is unset,
        /// with no error and no response. And SetContextAsync returns on the page's ACK of
        /// sdc.displayQuestionnaire, not on render — so "context set" does not mean "ready to
        /// submit", and nothing in the host API exposes render-completion.
        /// </summary>
        private static async Task<QuestionnaireResponse> FirstSubmit(
            TiroFormViewerR5 viewer, string intent, List<QuestionnaireResponse> submissions,
            QuestionnaireResponse.QuestionnaireResponseStatus wanted)
        {
            var mark = submissions.Count;
            var deadline = DateTime.UtcNow + StageTimeout;

            for (var attempt = 1; DateTime.UtcNow < deadline; attempt++)
            {
                if (!await Send(viewer, intent)) return null;

                // Short per-attempt wait: a dropped request yields no response at all, so
                // asking again is the only way to tell "not rendered yet" from "slow".
                var landed = await WaitForSubmission(submissions, mark, TimeSpan.FromSeconds(10), wanted);
                if (landed == null) continue;

                if (attempt > 1)
                {
                    // A retry can still yield two responses: an attempt that was merely slow
                    // produces one of its own. No drain delay bounds that, so the extras are left
                    // to arrive and the next stage matches on status rather than position.
                    Report("INFO", "submit landed on attempt " + attempt
                        + "; later stages match on status, so a straggler cannot be read as theirs");
                }
                return landed;
            }

            return null;
        }

        /// <summary>
        /// A submit after the form is known to have rendered: sent once, then awaited for the
        /// full stage timeout. Deliberately not retried — a resubmit races the first request
        /// rather than replacing it, and once the page has finalized a response it refuses the
        /// second with an error, which would turn a merely slow finalize into a hard failure.
        /// </summary>
        private static async Task<QuestionnaireResponse> SubmitOnce(
            TiroFormViewerR5 viewer, string intent, List<QuestionnaireResponse> submissions,
            QuestionnaireResponse.QuestionnaireResponseStatus wanted)
        {
            var mark = submissions.Count;
            if (!await Send(viewer, intent)) return null;
            return await WaitForSubmission(submissions, mark, StageTimeout, wanted);
        }

        private static async Task<bool> Send(TiroFormViewerR5 viewer, string intent)
        {
            try
            {
                await viewer.SendFormRequestSubmitAsync(intent);
                return true;
            }
            catch (Exception ex)
            {
                Report("FAIL", "SendFormRequestSubmitAsync(" + (intent ?? "finalize") + ") threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Polls for the first submission after <paramref name="mark"/> that MATCHES
        /// <paramref name="wanted"/>, rather than for whatever arrives next. Polling rather than a
        /// TaskCompletionSource because the completion source has to be swapped between stages,
        /// and a response arriving inside that window is lost.
        /// <para>
        /// Matching, not position, is what keeps the stages honest. A retried first submit can
        /// still land its earlier attempt's response later — the retry interval is 10s and no
        /// drain delay can bound that — so a positional read gave the NEXT stage a duplicate from
        /// the previous one, and B2 failed with "finalize produced status=in-progress" over a
        /// status the finalize never produced. A stage now ignores anything that isn't the outcome
        /// it is waiting for, so a straggler is inert instead of misattributed.
        /// </para>
        /// <para>
        /// The risk this trades into: a stage whose real result has the wrong status waits out its
        /// timeout instead of failing immediately. That is the right way round — a slow red says
        /// "no matching response", which is true, where the old fast red named a status that
        /// belonged to another stage.
        /// </para>
        /// </summary>
        private static async Task<QuestionnaireResponse> WaitForSubmission(
            List<QuestionnaireResponse> submissions,
            int mark,
            TimeSpan timeout,
            QuestionnaireResponse.QuestionnaireResponseStatus? wanted)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                for (var i = mark; i < submissions.Count; i++)
                {
                    if (wanted == null || submissions[i].Status == wanted) return submissions[i];
                }
                await Task.Delay(50);
            }

            return null;
        }

        /// <summary>
        /// What actually arrived after <paramref name="mark"/>, for a FAIL message. A stage that
        /// timed out because the wrong status came back must say which one, or the message reads
        /// as a hang.
        /// </summary>
        private static string Observed(List<QuestionnaireResponse> submissions, int mark)
        {
            if (submissions.Count <= mark) return "; no form.submitted arrived at all";
            var seen = new List<string>();
            for (var i = mark; i < submissions.Count; i++) seen.Add(submissions[i].Status.ToString());
            return "; saw: " + string.Join(", ", seen);
        }

        private static Patient SamplePatient() => new Patient
        {
            Id = "probe-patient",
            Gender = AdministrativeGender.Female,
            BirthDate = "1970-01-01",
        };

        private static void PrepareTelemetryDirectory()
        {
            try
            {
                if (System.IO.Directory.Exists(TelemetryDirectory))
                    System.IO.Directory.Delete(TelemetryDirectory, recursive: true);
            }
            catch { /* a leftover file only risks a confusing assertion, not a wrong one */ }
        }

        /// <summary>
        /// Stage C: the transcript this run produced, read back and checked.
        /// <para>
        /// What this can do that a unit test cannot: the records come from the real viewer's own
        /// calls, so the sequence is the sequence rather than my reading of it. It also prints the
        /// transcript, which makes the sample in the README a capture instead of a reconstruction.
        /// </para>
        /// </summary>
        private static int CheckTranscript()
        {
            string[] files;
            try
            {
                files = System.IO.Directory.Exists(TelemetryDirectory)
                    ? System.IO.Directory.GetFiles(TelemetryDirectory, "*.jsonl")
                    : new string[0];
            }
            catch (Exception ex)
            {
                Report("FAIL", "stage C — could not read " + TelemetryDirectory + ": " + ex.Message);
                return 1;
            }

            if (files.Length != 1)
            {
                Report("FAIL", "stage C — expected exactly one transcript in " + TelemetryDirectory
                             + ", found " + files.Length + ". One process, one day, one file.");
                return 1;
            }

            var text = System.IO.File.ReadAllText(files[0]);
            var lines = System.IO.File.ReadAllLines(files[0]);
            Report("INFO", "stage C — transcript " + System.IO.Path.GetFileName(files[0])
                         + " (" + text.Length + " bytes, " + lines.Length + " lines)");
            foreach (var line in lines) Report("XCRIPT", line);

            var types = new List<string>();
            var ops = new List<string>();
            var names = new List<string>();
            var crumbs = new List<string>();
            var tagKeys = new List<string>();
            string fullSessionId = null;

            foreach (var line in lines)
            {
                if (line.Length == 0) continue;

                System.Text.Json.JsonDocument document;
                try { document = System.Text.Json.JsonDocument.Parse(line); }
                catch (Exception ex)
                {
                    Report("FAIL", "stage C — a line did not parse, so the one-record-per-line "
                                 + "invariant is broken: " + ex.Message + " :: " + line);
                    return 1;
                }

                using (document)
                {
                    var record = document.RootElement;

                    System.Text.Json.JsonElement type;
                    System.Text.Json.JsonElement sid;
                    if (!record.TryGetProperty("type", out type) || !record.TryGetProperty("sid", out sid)
                        || !record.TryGetProperty("ts", out _))
                    {
                        Report("FAIL", "stage C — a record is missing type/ts/sid, so it cannot be read "
                                     + "on its own: " + line);
                        return 1;
                    }

                    if (sid.GetString().Length > 8)
                    {
                        Report("FAIL", "stage C — sid should be the short form on every line: " + line);
                        return 1;
                    }

                    types.Add(type.GetString());

                    System.Text.Json.JsonElement value;
                    if (type.GetString() == "session.start" && record.TryGetProperty("session", out value))
                        fullSessionId = value.GetString();
                    if (record.TryGetProperty("op", out value)) ops.Add(value.GetString());
                    if (record.TryGetProperty("name", out value)) names.Add(value.GetString());
                    if (type.GetString() == "crumb" && record.TryGetProperty("msg", out value)) crumbs.Add(value.GetString());
                    if (record.TryGetProperty("k", out value)) tagKeys.Add(value.GetString());
                }
            }

            // The viewer's own lifecycle, start to finish. A missing session.end would mean the
            // dispose path never reached the transcript.
            foreach (var required in new[] { "header", "session.start", "span.start", "span.end", "session.end" })
            {
                if (!types.Contains(required))
                {
                    Report("FAIL", "stage C — no '" + required + "' record. Types seen: " + string.Join(",", types));
                    return 1;
                }
            }

            if (types.IndexOf("session.end") < types.IndexOf("session.start"))
            {
                Report("FAIL", "stage C — session.end precedes session.start");
                return 1;
            }

            if (fullSessionId == null || fullSessionId.Length < 32)
            {
                Report("FAIL", "stage C — session.start carries no full form.session.id ('"
                             + (fullSessionId ?? "(none)") + "'), which is the key a Sentry event is found by");
                return 1;
            }

            if (!ops.Contains("swm.lifecycle.init"))
            {
                Report("FAIL", "stage C — no swm.lifecycle.init span; the viewer's own init transaction "
                             + "never reached the transcript. Ops seen: " + string.Join(",", ops));
                return 1;
            }

            if (!crumbs.Exists(c => c.IndexOf("constructed", StringComparison.OrdinalIgnoreCase) >= 0)
                || !crumbs.Exists(c => c.IndexOf("disposed", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Report("FAIL", "stage C — the construction and dispose breadcrumbs did not both arrive: "
                             + string.Join(" | ", crumbs));
                return 1;
            }

            if (ServerStagesEnabled)
            {
                // Only reachable with a server: these are the per-message transactions.
                foreach (var required in new[] { "sdc.displayQuestionnaire", "ui.form.requestSubmit" })
                {
                    if (!names.Contains(required))
                    {
                        Report("FAIL", "stage C — no '" + required + "' transaction. Names seen: "
                                     + string.Join(",", names));
                        return 1;
                    }
                }

                if (!tagKeys.Contains("questionnaire_url"))
                {
                    Report("FAIL", "stage C — no questionnaire_url tag. Tag keys seen: " + string.Join(",", tagKeys));
                    return 1;
                }
            }

            // PHI, against a real session rather than a fixture: the transcript must carry no FHIR
            // payload. These three markers cannot appear in a QuestionnaireResponse-free file, and
            // they are what a leak through a tag, a breadcrumb or an extra would drag in.
            foreach (var marker in new[] { "resourceType", "linkId", "valueString" })
            {
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Report("FAIL", "stage C — the transcript contains '" + marker + "', so a FHIR payload "
                                 + "reached a file built to be emailed");
                    return 1;
                }
            }

            // A real session is a few dozen small records. A transcript an order of magnitude
            // larger means something is writing payloads even if none of the markers above hit.
            if (text.Length > 64 * 1024)
            {
                Report("FAIL", "stage C — transcript is " + text.Length + " bytes for one session; "
                             + "a few dozen records should be single-digit KB");
                return 1;
            }

            Report("PASS", "stage C — the real viewer's session round-tripped through FileTelemetrySink: "
                         + lines.Length + " records, every line parseable, lifecycle complete, no FHIR payload");
            return 0;
        }

        private static void Report(string level, string message)
        {
            var line = "[probe] " + level + ": " + message;
            Console.WriteLine(line);
            Console.Out.Flush();
            // Also to a file: stdout from a windowed process is easy to lose, and a probe
            // whose verdict vanished is indistinguishable from a probe that passed.
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-report.log"),
                    line + Environment.NewLine);
            }
            catch { /* best-effort */ }
        }
    }
}
