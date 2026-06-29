Imports System.IO
Imports Hl7.Fhir.Model
Imports Tiro.Health.FormFiller.WebView2.Fhir.R5
Imports Tiro.Health.SmartWebMessaging.Events

Public Class EhrShell

    Private ReadOnly _practitioner As Practitioner = DemoData.Practitioner
    Private ReadOnly _patients As List(Of PatientRecord) = DemoData.Patients
    Private ReadOnly _templates As List(Of TemplateOption) = DemoData.Templates
    Private ReadOnly _store As New ResponseStore()

    ' Reports list backing data — keeps a parallel array so a row click maps
    ' back to a ResponseEntry without parsing the displayed string.
    Private _reportsBacking As List(Of ResponseEntry) = New List(Of ResponseEntry)()

    ' The viewer is created lazily on Launch and disposed on FormSubmitted /
    ' CloseApplication / explicit Close session. While alive, switching tabs only
    ' hides the WebView2 — JS keeps running, the bridge keeps routing, the
    ' SetContextAsync session stays valid.
    Private _viewer As TiroFormViewerR5

    ' Active session context — captured at LaunchSession time so we know what
    ' to save against when form.submitted fires (the user may have navigated
    ' the patient/encounter selectors mid-fill). _activeReportId identifies the
    ' report being written: a fresh id for a "+ New report", the existing id when
    ' reopening one for editing — so a new report never overwrites an old one.
    Private _activeReportId As String
    Private _activePatient As Patient
    Private _activeEncounter As Encounter
    Private _activeEncounterLabel As String
    Private _activeTemplate As TemplateOption

    Private Sub EhrShell_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        UserLabel.Text = $"Logged in as: {_practitioner.Name.First.Text}"
        For Each pr In _patients
            PatientList.Items.Add(pr.Patient.Name.First.Text)
        Next
        PatientList.SelectedIndex = 0
    End Sub

    ' ------------------------------------------------------------
    ' Selection plumbing (left pane)
    ' ------------------------------------------------------------

    Private Sub PatientList_SelectedIndexChanged(sender As Object, e As EventArgs) Handles PatientList.SelectedIndexChanged
        ReloadEncounters()
        UpdatePatientDetails()
        ReloadReports()
        UpdateNewReportButton()
    End Sub

    Private Sub EncounterList_SelectedIndexChanged(sender As Object, e As EventArgs) Handles EncounterList.SelectedIndexChanged
        UpdateNewReportButton()
    End Sub

    Private Sub ReloadEncounters()
        EncounterList.Items.Clear()
        Dim pr = SelectedPatientRecord()
        If pr Is Nothing Then Return
        For Each encRec In pr.Encounters
            EncounterList.Items.Add(encRec.Label)
        Next
        If EncounterList.Items.Count > 0 Then EncounterList.SelectedIndex = 0
    End Sub

    Private Sub UpdatePatientDetails()
        Dim pr = SelectedPatientRecord()
        If pr Is Nothing Then
            PatientHeaderLabel.Text = "(no patient selected)"
            PatientDetailsLabel.Text = ""
            Return
        End If
        Dim p = pr.Patient
        PatientHeaderLabel.Text = p.Name.First.Text
        PatientDetailsLabel.Text = $"Born {p.BirthDate}  ·  {p.Gender}"
    End Sub

    Private Sub ReloadReports()
        ReportsList.Items.Clear()
        _reportsBacking.Clear()
        NarrativePreviewBox.Clear()
        OpenReportButton.Enabled = False
        Dim pr = SelectedPatientRecord()
        If pr Is Nothing Then Return
        For Each entry In _store.GetReportsFor(pr.Patient)
            _reportsBacking.Add(entry)
            ReportsList.Items.Add(entry.DisplayLabel)
        Next
        If ReportsList.Items.Count = 0 Then
            ReportsList.Items.Add("(no reports yet — click '+ New report' to start one)")
            ReportsList.Enabled = False
        Else
            ReportsList.Enabled = True
        End If
    End Sub

    Private Sub ReportsList_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ReportsList.SelectedIndexChanged
        ' Single-click: render the selected report's narrative in the read-only
        ' preview pane. Works regardless of session state — peeking at an old
        ' report doesn't disturb a live form session in the Form tab.
        '
        ' Prefer the RTF narrative (richer formatting); fall back to the plain
        ' text alternative; show a placeholder if neither is available.
        If ReportsList.SelectedIndex < 0 OrElse ReportsList.SelectedIndex >= _reportsBacking.Count Then
            NarrativePreviewBox.Clear()
            OpenReportButton.Enabled = False
            Return
        End If

        OpenReportButton.Enabled = True

        Dim entry = _reportsBacking(ReportsList.SelectedIndex)
        Dim rtf = QuestionnaireResponseHelper.GetRtfNarrative(entry.Response)
        If Not String.IsNullOrEmpty(rtf) Then
            Try
                NarrativePreviewBox.Rtf = rtf
                ' Re-apply the zoom factor — assigning Rtf can reset it.
                NarrativePreviewBox.ZoomFactor = 0.75F
                Return
            Catch ex As ArgumentException
                ' Malformed RTF — fall through to the plain-text path.
            End Try
        End If

        Dim plain = QuestionnaireResponseHelper.GetPlainTextNarrative(entry.Response)
        NarrativePreviewBox.Text = If(String.IsNullOrEmpty(plain),
                                      "(no narrative available — the form may not have produced one)",
                                      plain)
    End Sub

    Private Sub UpdateNewReportButton()
        ' New report needs a patient + encounter selected and no live session.
        NewReportButton.Enabled =
            SelectedPatientRecord() IsNot Nothing AndAlso
            SelectedEncounterRecord() IsNot Nothing AndAlso
            _viewer Is Nothing
    End Sub

    ' ------------------------------------------------------------
    ' New report flow
    ' ------------------------------------------------------------

    Private Sub NewReportButton_Click(sender As Object, e As EventArgs) Handles NewReportButton.Click
        Dim pr = SelectedPatientRecord()
        Dim encRec = SelectedEncounterRecord()
        If pr Is Nothing OrElse encRec Is Nothing Then Return

        Using picker As New TemplatePickerDialog(_templates)
            If picker.ShowDialog(Me) <> DialogResult.OK OrElse picker.SelectedTemplate Is Nothing Then Return
            ' A new report gets a fresh id so it's always stored as a distinct report.
            LaunchSession(Guid.NewGuid().ToString(), pr.Patient, encRec.Encounter, encRec.Label,
                          picker.SelectedTemplate, initialResponse:=Nothing)
        End Using
    End Sub

    ' ------------------------------------------------------------
    ' Reopen-existing-report flow
    ' ------------------------------------------------------------

    Private Sub ReportsList_DoubleClick(sender As Object, e As EventArgs) Handles ReportsList.DoubleClick
        OpenSelectedReport()
    End Sub

    Private Sub OpenReportButton_Click(sender As Object, e As EventArgs) Handles OpenReportButton.Click
        OpenSelectedReport()
    End Sub

    Private Sub OpenSelectedReport()
        If ReportsList.SelectedIndex < 0 OrElse ReportsList.SelectedIndex >= _reportsBacking.Count Then Return
        Dim entry = _reportsBacking(ReportsList.SelectedIndex)

        ' Ask how to open the report. The same saved QR can be reopened two ways,
        ' each backed by a different WebContent page:
        '   Edit      → resume filling it in the main Form tab (editable page).
        '   Read-only → open it in a separate consultation window (view-only page),
        '               which leaves any live editing session untouched.
        Dim choice = MessageBox.Show(
            "Open this report in edit mode?" & vbCrLf & vbCrLf &
            "Yes — Edit: resume filling it in the main form." & vbCrLf &
            "No — Read-only: open it in a separate consultation window." & vbCrLf &
            "Cancel — don't open.",
            "Open report",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question)

        Select Case choice
            Case DialogResult.Yes
                EditReport(entry)
            Case DialogResult.No
                OpenReadOnly(entry)
        End Select
    End Sub

    Private Sub OpenReadOnly(entry As ResponseEntry)
        ' Spawn a separate top-level window with its own TiroFormViewerR5 so the
        ' main shell's in-progress session (if any) stays alive. The doctor can
        ' position the consultation window next to the EHR shell, read the
        ' previous report, then continue filling out the current one.
        Dim consultation As New ReportConsultationForm(entry, _practitioner)
        consultation.Show(Me)
    End Sub

    Private Sub EditReport(entry As ResponseEntry)
        ' Editing reuses the main shell's single Form tab. If a session is already
        ' live we'd orphan its viewer (and lose unsaved edits), so block until the
        ' current session is submitted, saved, or closed.
        If _viewer IsNot Nothing Then
            MessageBox.Show(
                "A form session is already open. Submit, save, or close it before editing another report.",
                "Session in progress", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        ' Relaunch with the saved QR as the initial response so the doctor picks up
        ' exactly where they left off. Reusing entry.ReportId means submitting
        ' updates this same report in place instead of creating a duplicate.
        LaunchSession(entry.ReportId, entry.Patient, entry.Encounter, entry.EncounterLabel, entry.Template,
                      initialResponse:=entry.Response)
    End Sub

    ' ------------------------------------------------------------
    ' Form session lifecycle
    ' ------------------------------------------------------------

    Private Async Sub LaunchSession(reportId As String, patient As Patient, encounter As Encounter, encounterLabel As String,
                                    template As TemplateOption, initialResponse As QuestionnaireResponse)
        _activeReportId = reportId
        _activePatient = patient
        _activeEncounter = encounter
        _activeEncounterLabel = encounterLabel
        _activeTemplate = template

        _viewer = New TiroFormViewerR5() With {.Dock = DockStyle.Fill}
        ' Point the viewer at WebContent\Form — the editable form page bundled
        ' with the sample. Each role gets its own page (Form for filling,
        ' Consultation for read-only viewing); the integrator picks which one
        ' by setting WebContentFolder, not by passing UI flags through the
        ' host API. Must be set before the handle is created (i.e. before the
        ' viewer is added to a Controls collection).
        _viewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent", "Form")
        AddHandler _viewer.FormSubmitted, AddressOf OnFormSubmitted
        AddHandler _viewer.CloseApplication, AddressOf OnCloseApplication

        ' Show the Form tab (it lives unparented when no session is alive) and
        ' set the context banner so the user sees what they're filling out.
        ContextLabel.Text = $"Filling: {template.Label}    ·    {patient.Name.First.Text}    ·    {encounterLabel}"
        FormTab.Controls.Add(_viewer)
        If Not MainTabs.TabPages.Contains(FormTab) Then MainTabs.TabPages.Add(FormTab)
        MainTabs.SelectedTab = FormTab

        SubmitFormButton.Enabled = True
        SaveDraftButton.Enabled = True
        CloseSessionButton.Enabled = True
        UpdateNewReportButton()

        Try
            Await _viewer.SetContextAsync(
                questionnaireCanonicalUrl:=template.CanonicalUrl,
                patient:=patient,
                encounter:=encounter,
                author:=_practitioner,
                initialResponse:=initialResponse)
        Catch ex As Exception
            ContextLabel.Text = $"Failed to load template: {ex.Message}"
            DisposeViewer()
            UpdateNewReportButton()
        End Try
    End Sub

    Private Async Sub SubmitFormButton_Click(sender As Object, e As EventArgs) Handles SubmitFormButton.Click
        If _viewer Is Nothing Then Return
        Await _viewer.SendFormRequestSubmitAsync()
    End Sub

    ' Save the form as a draft without finalizing. The "save-draft" intent maps to
    ' the frontend's submit({ status: "in-progress" }) (requires tiro-web-sdk >= 0.3.0;
    ' older versions ignore the option and finalize instead). The QR round-trips back
    ' through OnFormSubmitted with status In-progress, where we persist it but keep the
    ' session alive so the doctor can keep filling.
    Private Async Sub SaveDraftButton_Click(sender As Object, e As EventArgs) Handles SaveDraftButton.Click
        If _viewer Is Nothing Then Return
        Await _viewer.SendFormRequestSubmitAsync(intent:="save-draft")
    End Sub

    Private Sub CloseSessionButton_Click(sender As Object, e As EventArgs) Handles CloseSessionButton.Click
        If _viewer Is Nothing Then Return

        ' Closing tears the viewer down without persisting. Confirm first, since any
        ' edits made since the last Submit / Save in progress are lost.
        If MessageBox.Show(
            "Close this form without saving? Any unsaved changes will be lost.",
            "Close form", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return

        DisposeViewer()
        UpdateNewReportButton()
    End Sub

    Private Sub OnFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        If _activeReportId IsNot Nothing AndAlso _activePatient IsNot Nothing AndAlso _activeEncounter IsNot Nothing AndAlso _activeTemplate IsNot Nothing Then
            _store.SaveResponse(_activeReportId, _activePatient, _activeEncounter, _activeEncounterLabel, _activeTemplate, e.Response)
        End If

        ' A "Save in progress" round-trips a QR with status In-progress: persist it so
        ' the doctor can resume later (it shows up in the reports list, and relaunching
        ' the same patient/encounter/template reopens it), but keep the live session
        ' alive so they can carry on filling. A finalized Submit (status Completed)
        ' ends the session and tears the viewer down.
        Dim isDraft = e.Response IsNot Nothing AndAlso
                      e.Response.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress
        If isDraft Then
            ContextLabel.Text = $"Draft saved · {_activeTemplate.Label} · {_activePatient.Name.First.Text} · {_activeEncounterLabel}"
            ReloadReports()
            Return
        End If

        DisposeViewer()
        ReloadReports()
        UpdateNewReportButton()
    End Sub

    Private Sub OnCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        DisposeViewer()
        UpdateNewReportButton()
    End Sub

    Private Sub DisposeViewer()
        If _viewer Is Nothing Then Return
        FormTab.Controls.Remove(_viewer)
        _viewer.Dispose()
        _viewer = Nothing

        ' Hide the Form tab — it only exists while a session is alive.
        If MainTabs.TabPages.Contains(FormTab) Then MainTabs.TabPages.Remove(FormTab)
        MainTabs.SelectedTab = DetailsTab

        SubmitFormButton.Enabled = False
        SaveDraftButton.Enabled = False
        CloseSessionButton.Enabled = False
        _activeReportId = Nothing
        _activePatient = Nothing
        _activeEncounter = Nothing
        _activeEncounterLabel = Nothing
        _activeTemplate = Nothing
    End Sub

    ' ------------------------------------------------------------
    ' Selection accessors
    ' ------------------------------------------------------------

    Private Function SelectedPatientRecord() As PatientRecord
        If PatientList.SelectedIndex < 0 Then Return Nothing
        Return _patients(PatientList.SelectedIndex)
    End Function

    Private Function SelectedEncounterRecord() As EncounterRecord
        Dim pr = SelectedPatientRecord()
        If pr Is Nothing OrElse EncounterList.SelectedIndex < 0 Then Return Nothing
        Return pr.Encounters(EncounterList.SelectedIndex)
    End Function
End Class
