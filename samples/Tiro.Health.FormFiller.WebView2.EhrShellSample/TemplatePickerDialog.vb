Public Class TemplatePickerDialog

    Public Property SelectedTemplate As TemplateOption

    Private ReadOnly _templates As List(Of TemplateOption)

    Public Sub New(templates As List(Of TemplateOption))
        InitializeComponent()
        _templates = templates
    End Sub

    Private Sub TemplatePickerDialog_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        For Each t In _templates
            TemplatesList.Items.Add(t.Label)
        Next
        If TemplatesList.Items.Count > 0 Then TemplatesList.SelectedIndex = 0
    End Sub

    Private Sub TemplatesList_SelectedIndexChanged(sender As Object, e As EventArgs) Handles TemplatesList.SelectedIndexChanged
        SelectedTemplate = If(TemplatesList.SelectedIndex >= 0, _templates(TemplatesList.SelectedIndex), Nothing)
    End Sub

    Private Sub TemplatesList_DoubleClick(sender As Object, e As EventArgs) Handles TemplatesList.DoubleClick
        If TemplatesList.SelectedIndex >= 0 Then
            DialogResult = DialogResult.OK
            Close()
        End If
    End Sub
End Class
