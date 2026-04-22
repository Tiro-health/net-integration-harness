<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class QuestionnaireForm
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
        TiroFormViewer = New Tiro.Health.FormFiller.WebView2.TiroFormViewer()
        SuspendLayout()
        '
        ' TiroFormViewer
        '
        TiroFormViewer.Dock = DockStyle.Fill
        TiroFormViewer.Location = New Point(0, 0)
        TiroFormViewer.Name = "TiroFormViewer"
        TiroFormViewer.Size = New Size(900, 600)
        TiroFormViewer.TabIndex = 0
        '
        ' QuestionnaireForm
        '
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(900, 600)
        Controls.Add(TiroFormViewer)
        Name = "QuestionnaireForm"
        StartPosition = FormStartPosition.CenterParent
        Text = "Questionnaire"
        ResumeLayout(False)
    End Sub

    Friend WithEvents TiroFormViewer As Tiro.Health.FormFiller.WebView2.TiroFormViewer
End Class
