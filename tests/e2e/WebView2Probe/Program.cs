using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tiro.Health.FormFiller.WebView2.Fhir.R5;

namespace Tiro.Health.FormFiller.WebView2.E2E
{
    /// <summary>
    /// Layer 2 of GH-26: exercises the parts only a real WebView2 can exercise — the
    /// embedded web-sdk actually served over its virtual host under DenyCors, the bridge
    /// injected before page scripts, and the handshake reaching the .NET host with the
    /// SDK's identity (GH-60/GH-61). Deliberately does NOT need an SDC server: everything
    /// above happens before a questionnaire is requested. Exit 0 = pass, 1 = fail.
    /// </summary>
    internal static class Program
    {
        private const int TimeoutSeconds = 120;

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            var exitCode = 1;

            // WinForms needs a pump for WebView2's async init to progress; run the probe as
            // a continuation on the UI thread and quit the loop when it settles.
            var form = new Form { Text = "WebView2 probe", Width = 900, Height = 700 };
            var viewer = new TiroFormViewerR5 { Dock = DockStyle.Fill };
            form.Controls.Add(viewer);

            form.Shown += async (_, __) =>
            {
                try
                {
                    exitCode = await RunAsync(viewer);
                }
                catch (Exception ex)
                {
                    Report("FAIL", "unhandled: " + ex);
                    exitCode = 1;
                }
                finally
                {
                    form.Close();
                }
            };

            Application.Run(form);
            return exitCode;
        }

        private static async Task<int> RunAsync(TiroFormViewerR5 viewer)
        {
            Report("INFO", "state=" + viewer.State);

            // SetContextAsync navigates, waits for the handshake, then sends
            // sdc.configure + sdc.displayQuestionnaire. Without a reachable SDC server the
            // form cannot render, but everything this probe asserts has already happened by
            // then — so a canonical that resolves nowhere is fine and keeps the probe
            // server-independent.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                try
                {
                    await viewer.SetContextAsync("http://example.invalid/Questionnaire/probe", cancellationToken: cts.Token);
                    Report("INFO", "SetContextAsync completed");
                }
                catch (WebSdkLoadException ex)
                {
                    // The embedded bundle did not load, or the page loaded its own copy.
                    // This is the failure this probe exists to catch.
                    Report("FAIL", "web-sdk load refused: reason=" + ex.Reason + " :: " + ex.Message);
                    return 1;
                }
                catch (WebSdkVersionMismatchException ex)
                {
                    Report("FAIL", "version mismatch: expected=" + ex.ExpectedVersion + " reported=" + ex.ReportedVersion);
                    return 1;
                }
                catch (TimeoutException)
                {
                    // No handshake within 30s: the bridge never reached the host, meaning the
                    // page or the injected bundle failed to come up at all.
                    Report("FAIL", "handshake timeout — bridge never reached the host");
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    Report("FAIL", "probe timed out after " + TimeoutSeconds + "s");
                    return 1;
                }
                catch (Exception ex)
                {
                    // Anything else (e.g. the unresolvable questionnaire) is expected here:
                    // the handshake assertions below are what matter.
                    Report("INFO", "SetContextAsync threw (expected without a server): " + ex.GetType().Name);
                }
            }

            // The handshake is the proof: it can only arrive if the virtual host served the
            // embedded bundle, the element upgraded, and the bridge ran before page scripts.
            var handshakeArrived = viewer.State != TiroFormViewerState.Initializing;
            Report("INFO", "state=" + viewer.State + " pageWebSdkVersion=" + (viewer.PageWebSdkVersion ?? "(null)"));

            if (!handshakeArrived)
            {
                Report("FAIL", "no handshake: state never left Initializing");
                return 1;
            }

            Report("PASS", "embedded web-sdk served over the virtual host, bridge injected, handshake received");
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
