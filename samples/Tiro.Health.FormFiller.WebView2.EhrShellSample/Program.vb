Imports Tiro.Health.FormFiller.WebView2.Sentry

Module Program
    <STAThread>
    Public Sub Main()
        ' Opt the whole app into Sentry telemetry — one line, before any TiroFormViewerR5
        ' is constructed. Designer-placed viewers in EhrShell/ReportConsultationForm pick
        ' this up automatically. The zero-arg call uses Tiro's hosted DSNs (PHI-safe by
        ' design: no FHIR payloads on spans). Comment out to ship without telemetry, or
        ' pass your own DSN: TiroFormFillerSentry.UseSentry(dsn:="https://...").
        TiroFormFillerSentry.UseSentry()

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New EhrShell())
    End Sub
End Module
