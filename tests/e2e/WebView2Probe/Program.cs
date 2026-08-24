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
        private static readonly string Questionnaire =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE")
            ?? "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699";
        private static readonly bool ServerStagesEnabled =
            Environment.GetEnvironmentVariable("PROBE_SKIP_SERVER_STAGES") != "1";

        private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(3);

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            var exitCode = 1;

            var form = new Form { Text = "WebView2 probe", Width = 1000, Height = 800 };
            var viewer = new TiroFormViewerR5 { Dock = DockStyle.Fill, SdcEndpointAddress = SdcEndpoint };
            form.Controls.Add(viewer);

            form.Shown += async (_, __) =>
            {
                try { exitCode = await RunAsync(viewer); }
                catch (Exception ex) { Report("FAIL", "unhandled: " + ex); exitCode = 1; }
                finally { form.Close(); }
            };

            Application.Run(form);
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

            if (viewer.State == TiroFormViewerState.Initializing)
            {
                Report("FAIL", "no handshake: state never left Initializing");
                return 1;
            }
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
            var draft = await FirstSubmit(viewer, "save-draft", submissions);
            if (draft == null) { Report("FAIL", "no form.submitted after save-draft"); return 1; }
            if (draft.Status != QuestionnaireResponse.QuestionnaireResponseStatus.InProgress)
            {
                // The silent-finalize bug, asserted in typed FHIR rather than a JSON string.
                Report("FAIL", "save-draft produced status=" + draft.Status + ", expected in-progress");
                return 1;
            }
            if (viewer.State != TiroFormViewerState.ContextSet)
            {
                Report("FAIL", "a saved draft ended the session (state=" + viewer.State + "); the doctor cannot keep filling");
                return 1;
            }
            Report("PASS", "stage B1 — save-draft returned in-progress and the session stayed usable");

            var final = await SubmitOnce(viewer, null, submissions);
            if (final == null) { Report("FAIL", "no form.submitted after finalize"); return 1; }
            if (final.Status != QuestionnaireResponse.QuestionnaireResponseStatus.Completed)
            {
                Report("FAIL", "finalize produced status=" + final.Status + ", expected completed");
                return 1;
            }
            if (viewer.State != TiroFormViewerState.Submitted)
            {
                Report("FAIL", "a completed response did not end the session (state=" + viewer.State + ")");
                return 1;
            }
            Report("PASS", "stage B2 — finalize returned completed and ended the session");

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
            TiroFormViewerR5 viewer, string intent, List<QuestionnaireResponse> submissions)
        {
            var mark = submissions.Count;
            var deadline = DateTime.UtcNow + StageTimeout;

            for (var attempt = 1; DateTime.UtcNow < deadline; attempt++)
            {
                if (!await Send(viewer, intent)) return null;

                // Short per-attempt wait: a dropped request yields no response at all, so
                // asking again is the only way to tell "not rendered yet" from "slow".
                var landed = await WaitForSubmission(submissions, mark, TimeSpan.FromSeconds(10));
                if (landed == null) continue;

                if (attempt > 1)
                {
                    // An attempt that was merely slow rather than dropped still produces a
                    // response, so a retry can yield two. Let the extras arrive and account
                    // for them here, or the next stage would read one as its own result.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    var extras = submissions.Count - mark - 1;
                    Report("INFO", "submit landed on attempt " + attempt
                        + (extras > 0 ? ", discarding " + extras + " duplicate response(s) from earlier attempts" : ""));
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
            TiroFormViewerR5 viewer, string intent, List<QuestionnaireResponse> submissions)
        {
            var mark = submissions.Count;
            if (!await Send(viewer, intent)) return null;
            return await WaitForSubmission(submissions, mark, StageTimeout);
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
        /// Polls for the first submission recorded after <paramref name="mark"/>. Polling
        /// rather than a TaskCompletionSource because the completion source has to be swapped
        /// between stages, and a response arriving inside that window is lost.
        /// </summary>
        private static async Task<QuestionnaireResponse> WaitForSubmission(
            List<QuestionnaireResponse> submissions, int mark, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (submissions.Count > mark) return submissions[mark];
                await Task.Delay(50);
            }

            return null;
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
