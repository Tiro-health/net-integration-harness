<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
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

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        TiroFormViewer = New Tiro.Health.FormFiller.WebView2.Fhir.R5.TiroFormViewerR5()
        BottomPanel = New Panel()
        SubmitButton = New Button()
        SnippetLabel = New Label()
        NormalExamButton = New Button()
        NoAllergiesButton = New Button()
        ConclusionButton = New Button()
        SnippetStatusLabel = New Label()
        BottomPanel.SuspendLayout()
        SuspendLayout()
        '
        ' TiroFormViewer
        '
        TiroFormViewer.Dock = DockStyle.Fill
        TiroFormViewer.Location = New Point(0, 0)
        TiroFormViewer.Margin = New Padding(4, 3, 4, 3)
        TiroFormViewer.Name = "TiroFormViewer"
        TiroFormViewer.Size = New Size(800, 404)
        TiroFormViewer.TabIndex = 0
        '
        ' SubmitButton
        '
        SubmitButton.Anchor = CType(AnchorStyles.Top Or AnchorStyles.Right, AnchorStyles)
        SubmitButton.Location = New Point(703, 8)
        SubmitButton.Name = "SubmitButton"
        SubmitButton.Size = New Size(85, 30)
        SubmitButton.TabIndex = 0
        SubmitButton.Text = "Submit"
        SubmitButton.UseVisualStyleBackColor = True
        '
        ' SnippetLabel
        '
        SnippetLabel.AutoSize = True
        SnippetLabel.Location = New Point(8, 16)
        SnippetLabel.Name = "SnippetLabel"
        SnippetLabel.Size = New Size(62, 15)
        SnippetLabel.TabIndex = 1
        SnippetLabel.Text = "Snippets:"
        '
        ' NormalExamButton
        '
        NormalExamButton.Location = New Point(76, 8)
        NormalExamButton.Name = "NormalExamButton"
        NormalExamButton.Size = New Size(104, 30)
        NormalExamButton.TabIndex = 2
        NormalExamButton.Tag = "No abnormalities on inspection or palpation. "
        NormalExamButton.Text = "Normal exam"
        NormalExamButton.UseVisualStyleBackColor = True
        '
        ' NoAllergiesButton
        '
        NoAllergiesButton.Location = New Point(186, 8)
        NoAllergiesButton.Name = "NoAllergiesButton"
        NoAllergiesButton.Size = New Size(104, 30)
        NoAllergiesButton.TabIndex = 3
        NoAllergiesButton.Tag = "No known drug allergies. "
        NoAllergiesButton.Text = "No allergies"
        NoAllergiesButton.UseVisualStyleBackColor = True
        '
        ' ConclusionButton
        '
        ConclusionButton.Location = New Point(296, 8)
        ConclusionButton.Name = "ConclusionButton"
        ConclusionButton.Size = New Size(104, 30)
        ConclusionButton.TabIndex = 4
        ConclusionButton.Tag = "Findings consistent with the clinical picture; no further imaging indicated. "
        ConclusionButton.Text = "Conclusion"
        ConclusionButton.UseVisualStyleBackColor = True
        '
        ' SnippetStatusLabel
        '
        SnippetStatusLabel.AutoSize = False
        SnippetStatusLabel.Location = New Point(406, 16)
        SnippetStatusLabel.Name = "SnippetStatusLabel"
        SnippetStatusLabel.Size = New Size(290, 22)
        SnippetStatusLabel.TabIndex = 5
        SnippetStatusLabel.Text = ""
        '
        ' BottomPanel
        '
        BottomPanel.Controls.Add(SnippetLabel)
        BottomPanel.Controls.Add(NormalExamButton)
        BottomPanel.Controls.Add(NoAllergiesButton)
        BottomPanel.Controls.Add(ConclusionButton)
        BottomPanel.Controls.Add(SnippetStatusLabel)
        BottomPanel.Controls.Add(SubmitButton)
        BottomPanel.Dock = DockStyle.Bottom
        BottomPanel.Location = New Point(0, 404)
        BottomPanel.Name = "BottomPanel"
        BottomPanel.Size = New Size(800, 46)
        BottomPanel.TabIndex = 1
        '
        ' Form1
        '
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(800, 450)
        Controls.Add(TiroFormViewer)
        Controls.Add(BottomPanel)
        Name = "Form1"
        Text = "Form1"
        BottomPanel.ResumeLayout(False)
        ResumeLayout(False)
    End Sub

    Friend WithEvents TiroFormViewer As Tiro.Health.FormFiller.WebView2.Fhir.R5.TiroFormViewerR5
    Friend WithEvents BottomPanel As Panel
    Friend WithEvents SubmitButton As Button
    Friend WithEvents SnippetLabel As Label
    Friend WithEvents NormalExamButton As Button
    Friend WithEvents NoAllergiesButton As Button
    Friend WithEvents ConclusionButton As Button
    Friend WithEvents SnippetStatusLabel As Label

End Class
