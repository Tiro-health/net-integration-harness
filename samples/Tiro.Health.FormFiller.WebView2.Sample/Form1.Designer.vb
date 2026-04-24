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
        TiroFormViewer = New Tiro.Health.FormFiller.WebView2.Fhir.R4.TiroFormViewerR4()
        SuspendLayout()
        ' 
        ' TiroFormViewer
        ' 
        TiroFormViewer.Dock = DockStyle.Fill
        TiroFormViewer.Location = New Point(0, 0)
        TiroFormViewer.Margin = New Padding(4, 3, 4, 3)
        TiroFormViewer.Name = "TiroFormViewer"
        TiroFormViewer.Size = New Size(800, 450)
        TiroFormViewer.TabIndex = 0
        ' 
        ' Form1
        ' 
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(800, 450)
        Controls.Add(TiroFormViewer)
        Name = "Form1"
        Text = "Form1"
        ResumeLayout(False)
    End Sub

    Friend WithEvents TiroFormViewer As Tiro.Health.FormFiller.WebView2.Fhir.R4.TiroFormViewerR4

End Class
