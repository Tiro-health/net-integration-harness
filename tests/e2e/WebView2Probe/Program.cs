using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Fhir.R5;
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
    /// machine and the SdcClient. Exit 0 = pass.
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

        private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(3);

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

            if (!ServerStagesEnabled)
            {
                Report("INFO", "stage B skipped (PROBE_SKIP_SERVER_STAGES=1)");
                return 0;
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
