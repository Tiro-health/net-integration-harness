<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class TemplatePickerDialog
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
        TemplatesList = New ListBox()
        OkButton = New Button()
        CancelDialogButton = New Button()
        SuspendLayout()
        '
        ' TitleLabel
        '
        TitleLabel.AutoSize = True
        TitleLabel.Font = New Font("Segoe UI Semibold", 9.0F, FontStyle.Bold)
        TitleLabel.Location = New Point(16, 16)
        TitleLabel.Name = "TitleLabel"
        TitleLabel.Text = "Pick a template for the new report:"
        '
        ' TemplatesList
        '
        TemplatesList.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        TemplatesList.IntegralHeight = False
        TemplatesList.ItemHeight = 15
        TemplatesList.Location = New Point(16, 44)
        TemplatesList.Name = "TemplatesList"
        TemplatesList.Size = New Size(420, 204)
        TemplatesList.TabIndex = 0
        '
        ' OkButton
        '
        OkButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        OkButton.DialogResult = DialogResult.OK
        OkButton.Location = New Point(276, 260)
        OkButton.Name = "OkButton"
        OkButton.Size = New Size(75, 28)
        OkButton.TabIndex = 1
        OkButton.Text = "OK"
        OkButton.UseVisualStyleBackColor = True
        '
        ' CancelDialogButton
        '
        CancelDialogButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        CancelDialogButton.DialogResult = DialogResult.Cancel
        CancelDialogButton.Location = New Point(361, 260)
        CancelDialogButton.Name = "CancelDialogButton"
        CancelDialogButton.Size = New Size(75, 28)
        CancelDialogButton.TabIndex = 2
        CancelDialogButton.Text = "Cancel"
        CancelDialogButton.UseVisualStyleBackColor = True
        '
        ' TemplatePickerDialog
        '
        AcceptButton = OkButton
        Me.CancelButton = CancelDialogButton
        AutoScaleDimensions = New SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(452, 304)
        Controls.Add(TitleLabel)
        Controls.Add(TemplatesList)
        Controls.Add(OkButton)
        Controls.Add(CancelDialogButton)
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        Name = "TemplatePickerDialog"
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterParent
        Text = "New report"
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents TitleLabel As Label
    Friend WithEvents TemplatesList As ListBox
    Friend WithEvents OkButton As Button
    Friend WithEvents CancelDialogButton As Button
End Class
