<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class LauncherForm
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
        TitleLabel = New Label()
        PatientList = New ListBox()
        OpenButton = New Button()
        StatusLabel = New Label()
        TimingLabel = New Label()
        NoteLabel = New Label()
        SuspendLayout()
        '
        ' TitleLabel
        '
        TitleLabel.AutoSize = True
        TitleLabel.Font = New Font("Segoe UI Semibold", 10.0F, FontStyle.Bold)
        TitleLabel.Location = New Point(16, 12)
        TitleLabel.Name = "TitleLabel"
        TitleLabel.Text = "Select a patient"
        '
        ' PatientList
        '
        PatientList.FormattingEnabled = True
        PatientList.ItemHeight = 15
        PatientList.Location = New Point(16, 40)
        PatientList.Name = "PatientList"
        PatientList.Size = New Size(320, 94)
        PatientList.TabIndex = 0
        '
        ' OpenButton
        '
        OpenButton.Location = New Point(16, 144)
        OpenButton.Name = "OpenButton"
        OpenButton.Size = New Size(160, 28)
        OpenButton.TabIndex = 1
        OpenButton.Text = "Open Questionnaire"
        '
        ' StatusLabel
        '
        StatusLabel.AutoSize = True
        StatusLabel.Location = New Point(16, 188)
        StatusLabel.Name = "StatusLabel"
        StatusLabel.Text = "Opens: 0"
        '
        ' TimingLabel
        '
        TimingLabel.AutoSize = True
        TimingLabel.ForeColor = Color.Gray
        TimingLabel.Location = New Point(16, 208)
        TimingLabel.Name = "TimingLabel"
        TimingLabel.Text = "Last load: (none)"
        '
        ' NoteLabel
        '
        NoteLabel.AutoSize = True
        NoteLabel.ForeColor = Color.Gray
        NoteLabel.Location = New Point(16, 236)
        NoteLabel.MaximumSize = New Size(320, 0)
        NoteLabel.Name = "NoteLabel"
        NoteLabel.Text = "First open pays the WebView2 cold-start cost. Subsequent opens attach to the already-warm environment and should be much faster."
        '
        ' LauncherForm
        '
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(352, 300)
        Controls.Add(TitleLabel)
        Controls.Add(PatientList)
        Controls.Add(OpenButton)
        Controls.Add(StatusLabel)
        Controls.Add(TimingLabel)
        Controls.Add(NoteLabel)
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        Name = "LauncherForm"
        StartPosition = FormStartPosition.CenterScreen
        Text = "Tiro Form Filler — Launcher"
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents TitleLabel As Label
    Friend WithEvents PatientList As ListBox
    Friend WithEvents OpenButton As Button
    Friend WithEvents StatusLabel As Label
    Friend WithEvents TimingLabel As Label
    Friend WithEvents NoteLabel As Label
End Class
