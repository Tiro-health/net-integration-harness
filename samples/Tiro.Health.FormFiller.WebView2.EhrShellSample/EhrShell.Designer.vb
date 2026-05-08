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
        ReportsHeaderLabel = New Label()
        ReportsList = New ListBox()
        NewReportButton = New Button()
        PatientHeaderLabel = New Label()
        PatientDetailsLabel = New Label()
        FormTab = New TabPage()
        ContextLabel = New Label()
        FormFooterPanel = New Panel()
        SubmitFormButton = New Button()
        CloseSessionButton = New Button()
        TopStrip.SuspendLayout()
        LeftPanel.SuspendLayout()
        EncountersGroup.SuspendLayout()
        PatientsGroup.SuspendLayout()
        MainTabs.SuspendLayout()
        DetailsTab.SuspendLayout()
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
        ' and Encounters (bottom, fixed 240px). Add order: PatientsGroup first so it
        ' docks LAST and gets the leftover Fill height; EncountersGroup second so it
        ' docks FIRST and claims its 240px bottom strip.
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
        DetailsTab.Controls.Add(ReportsList)
        DetailsTab.Controls.Add(ReportsHeaderLabel)
        DetailsTab.Controls.Add(NewReportButton)
        DetailsTab.Controls.Add(PatientDetailsLabel)
        DetailsTab.Controls.Add(PatientHeaderLabel)
        DetailsTab.Location = New Point(4, 24)
        DetailsTab.Name = "DetailsTab"
        DetailsTab.Padding = New Padding(20)
        DetailsTab.Size = New Size(832, 670)
        DetailsTab.TabIndex = 0
        DetailsTab.Text = "Patient details"
        DetailsTab.UseVisualStyleBackColor = True
        '
        ' PatientHeaderLabel
        '
        PatientHeaderLabel.AutoSize = True
        PatientHeaderLabel.Font = New Font("Segoe UI Semibold", 12.0F, FontStyle.Bold)
        PatientHeaderLabel.Location = New Point(24, 24)
        PatientHeaderLabel.Name = "PatientHeaderLabel"
        PatientHeaderLabel.Text = "(no patient selected)"
        '
        ' PatientDetailsLabel
        '
        PatientDetailsLabel.AutoSize = True
        PatientDetailsLabel.ForeColor = Color.Gray
        PatientDetailsLabel.Location = New Point(24, 54)
        PatientDetailsLabel.Name = "PatientDetailsLabel"
        PatientDetailsLabel.Text = ""
        '
        ' ReportsHeaderLabel
        '
        ReportsHeaderLabel.AutoSize = True
        ReportsHeaderLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        ReportsHeaderLabel.Location = New Point(24, 100)
        ReportsHeaderLabel.Name = "ReportsHeaderLabel"
        ReportsHeaderLabel.Text = "Reports"
        '
        ' ReportsList
        '
        ReportsList.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        ReportsList.Font = New Font("Segoe UI", 9.0F)
        ReportsList.IntegralHeight = False
        ReportsList.ItemHeight = 18
        ReportsList.Location = New Point(24, 130)
        ReportsList.Name = "ReportsList"
        ReportsList.Size = New Size(784, 460)
        ReportsList.TabIndex = 0
        '
        ' NewReportButton
        '
        NewReportButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
        NewReportButton.Enabled = False
        NewReportButton.Location = New Point(24, 605)
        NewReportButton.Name = "NewReportButton"
        NewReportButton.Size = New Size(140, 30)
        NewReportButton.TabIndex = 1
        NewReportButton.Text = "+ New report"
        NewReportButton.UseVisualStyleBackColor = True
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
        SubmitFormButton.Location = New Point(735, 8)
        SubmitFormButton.Name = "SubmitFormButton"
        SubmitFormButton.Size = New Size(85, 30)
        SubmitFormButton.TabIndex = 1
        SubmitFormButton.Text = "Submit"
        SubmitFormButton.UseVisualStyleBackColor = True
        '
        ' CloseSessionButton
        '
        CloseSessionButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        CloseSessionButton.Location = New Point(549, 8)
        CloseSessionButton.Name = "CloseSessionButton"
        CloseSessionButton.Size = New Size(180, 30)
        CloseSessionButton.TabIndex = 0
        CloseSessionButton.Text = "Close session (dispose)"
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
        DetailsTab.PerformLayout()
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
    Friend WithEvents PatientHeaderLabel As Label
    Friend WithEvents PatientDetailsLabel As Label
    Friend WithEvents ReportsHeaderLabel As Label
    Friend WithEvents ReportsList As ListBox
    Friend WithEvents NewReportButton As Button
    Friend WithEvents FormTab As TabPage
    Friend WithEvents ContextLabel As Label
    Friend WithEvents FormFooterPanel As Panel
    Friend WithEvents SubmitFormButton As Button
    Friend WithEvents CloseSessionButton As Button
End Class
