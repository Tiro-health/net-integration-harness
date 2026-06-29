Imports System.Diagnostics
Imports Hl7.Fhir.Model
Imports Hl7.Fhir.Serialization
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

        ' QuestionnaireResponse.Text.Div is the XHTML narrative the SDC backend
        ' generates. Plain-text and RTF alternatives live on QR.text via the
        ' http://fhir.tiro.health/StructureDefinition/narrative-alternative-format
        ' extension (an Attachment with ContentType "text/plain" or "text/rtf").
        ' See the EhrShellSample's QuestionnaireResponseHelper for how to read those.
        Dim narrativeHtml As String = e.Response.Text?.Div
        If Not String.IsNullOrEmpty(narrativeHtml) Then
            MessageBox.Show(narrativeHtml, "QuestionnaireResponse Narrative", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End If

        ' Demonstrate the SDC $extract operation: run the questionnaire's extraction
        ' over the completed QR to get the transaction Bundle of resources it produces
        ' (for a template questionnaire, a Composition; for definition-based ones,
        ' structured resources like Observation). The client is constructed from the
        ' viewer's own SdcEndpointAddress, so it targets the same SDC server the form
        ' rendered against (deriving the address from the viewer is what keeps them in
        ' sync — the API doesn't enforce it). Foreground: we await before closing.
        Try
            Using client As New SdcClient(New Uri(TiroFormViewer.SdcEndpointAddress))
                Dim bundle As Bundle = Await client.ExtractAsync(e.Response)

                ' Showcase the extracted Bundle by rendering it as pretty FHIR JSON.
                ' In a real EHR you would walk bundle.Entry and persist each resource;
                ' here we just serialize the whole transaction Bundle to the debug output
                ' so the demo shows exactly what $extract produced (Composition /
                ' Observation / Provenance / ...). Watch the Output window in Visual Studio.
                Dim json As String =
                    New FhirJsonSerializer(New SerializerSettings With {.Pretty = True}).SerializeToString(bundle)

                Debug.WriteLine($"$extract produced a '{bundle.Type}' Bundle with {bundle.Entry.Count} entries:")
                Debug.WriteLine(json)
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
