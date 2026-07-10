Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events
Imports Tiro.Health.FormSdk.Client
Imports Tiro.Health.FormSdk.Client.Fhir.R5

Public Class Form1

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication

        Dim patient As New Patient() With {
            .Name = New List(Of HumanName) From {
                New HumanName() With {
                    .Family = "da Vinci",
                    .Given = New List(Of String) From {"Leonardo"},
                    .Text = "Leonardo da Vinci"
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

        Await TiroFormViewer.SetContextAsync(
            "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699",
            patient)
    End Sub

    Private Async Sub SubmitButton_Click(sender As Object, e As EventArgs) Handles SubmitButton.Click
        Await TiroFormViewer.SendFormRequestSubmitAsync()
    End Sub

    Private Async Sub HandleFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        If e.Outcome IsNot Nothing AndAlso e.Outcome.Success = False Then
            Dim result As DialogResult = MessageBox.Show(
                "There are validation errors. Close anyway?",
                "Validation Errors",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning)
            If result = DialogResult.No Then Return
        End If

        ' Run the SDC $extract operation over the completed QuestionnaireResponse. Unlike the
        ' plain Sample (which just shows the QR narrative), $extract turns the response into a
        ' transaction Bundle of structured FHIR resources. For a template questionnaire the
        ' Bundle's primary resource is a Composition — the clinical document the form produced.
        '
        ' The client is constructed from the viewer's own SdcEndpointAddress, so the extract
        ' targets the same SDC server the form rendered against (deriving the address from the
        ' viewer is the convention that keeps them in sync — the API doesn't enforce it).
        Try
            Using client As New SdcClient(New Uri(TiroFormViewer.SdcEndpointAddress))
                Dim bundle As Bundle = Await client.ExtractAsync(e.Response)

                ' Pull the Composition (the extracted clinical document) out of the Bundle.
                Dim composition As Composition =
                    bundle.Entry.Select(Function(entry) entry.Resource).OfType(Of Composition)().FirstOrDefault()

                If composition IsNot Nothing Then
                    ' The readable narrative lives on the Composition's SECTIONS
                    ' (section[].Text.Div), not the top-level Composition.Text — so join the
                    ' section narratives. (Text.Div here is XHTML; a real host would render it
                    ' in a browser/RichTextBox rather than a plain MessageBox.)
                    Dim narrative As String = String.Join(
                        Environment.NewLine & Environment.NewLine,
                        composition.Section.
                            Select(Function(s) s.Text?.Div).
                            Where(Function(div) Not String.IsNullOrEmpty(div)))

                    If String.IsNullOrEmpty(narrative) Then narrative = composition.Text?.Div

                    Dim title As String = If(String.IsNullOrEmpty(composition.Title), "Extracted Composition", composition.Title)
                    MessageBox.Show(narrative, title, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    ' No Composition (e.g. a definition-based questionnaire extracts structured
                    ' resources like Observation instead). Fall back to what the Bundle contains.
                    Dim summary As String =
                        $"$extract produced a '{bundle.Type}' Bundle with {bundle.Entry.Count} entries, " &
                        "but no Composition to show a narrative for."
                    MessageBox.Show(summary, "Extract result", MessageBoxButtons.OK, MessageBoxIcon.Information)
                End If
            End Using
        Catch ex As SdcOperationException
            MessageBox.Show($"Extraction failed: {ex.Message}", "Extract error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try

        Me.Close()
    End Sub

    Private Sub HandleCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        Me.Close()
    End Sub

End Class
