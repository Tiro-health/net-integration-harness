Imports Tiro.Health.FormFiller.WebView2
Imports Tiro.Health.FormFiller.WebView2.Sentry
Imports Tiro.Health.FormFiller.WebView2.Telemetry

Module Program
    <STAThread>
    Public Sub Main()
        ' Opt the whole app into telemetry, before any TiroFormViewerR5 is constructed —
        ' Designer-placed viewers pick this up automatically, viewers built earlier do not.
        ' Registers both sinks: a local JSONL transcript wrapped around Sentry, for sites
        ' whose network may not let Sentry out. Both are PHI-safe (no FHIR payloads).
        ' Transcripts: %LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry\<yyyyMMdd>.jsonl,
        ' one file per day shared by every viewer. Pass a FileTelemetryOptions to change
        ' the directory, retention or file size. See the README's telemetry section.
        '
        ' Other choices:  TiroFormFillerSentry.UseSentry()          ' Sentry only
        '                 TiroFormFillerSentry.UseSentry(dsn:="...") ' your own DSN
        '                 New FileTelemetrySink()                    ' file only, air-gapped
        '                 leave TelemetrySinkFactory unset           ' nothing (the default)
        TiroFormViewerDefaults.TelemetrySinkFactory =
            Function() New FileTelemetrySink(FileTelemetrySink.DefaultDirectory, New SentryTelemetrySink())

        Debug.WriteLine("[telemetry] transcripts: " & FileTelemetrySink.DefaultDirectory)

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New EhrShell())
    End Sub
End Module
