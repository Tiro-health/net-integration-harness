Imports System.IO
Imports Hl7.Fhir.Model
Imports Tiro.Health.FormFiller.WebView2.Fhir.R5

''' <summary>
''' Modeless top-level window that hosts its own TiroFormViewerR5 to display a
''' previously-submitted report. Opens with the saved QuestionnaireResponse as
''' the initial response so the doctor sees the same answers they recorded.
''' Lives independently of the main shell — closing this window doesn't
''' affect the in-progress form session in the EHR shell, and vice versa.
''' </summary>
Public Class ReportConsultationForm

    Private ReadOnly _entry As ResponseEntry
    Private ReadOnly _practitioner As Practitioner

    Public Sub New(entry As ResponseEntry, practitioner As Practitioner)
        InitializeComponent()
        _entry = entry
        _practitioner = practitioner
        ' Point WebContentFolder at the read-only "Consultation" page — its
        ' index.html bakes the <tiro-form-filler read-only> attribute into the
        ' element so the rendered form is view-only. Must be set before the
        ' WebView2 handle is created (i.e. before the form is shown).
        TiroFormViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent", "Consultation")
    End Sub

    Private Async Sub ReportConsultationForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ContextLabel.Text = $"Consulting: {_entry.Template.Label}    ·    {_entry.Patient.Name.First.Text}    ·    {_entry.EncounterLabel}    ·    saved {_entry.SavedAt:yyyy-MM-dd HH:mm}"
        Text = $"Report — {_entry.Template.Label}"

        Try
            Await TiroFormViewer.SetContextAsync(
                questionnaireCanonicalUrl:=_entry.Template.CanonicalUrl,
                patient:=_entry.Patient,
                encounter:=_entry.Encounter,
                author:=_practitioner,
                initialResponse:=_entry.Response)
        Catch ex As Exception
            ContextLabel.Text = $"Failed to load: {ex.Message}"
        End Try
    End Sub
End Class
