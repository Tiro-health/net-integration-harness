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
        ' Same page as the editable session (WebContent\Form) — the view-only
        ' rendering comes from the ReadOnly property, not from a second index.html.
        ' The bridge applies it to the <tiro-form-filler> element before the form
        ' initializes, so nothing here paints as editable first. Both are read at
        ' SetContextAsync, so setting them in the ctor is well before the deadline.
        TiroFormViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent", "Form")
        TiroFormViewer.ReadOnly = True
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
