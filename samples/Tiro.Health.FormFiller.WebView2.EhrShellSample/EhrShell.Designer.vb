<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class EhrShell
    Inherits System.Windows.Forms.Form

    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        TopStrip = New StatusStrip()
        UserLabel = New ToolStripStatusLabel()
        LeftPanel = New Panel()
        EncountersGroup = New Panel()
        EncountersLabel = New Label()
        EncounterList = New ListBox()
        PatientsGroup = New Panel()
        PatientsLabel = New Label()
        PatientList = New ListBox()
        MainTabs = New TabControl()
        DetailsTab = New TabPage()
        ReportsPanel = New Panel()
        ReportsList = New ListBox()
        ReportsHeaderRow = New Panel()
        ReportsHeaderLabel = New Label()
        NewReportButton = New Button()
        PreviewPanel = New Panel()
        NarrativePreviewBox = New RichTextBox()
        NarrativePreviewLabel = New Label()
        OpenReportButton = New Button()
        HeaderPanel = New Panel()
        PatientHeaderLabel = New Label()
        PatientDetailsLabel = New Label()
        FormTab = New TabPage()
        ContextLabel = New Label()
        FormFooterPanel = New Panel()
        SubmitFormButton = New Button()
        SaveDraftButton = New Button()
        CloseSessionButton = New Button()
        TopStrip.SuspendLayout()
        LeftPanel.SuspendLayout()
        EncountersGroup.SuspendLayout()
        PatientsGroup.SuspendLayout()
        MainTabs.SuspendLayout()
        DetailsTab.SuspendLayout()
        ReportsPanel.SuspendLayout()
        ReportsHeaderRow.SuspendLayout()
        PreviewPanel.SuspendLayout()
        HeaderPanel.SuspendLayout()
        FormTab.SuspendLayout()
        FormFooterPanel.SuspendLayout()
        SuspendLayout()
        '
        ' TopStrip
        '
        TopStrip.Items.AddRange(New ToolStripItem() {UserLabel})
        TopStrip.Dock = DockStyle.Top
        TopStrip.Location = New Point(0, 0)
        TopStrip.Name = "TopStrip"
        TopStrip.SizingGrip = False
        TopStrip.Size = New Size(1100, 22)
        TopStrip.TabIndex = 0
        '
        ' UserLabel
        '
        UserLabel.Name = "UserLabel"
        UserLabel.Text = "Logged in as: (loading...)"
        '
        ' LeftPanel
        '
        ' Two stacked sections inside the left strip: Patients (top, fill remaining)
        ' and Encounters (bottom, fixed 240px). Add order matters for docking —
        ' PatientsGroup added first so it docks LAST (Fill); EncountersGroup
        ' added second so it docks FIRST (claims its 240px bottom strip).
        LeftPanel.Controls.Add(PatientsGroup)
        LeftPanel.Controls.Add(EncountersGroup)
        LeftPanel.Dock = DockStyle.Left
        LeftPanel.Location = New Point(0, 22)
        LeftPanel.Name = "LeftPanel"
        LeftPanel.Size = New Size(260, 698)
        LeftPanel.TabIndex = 1
        '
        ' EncountersGroup
        '
        EncountersGroup.Controls.Add(EncounterList)
        EncountersGroup.Controls.Add(EncountersLabel)
        EncountersGroup.Dock = DockStyle.Bottom
        EncountersGroup.Location = New Point(0, 458)
        EncountersGroup.Name = "EncountersGroup"
        EncountersGroup.Padding = New Padding(12, 8, 12, 12)
        EncountersGroup.Size = New Size(260, 240)
        EncountersGroup.TabIndex = 1
        '
        ' EncountersLabel
        '
        EncountersLabel.AutoSize = True
        EncountersLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        EncountersLabel.Location = New Point(12, 8)
        EncountersLabel.Name = "EncountersLabel"
        EncountersLabel.Text = "Encounters"
        '
        ' EncounterList
        '
        EncounterList.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        EncounterList.IntegralHeight = False
        EncounterList.ItemHeight = 15
        EncounterList.Location = New Point(12, 36)
        EncounterList.Name = "EncounterList"
        EncounterList.Size = New Size(236, 192)
        EncounterList.TabIndex = 0
        '
        ' PatientsGroup
        '
        PatientsGroup.Controls.Add(PatientList)
        PatientsGroup.Controls.Add(PatientsLabel)
        PatientsGroup.Dock = DockStyle.Fill
        PatientsGroup.Location = New Point(0, 0)
        PatientsGroup.Name = "PatientsGroup"
        PatientsGroup.Padding = New Padding(12, 12, 12, 8)
        PatientsGroup.Size = New Size(260, 458)
        PatientsGroup.TabIndex = 0
        '
        ' PatientsLabel
        '
        PatientsLabel.AutoSize = True
        PatientsLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        PatientsLabel.Location = New Point(12, 12)
        PatientsLabel.Name = "PatientsLabel"
        PatientsLabel.Text = "Patients"
        '
        ' PatientList
        '
        PatientList.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        PatientList.IntegralHeight = False
        PatientList.ItemHeight = 15
        PatientList.Location = New Point(12, 40)
        PatientList.Name = "PatientList"
        PatientList.Size = New Size(236, 410)
        PatientList.TabIndex = 0
        '
        ' MainTabs
        '
        ' Only the DetailsTab is added at design time; the FormTab is a sibling
        ' control that lives unparented until LaunchSession adds it to TabPages,
        ' and is removed again on session close. That way the Form tab only
        ' shows when a session is actually alive.
        MainTabs.Controls.Add(DetailsTab)
        MainTabs.Dock = DockStyle.Fill
        MainTabs.Location = New Point(260, 22)
        MainTabs.Name = "MainTabs"
        MainTabs.SelectedIndex = 0
        MainTabs.Size = New Size(840, 698)
        MainTabs.TabIndex = 2
        '
        ' DetailsTab
        '
        ' Three stacked Dock-based regions: HeaderPanel (Top), PreviewPanel
        ' (Bottom), ReportsPanel (Fill). Add order is reverse of the visual
        ' layering: Fill first (docks last, takes leftover middle), Bottom
        ' next (claims its strip), Top last (claims top strip first).
        DetailsTab.Controls.Add(ReportsPanel)
        DetailsTab.Controls.Add(PreviewPanel)
        DetailsTab.Controls.Add(HeaderPanel)
        DetailsTab.Location = New Point(4, 24)
        DetailsTab.Name = "DetailsTab"
        DetailsTab.Padding = New Padding(20)
        DetailsTab.Size = New Size(832, 670)
        DetailsTab.TabIndex = 0
        DetailsTab.Text = "Patient details"
        DetailsTab.UseVisualStyleBackColor = True
        '
        ' HeaderPanel
        '
        HeaderPanel.Controls.Add(PatientDetailsLabel)
        HeaderPanel.Controls.Add(PatientHeaderLabel)
        HeaderPanel.Dock = DockStyle.Top
        HeaderPanel.Location = New Point(20, 20)
        HeaderPanel.Name = "HeaderPanel"
        HeaderPanel.Size = New Size(792, 60)
        HeaderPanel.TabIndex = 0
        '
        ' PatientHeaderLabel
        '
        PatientHeaderLabel.AutoSize = True
        PatientHeaderLabel.Font = New Font("Segoe UI Semibold", 12.0F, FontStyle.Bold)
        PatientHeaderLabel.Location = New Point(4, 4)
        PatientHeaderLabel.Name = "PatientHeaderLabel"
        PatientHeaderLabel.Text = "(no patient selected)"
        '
        ' PatientDetailsLabel
        '
        PatientDetailsLabel.AutoSize = True
        PatientDetailsLabel.ForeColor = Color.Gray
        PatientDetailsLabel.Location = New Point(4, 32)
        PatientDetailsLabel.Name = "PatientDetailsLabel"
        PatientDetailsLabel.Text = ""
        '
        ' PreviewPanel
        '
        PreviewPanel.Controls.Add(NarrativePreviewBox)
        PreviewPanel.Controls.Add(OpenReportButton)
        PreviewPanel.Controls.Add(NarrativePreviewLabel)
        PreviewPanel.Dock = DockStyle.Bottom
        PreviewPanel.Location = New Point(20, 410)
        PreviewPanel.Name = "PreviewPanel"
        PreviewPanel.Padding = New Padding(0, 8, 0, 0)
        PreviewPanel.Size = New Size(792, 220)
        PreviewPanel.TabIndex = 1
        '
        ' NarrativePreviewLabel
        '
        NarrativePreviewLabel.AutoSize = True
        NarrativePreviewLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        NarrativePreviewLabel.Location = New Point(4, 12)
        NarrativePreviewLabel.Name = "NarrativePreviewLabel"
        NarrativePreviewLabel.Text = "Selected report — narrative"
        '
        ' OpenReportButton
        '
        ' Right-aligned to the preview header so it sits next to the narrative
        ' label. Enabled only when a real saved report is selected. Clicking
        ' prompts for edit vs read-only: edit resumes filling in the Form tab
        ' (blocked while another session is live), read-only opens a separate
        ' consultation window.
        OpenReportButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        OpenReportButton.Enabled = False
        OpenReportButton.Location = New Point(628, 6)
        OpenReportButton.Name = "OpenReportButton"
        OpenReportButton.Size = New Size(160, 28)
        OpenReportButton.TabIndex = 1
        OpenReportButton.Text = "Open this report"
        OpenReportButton.UseVisualStyleBackColor = True
        '
        ' NarrativePreviewBox
        '
        ' RichTextBox so we can render the RTF narrative (when present) via the
        ' Rtf property; plain text is also supported via the Text property when
        ' RTF isn't available. ReadOnly + DetectUrls=False to keep it as a pure
        ' renderer (no editing, no URL auto-linking surprises).
        NarrativePreviewBox.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        NarrativePreviewBox.BackColor = Color.FromArgb(248, 250, 252)
        NarrativePreviewBox.BorderStyle = BorderStyle.FixedSingle
        NarrativePreviewBox.DetectUrls = False
        NarrativePreviewBox.Font = New Font("Segoe UI", 9.0F)
        NarrativePreviewBox.Location = New Point(4, 36)
        NarrativePreviewBox.Name = "NarrativePreviewBox"
        NarrativePreviewBox.ReadOnly = True
        NarrativePreviewBox.ScrollBars = RichTextBoxScrollBars.Vertical
        NarrativePreviewBox.Size = New Size(784, 180)
        NarrativePreviewBox.TabIndex = 0
        NarrativePreviewBox.WordWrap = True
        ' RTF carries its own font sizes (typically authored at 16–20pt for print)
        ' so the control's Font property is ignored. Scale everything down uniformly
        ' so a tighter on-screen rendering doesn't dominate the preview pane.
        NarrativePreviewBox.ZoomFactor = 0.75F
        '
        ' ReportsPanel
        '
        ReportsPanel.Controls.Add(ReportsList)
        ReportsPanel.Controls.Add(ReportsHeaderRow)
        ReportsPanel.Dock = DockStyle.Fill
        ReportsPanel.Location = New Point(20, 80)
        ReportsPanel.Name = "ReportsPanel"
        ReportsPanel.Padding = New Padding(0, 8, 0, 8)
        ReportsPanel.Size = New Size(792, 330)
        ReportsPanel.TabIndex = 2
        '
        ' ReportsHeaderRow
        '
        ' Top strip with Reports label on the left and "+ New report" button on
        ' the right. Always visible regardless of how the Reports list is sized.
        ReportsHeaderRow.Controls.Add(NewReportButton)
        ReportsHeaderRow.Controls.Add(ReportsHeaderLabel)
        ReportsHeaderRow.Dock = DockStyle.Top
        ReportsHeaderRow.Location = New Point(0, 8)
        ReportsHeaderRow.Name = "ReportsHeaderRow"
        ReportsHeaderRow.Size = New Size(792, 40)
        ReportsHeaderRow.TabIndex = 0
        '
        ' ReportsHeaderLabel
        '
        ReportsHeaderLabel.AutoSize = True
        ReportsHeaderLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        ReportsHeaderLabel.Location = New Point(4, 10)
        ReportsHeaderLabel.Name = "ReportsHeaderLabel"
        ReportsHeaderLabel.Text = "Reports"
        '
        ' NewReportButton
        '
        NewReportButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        NewReportButton.Enabled = False
        NewReportButton.Location = New Point(648, 5)
        NewReportButton.Name = "NewReportButton"
        NewReportButton.Size = New Size(140, 30)
        NewReportButton.TabIndex = 0
        NewReportButton.Text = "+ New report"
        NewReportButton.UseVisualStyleBackColor = True
        '
        ' ReportsList
        '
        ReportsList.Dock = DockStyle.Fill
        ReportsList.Font = New Font("Segoe UI", 9.0F)
        ReportsList.IntegralHeight = False
        ReportsList.ItemHeight = 18
        ReportsList.Location = New Point(0, 48)
        ReportsList.Name = "ReportsList"
        ReportsList.Size = New Size(792, 274)
        ReportsList.TabIndex = 1
        '
        ' FormTab
        '
        FormTab.Controls.Add(FormFooterPanel)
        FormTab.Controls.Add(ContextLabel)
        FormTab.Location = New Point(4, 24)
        FormTab.Name = "FormTab"
        FormTab.Size = New Size(832, 670)
        FormTab.TabIndex = 1
        FormTab.Text = "Form"
        FormTab.UseVisualStyleBackColor = True
        '
        ' ContextLabel
        '
        ContextLabel.BackColor = Color.FromArgb(241, 245, 249)
        ContextLabel.BorderStyle = BorderStyle.FixedSingle
        ContextLabel.Dock = DockStyle.Top
        ContextLabel.Font = New Font("Segoe UI Semibold", 9.0F, FontStyle.Bold)
        ContextLabel.Location = New Point(0, 0)
        ContextLabel.Name = "ContextLabel"
        ContextLabel.Padding = New Padding(12, 8, 12, 8)
        ContextLabel.Size = New Size(832, 32)
        ContextLabel.Text = "(filling out a form...)"
        ContextLabel.TabIndex = 0
        '
        ' FormFooterPanel
        '
        ' Dock=Bottom claims a 46px footer; the TiroFormViewer added at runtime
        ' with Dock=Fill lands above it (and below ContextLabel).
        FormFooterPanel.Controls.Add(SubmitFormButton)
        FormFooterPanel.Controls.Add(SaveDraftButton)
        FormFooterPanel.Controls.Add(CloseSessionButton)
        FormFooterPanel.Dock = DockStyle.Bottom
        FormFooterPanel.Location = New Point(0, 624)
        FormFooterPanel.Name = "FormFooterPanel"
        FormFooterPanel.Size = New Size(832, 46)
        FormFooterPanel.TabIndex = 1
        '
        ' SubmitFormButton
        '
        SubmitFormButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        SubmitFormButton.Location = New Point(690, 8)
        SubmitFormButton.Name = "SubmitFormButton"
        SubmitFormButton.Size = New Size(130, 30)
        SubmitFormButton.TabIndex = 2
        SubmitFormButton.Text = "Submit"
        SubmitFormButton.UseVisualStyleBackColor = True
        '
        ' SaveDraftButton
        '
        SaveDraftButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        SaveDraftButton.Location = New Point(552, 8)
        SaveDraftButton.Name = "SaveDraftButton"
        SaveDraftButton.Size = New Size(130, 30)
        SaveDraftButton.TabIndex = 1
        SaveDraftButton.Text = "Save in progress"
        SaveDraftButton.UseVisualStyleBackColor = True
        '
        ' CloseSessionButton
        '
        CloseSessionButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        CloseSessionButton.Location = New Point(414, 8)
        CloseSessionButton.Name = "CloseSessionButton"
        CloseSessionButton.Size = New Size(130, 30)
        CloseSessionButton.TabIndex = 0
        CloseSessionButton.Text = "Close"
        CloseSessionButton.UseVisualStyleBackColor = True
        '
        ' EhrShell
        '
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(1100, 720)
        MinimumSize = New Size(900, 600)
        Controls.Add(MainTabs)
        Controls.Add(LeftPanel)
        Controls.Add(TopStrip)
        Name = "EhrShell"
        StartPosition = FormStartPosition.CenterScreen
        Text = "Tiro Form Filler — EHR Shell sample"
        TopStrip.ResumeLayout(False)
        TopStrip.PerformLayout()
        LeftPanel.ResumeLayout(False)
        EncountersGroup.ResumeLayout(False)
        EncountersGroup.PerformLayout()
        PatientsGroup.ResumeLayout(False)
        PatientsGroup.PerformLayout()
        MainTabs.ResumeLayout(False)
        DetailsTab.ResumeLayout(False)
        ReportsPanel.ResumeLayout(False)
        ReportsHeaderRow.ResumeLayout(False)
        ReportsHeaderRow.PerformLayout()
        PreviewPanel.ResumeLayout(False)
        PreviewPanel.PerformLayout()
        HeaderPanel.ResumeLayout(False)
        HeaderPanel.PerformLayout()
        FormTab.ResumeLayout(False)
        FormFooterPanel.ResumeLayout(False)
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents TopStrip As StatusStrip
    Friend WithEvents UserLabel As ToolStripStatusLabel
    Friend WithEvents LeftPanel As Panel
    Friend WithEvents EncountersGroup As Panel
    Friend WithEvents EncountersLabel As Label
    Friend WithEvents EncounterList As ListBox
    Friend WithEvents PatientsGroup As Panel
    Friend WithEvents PatientsLabel As Label
    Friend WithEvents PatientList As ListBox
    Friend WithEvents MainTabs As TabControl
    Friend WithEvents DetailsTab As TabPage
    Friend WithEvents HeaderPanel As Panel
    Friend WithEvents PatientHeaderLabel As Label
    Friend WithEvents PatientDetailsLabel As Label
    Friend WithEvents ReportsPanel As Panel
    Friend WithEvents ReportsHeaderRow As Panel
    Friend WithEvents ReportsHeaderLabel As Label
    Friend WithEvents NewReportButton As Button
    Friend WithEvents ReportsList As ListBox
    Friend WithEvents PreviewPanel As Panel
    Friend WithEvents NarrativePreviewLabel As Label
    Friend WithEvents OpenReportButton As Button
    Friend WithEvents NarrativePreviewBox As RichTextBox
    Friend WithEvents FormTab As TabPage
    Friend WithEvents ContextLabel As Label
    Friend WithEvents FormFooterPanel As Panel
    Friend WithEvents SubmitFormButton As Button
    Friend WithEvents SaveDraftButton As Button
    Friend WithEvents CloseSessionButton As Button
End Class
