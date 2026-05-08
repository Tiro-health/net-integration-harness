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
    ' the patient/encounter selectors mid-fill).
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
            Return
        End If

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
            LaunchSession(pr.Patient, encRec, picker.SelectedTemplate, initialResponse:=Nothing)
        End Using
    End Sub

    ' ------------------------------------------------------------
    ' Reopen-existing-report flow
    ' ------------------------------------------------------------

    Private Sub ReportsList_DoubleClick(sender As Object, e As EventArgs) Handles ReportsList.DoubleClick
        OpenSelectedReport()
    End Sub

    Private Sub OpenSelectedReport()
        If _viewer IsNot Nothing Then Return  ' a session is already live
        If ReportsList.SelectedIndex < 0 OrElse ReportsList.SelectedIndex >= _reportsBacking.Count Then Return

        Dim entry = _reportsBacking(ReportsList.SelectedIndex)
        Dim encRec = New EncounterRecord(entry.Encounter, entry.EncounterLabel)
        LaunchSession(entry.Patient, encRec, entry.Template, initialResponse:=entry.Response)
    End Sub

    ' ------------------------------------------------------------
    ' Form session lifecycle
    ' ------------------------------------------------------------

    Private Async Sub LaunchSession(patient As Patient, encRec As EncounterRecord,
                                    template As TemplateOption, initialResponse As QuestionnaireResponse)
        _activePatient = patient
        _activeEncounter = encRec.Encounter
        _activeEncounterLabel = encRec.Label
        _activeTemplate = template

        _viewer = New TiroFormViewerR5() With {.Dock = DockStyle.Fill}
        ' Showcase the WebContentFolder seam: point the viewer at the bundled
        ' WebContent folder so it loads the sample's own index.html instead of
        ' the library's default banner page. Must be set before the handle is
        ' created — i.e. before the viewer is added to a Controls collection.
        _viewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent")
        AddHandler _viewer.FormSubmitted, AddressOf OnFormSubmitted
        AddHandler _viewer.CloseApplication, AddressOf OnCloseApplication

        ' Show the Form tab (it lives unparented when no session is alive) and
        ' set the context banner so the user sees what they're filling out.
        ContextLabel.Text = $"Filling: {template.Label}    ·    {patient.Name.First.Text}    ·    {encRec.Label}"
        FormTab.Controls.Add(_viewer)
        If Not MainTabs.TabPages.Contains(FormTab) Then MainTabs.TabPages.Add(FormTab)
        MainTabs.SelectedTab = FormTab

        SubmitFormButton.Enabled = True
        CloseSessionButton.Enabled = True
        UpdateNewReportButton()

        Try
            Await _viewer.SetContextAsync(
                questionnaireCanonicalUrl:=template.CanonicalUrl,
                patient:=patient,
                encounter:=encRec.Encounter,
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

    Private Sub CloseSessionButton_Click(sender As Object, e As EventArgs) Handles CloseSessionButton.Click
        DisposeViewer()
        UpdateNewReportButton()
    End Sub

    Private Sub OnFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        If _activePatient IsNot Nothing AndAlso _activeEncounter IsNot Nothing AndAlso _activeTemplate IsNot Nothing Then
            _store.SaveResponse(_activePatient, _activeEncounter, _activeEncounterLabel, _activeTemplate, e.Response)
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
        CloseSessionButton.Enabled = False
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
