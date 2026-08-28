Imports Tiro.Health.FormFiller.WebView2
Imports Tiro.Health.FormFiller.WebView2.Sentry
Imports Tiro.Health.FormFiller.WebView2.Telemetry

Module Program
    <STAThread>
    Public Sub Main()
        ' Opt the whole app into telemetry — before any TiroFormViewerR5 is constructed.
        ' Designer-placed viewers in EhrShell/ReportConsultationForm pick this up
        ' automatically; viewers built before this line runs would not.
        '
        ' This registers BOTH sinks: a JSONL transcript on local disk, wrapped around
        ' Sentry. The file is there for sites whose network will not let Sentry out, and
        ' since a blocked DSN looks identical to a healthy one from inside the process,
        ' wrapping is safer than choosing. Sentry's own behaviour is unchanged — same
        ' hosted DSNs, same embedded-page DSN, same unified trace. Both are PHI-safe by
        ' design: no FHIR payloads on spans.
        '
        ' Transcripts land in
        '   %LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry\<yyyyMMdd>.jsonl
        ' one file per day, shared by every viewer in the process — open two report tabs
        ' and you will see two session.start records in the same file. Pass a
        ' FileTelemetryOptions to change the directory, retention or file size.
        TiroFormViewerDefaults.TelemetrySinkFactory =
            Function() New FileTelemetrySink(FileTelemetrySink.DefaultDirectory, New SentryTelemetrySink())

        ' Sentry only, no local file — the one-liner, if that is all you want:
        '   TiroFormFillerSentry.UseSentry()
        ' Your own DSN:
        '   TiroFormFillerSentry.UseSentry(dsn:="https://...")
        ' The file alone, for an air-gapped site with no Sentry package at all:
        '   TiroFormViewerDefaults.TelemetrySinkFactory = Function() New FileTelemetrySink()
        ' Nothing at all: leave TelemetrySinkFactory unset. That is the default.

        ' Where this run is writing, so the transcript is findable from the debugger
        ' without guessing at the name.
        Debug.WriteLine("[telemetry] transcripts: " & FileTelemetrySink.DefaultDirectory)

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New EhrShell())
    End Sub
End Module
