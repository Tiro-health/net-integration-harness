Imports System.IO
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events
Imports Tiro.Health.SmartWebMessaging.Message.Payload
Imports Tiro.Health.FormSdk.Client
Imports Tiro.Health.FormSdk.Client.Fhir.R5

Public Class Form1

    ' Single source of truth for the SDC server. Passed to BOTH the viewer (which runs
    ' $populate / $validate / $generate-narrative for the form) and the $extract client, so
    ' they always target the same server. Point this at your own SDC server for production;
    ' https://sdc.tiro.health/fhir/r5 is the shared demo instance (also the viewer's default).
    Private Const SdcEndpoint As String = "https://sdc-dev.tiro.health/fhir/r5"

    ' Set right before a program-initiated Me.Close() (from HandleFormSubmitted /
    ' HandleCloseApplication) so Form1_FormClosing's unsaved-changes prompt doesn't
    ' re-trigger on that same close.
    Private isClosingConfirmed As Boolean = False

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication

        ' Serve the form page bundled with this sample (WebContent\index.html) instead of the
        ' viewer's built-in assets — the light-blue page makes it obvious the host is supplying
        ' the HTML. WebContentFolder is read at the first SetContextAsync (when the viewer
        ' navigates), so setting it here in Form_Load — before that call — takes effect.
        '
        ' That page also puts a <tiro-magic-clipboard> next to the form: paste clinical notes,
        ' hit Autofill, and the SDK runs SDC $populate to fill the answers in. It needs no host
        ' wiring — the element links to the <tiro-form-filler> by id and borrows its SDC client,
        ' so it targets the SdcEndpoint set below like everything else. Submit is unchanged:
        ' the populated response still comes back through FormSubmitted and gets $extract-ed.
        TiroFormViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent")

        ' Point the viewer at the SDC server. Must be set BEFORE SetContextAsync (the bridge
        ' reads it once when the page is wired). The $extract client uses the same SdcEndpoint.
        TiroFormViewer.SdcEndpointAddress = SdcEndpoint

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

        ' Showcases passing an arbitrary named resource as launch context, alongside the
        ' well-known patient/encounter/author shorthand — here a Specimen, via the
        ' launchContext parameter. Purely illustrative: this sample form doesn't reference
        ' %specimen anywhere, so it has no effect on rendering or extraction.
        Dim specimen As New Specimen() With {
            .Id = "specimen-1",
            .Type = New CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0487", "TISS", "Tissue"),
            .Subject = New ResourceReference("Patient/test-123")
        }

        Await TiroFormViewer.SetContextAsync(
            "http://templates.tiro.health/templates/44ed83d0ee324811a170dd9b4098bb3a|1.2.7",
            patient:=patient,
            launchContext:=New List(Of LaunchContext(Of Resource)) From {
                New LaunchContext(Of Resource)("specimen", contentResource:=specimen)
            })
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
        ' Construct the client against the same SdcEndpoint the viewer used, so the extract
        ' targets the server the form rendered against.
        Try
            Using client As New SdcClient(New Uri(SdcEndpoint))
                Dim bundle As Bundle = Await client.ExtractAsync(e.Response)

                ' Pull the Composition (the extracted clinical document) out of the Bundle.
                Dim composition As Composition =
                    bundle.Entry.Select(Function(entry) entry.Resource).OfType(Of Composition)().FirstOrDefault()

                If composition IsNot Nothing Then
                    ' The readable narrative lives on the Composition's SECTIONS (section[].Text.Div))
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

        isClosingConfirmed = True
        Me.Close()
    End Sub

    Private Sub HandleCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        isClosingConfirmed = True
        Me.Close()
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If isClosingConfirmed Then Return

        If TiroFormViewer.IsDirty Then
            Dim result As DialogResult = MessageBox.Show(
                "You have unsaved changes. Close anyway?",
                "Unsaved changes",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning)
            If result = DialogResult.No Then
                e.Cancel = True
                Return
            End If
        End If

        isClosingConfirmed = True
    End Sub

End Class
