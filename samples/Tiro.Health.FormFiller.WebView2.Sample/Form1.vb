Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class Form1
    ' Flag that keeps track if form has been submitted
    Private isFormSubmitted As Boolean = False

    Public Sub New()
        InitializeComponent()
        TiroFormViewer.WebContentFolder = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "WebContent")
    End Sub

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication
        Await InitializeViewerAsync()
    End Sub

    Private Async Function InitializeViewerAsync() As System.Threading.Tasks.Task
        Dim patient As Patient = New Patient() With {
            .Name = New List(Of HumanName) From {
                New HumanName() With {
                    .Family = "da Vinci",
                    .Given = New List(Of String) From {"Leonardo"},
                    .Text = "Leonardo da Vinci2"
                }
            },
            .BirthDate = "1452-04-15",
            .Gender = AdministrativeGender.Male,
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {
                    .System = "http://test.org/test/patient-ids",
                    .Value = "test-123"
                }
            }
        }
        ' Hint: here it's possible to pass a previous QR as context
        Await TiroFormViewer.SetContextAsync("http://templates.tiro.health/templates/2630b8675c214707b1f86d1fbd4deb87", patient)
    End Function

    ' ----------------------------------------------------
    ' EVENT HANDLER FOR FORM SUBMISSION
    ' ----------------------------, it ------------------------
    Private Sub HandleFormSubmitted(ByVal sender As Object, ByVal e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))

        ' Check if there are validation errors
        If e.Outcome IsNot Nothing AndAlso e.Outcome.Success = False Then
            Dim result As DialogResult = MessageBox.Show("There are validation errors. Do you want to close anyway?", "Validation Errors", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If result = DialogResult.No Then
                Return
            End If
        End If

        ' The FormSubmittedEventArgs contains the submitted FHIR resource
        Dim response As QuestionnaireResponse = TryCast(e.Response, QuestionnaireResponse)

        If response IsNot Nothing Then

            ' Access the narrative property
            Dim narrativeHtml As String = response.Text?.Div

            ' Check if the narrative is present
            If Not String.IsNullOrEmpty(narrativeHtml) Then
                ' Display the narrative in a simple message box (or a rich text box)
                MessageBox.Show(narrativeHtml, "QuestionnaireResponse Narrative", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Else
                MessageBox.Show("Submitted QuestionnaireResponse has no narrative text.", "Submission Received")
            End If

        Else
            MessageBox.Show("Form submission received, but resource was not a QuestionnaireResponse.", "Error")
        End If

        ' Close the form after handling submission
        isFormSubmitted = True
        Me.Close()

    End Sub

    ' ----------------------------------------------------
    ' EVENT HANDLER FOR CLOSE APPLICATION (ui.done)
    ' ----------------------------------------------------
    Private Sub HandleCloseApplication(ByVal sender As Object, ByVal e As CloseApplicationEventArgs)
        isFormSubmitted = True
        MessageBox.Show("Closing.", "Closing")
        Me.Close()
    End Sub

    Private Async Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        If Not isFormSubmitted Then
            e.Cancel = True
            Await TiroFormViewer.SendFormRequestSubmitAsync()
        End If
    End Sub

End Class
