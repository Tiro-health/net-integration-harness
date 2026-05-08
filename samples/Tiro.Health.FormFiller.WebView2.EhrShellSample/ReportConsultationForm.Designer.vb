<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class ReportConsultationForm
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
        TiroFormViewer = New Tiro.Health.FormFiller.WebView2.Fhir.R5.TiroFormViewerR5()
        ContextLabel = New Label()
        SuspendLayout()
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
        ContextLabel.Size = New Size(900, 32)
        ContextLabel.TabIndex = 0
        ContextLabel.Text = "(loading report...)"
        '
        ' TiroFormViewer
        '
        TiroFormViewer.Dock = DockStyle.Fill
        TiroFormViewer.Location = New Point(0, 32)
        TiroFormViewer.Name = "TiroFormViewer"
        TiroFormViewer.Size = New Size(900, 568)
        TiroFormViewer.TabIndex = 1
        '
        ' ReportConsultationForm
        '
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(900, 600)
        ' Add order: TiroFormViewer (Fill) first so it docks LAST and fills the
        ' leftover area below ContextLabel; ContextLabel (Top) added last so it
        ' docks FIRST and claims its 32px top strip.
        Controls.Add(TiroFormViewer)
        Controls.Add(ContextLabel)
        MinimumSize = New Size(700, 500)
        Name = "ReportConsultationForm"
        StartPosition = FormStartPosition.CenterParent
        Text = "Report — (loading...)"
        ResumeLayout(False)
    End Sub

    Friend WithEvents TiroFormViewer As Tiro.Health.FormFiller.WebView2.Fhir.R5.TiroFormViewerR5
    Friend WithEvents ContextLabel As Label
End Class
