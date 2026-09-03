Imports System.IO
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events
Imports Tiro.Health.SmartWebMessaging.Message.Payload
Imports Tiro.Health.FormSdk.Client
Imports Tiro.Health.FormSdk.Client.Fhir.R5
Imports Tiro.Health.FormFiller.WebView2

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
        ' That page also puts a <tiro-magic-clipboard> next to the form: paste or dictate clinical
        ' notes, hit Autofill, and the SDK runs SDC $populate to fill the answers in. It needs no host
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

        ' The right-click menu, host-side. The harness appends these to the embedded browser's
        ' own context menu; picking one puts text on the Windows clipboard and the clinician
        ' pastes it into whichever field they want with Ctrl+V. A real EHR would build this list
        ' from its own configuration — it's read fresh on every right-click, so items can be
        ' added, removed or relabelled per patient, and their text is resolved at click time
        ' (these close over `patient` and `conclusion`, not over a copy made now).
        Dim conclusion As String = "No evidence of atrial fibrillation on today's tracing."

        TiroFormViewer.ContextMenuItems.Add(
            TiroContextMenuItem.CopyToClipboard("Copy patient name", Function() patient.Name(0).Text))

        ' IsVisible filters per click: offering a conclusion over a read-only score or a
        ' checkbox is a dead end, so this one only shows over something typeable.
        Dim copyConclusion As TiroContextMenuItem =
            TiroContextMenuItem.CopyToClipboard("Copy conclusion", Function() conclusion)
        copyConclusion.IsVisible = Function(context) context.IsEditable
        TiroFormViewer.ContextMenuItems.Add(copyConclusion)

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

    ' The labelled clipboard, host-side: three snippets the shell offers, each carrying its
    ' text in the button's Tag (set in the designer). InsertTextAsync types the text into
    ' whichever form field holds the caret, so nothing here knows a thing about the
    ' questionnaire — no linkIds, no QuestionnaireResponse. The form stays the only writer of
    ' answers, which is what keeps validation, dirty-state and its own undo intact.
    '
    ' Clicking a Button takes keyboard focus off the WebView2. InsertTextAsync hands it back
    ' and the page re-focuses the field the caret was in, so the snippet lands there and the
    ' clinician's next keystroke continues after it — which is also why this works from a
    ' plain focusable Button and not just from a ToolStrip.
    Private Async Sub SnippetButton_Click(sender As Object, e As EventArgs) _
        Handles NormalExamButton.Click, NoAllergiesButton.Click, ConclusionButton.Click

        Dim snippet As String = TryCast(CType(sender, Button).Tag, String)
        If String.IsNullOrEmpty(snippet) Then Return

        ' False means there was nothing to type into: the clinician hasn't clicked into a
        ' field, or is standing in one that doesn't take free text (a checkbox, a date). Worth
        ' saying out loud — otherwise the button looks broken.
        Dim inserted As Boolean = Await TiroFormViewer.InsertTextAsync(snippet)

        SnippetStatusLabel.Text = If(inserted,
                                     "Inserted at the caret.",
                                     "Click in a text field first, then pick a snippet.")
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
