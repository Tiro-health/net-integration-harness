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
    Private Const SdcEndpoint As String = "https://sdc.tiro.health/fhir/r5"

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
        ' own context menu, below its native entries. Because it is the browser's own menu, the
        ' click never leaves the page: the caret stays exactly where the clinician
        ' right-clicked, which is what lets these insert there.
        '
        ' A real EHR would build this list from its own configuration: it's read fresh on every
        ' right-click, and each item's value is resolved when it's picked (these close over
        ' `patient` and the constants below rather than over a copy made now), so items can be
        ' added, removed or relabelled per patient without touching the harness.
        '
        ' Nothing here touches the Windows clipboard. The content goes straight into the field
        ' that was right-clicked, so the clinician needs one click instead of a copy and a
        ' Ctrl+V, whatever they had copied is left alone, and no patient text is ever put on a
        ' machine-wide surface.
        '
        ' IsVisible = IsEditable on every item: over a checkbox or a read-only score there is
        ' nothing to insert into, so the item stays out of the menu rather than doing nothing.
        TiroFormViewer.AddInsertItem("Insert patient name",
                                     Function() patient.Name(0).Text,
                                     onResult:=AddressOf ShowInsertResult)

        TiroFormViewer.AddInsertItem("Insert ""no known drug allergies""",
                                     Function() "No known drug allergies.",
                                     onResult:=AddressOf ShowInsertResult)

        ' The conclusion the EHR holds as RTF, flattened by the RTF parser WinForms already
        ' contains. Goes into any field; formatting dropped.
        TiroFormViewer.AddInsertItem("Insert conclusion (plain text)",
                                     Function() TiroRtf.ToPlainText(ConclusionRtf),
                                     onResult:=AddressOf ShowInsertResult)

        ' The same conclusion, keeping its formatting. Both renditions come from the harness:
        ' ToPlainText uses the RTF parser WinForms already has, ToHtml is the harness's own
        ' converter. The page offers the HTML to the field first and falls back to the plain text
        ' if the field won't take it; onResult says which happened, so what a given field can
        ' actually store is visible.
        '
        ' ToHtml is a convenience, not a full-fidelity converter — it keeps what the field can
        ' store (emphasis, paragraphs) and flattens the rest. For RTF from arbitrary sources,
        ' pass your own converter's output here instead; nothing about it is mandatory.
        TiroFormViewer.AddInsertItem("Insert conclusion (formatted)",
                                     Function() TiroRtf.ToPlainText(ConclusionRtf),
                                     Function() TiroRtf.ToHtml(ConclusionRtf),
                                     onResult:=AddressOf ShowInsertResult)

        ' Showcases passing an arbitrary named resource as launch context, alongside the
        ' well-known patient/encounter/author shorthand — here a Specimen, via the
        ' launchContext parameter. Purely illustrative: this sample form doesn't reference
        ' %specimen anywhere, so it has no effect on rendering or extraction.
        Dim specimen As New Specimen() With {
            .Id = "specimen-1",
            .Type = New CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0487", "TISS", "Tissue"),
            .Subject = New ResourceReference("Patient/test-123")
        }

        ' A pathology report with Macroscopie / Microscopie / Conclusie sections. Its Composition
        ' blueprint (authored with the template) is what makes $extract return one Composition
        ' section per report section — see HandleFormSubmitted.
        Await TiroFormViewer.SetContextAsync(
            "http://templates.tiro.health/templates/44ed83d0ee324811a170dd9b4098bb3a|2.0.2",
            patient:=patient,
            launchContext:=New List(Of LaunchContext(Of Resource)) From {
                New LaunchContext(Of Resource)("specimen", contentResource:=specimen)
            })
    End Sub

    ''' <summary>
    ''' Asks the form to submit. The button does not submit anything itself — it requests it, and
    ''' the page decides: it validates, and only a form that passes comes back through
    ''' <see cref="HandleFormSubmitted"/> with the completed QuestionnaireResponse. That is why
    ''' there is no result to inspect here.
    ''' </summary>
    Private Async Sub SubmitButton_Click(sender As Object, e As EventArgs) Handles SubmitButton.Click
        Await TiroFormViewer.SendFormRequestSubmitAsync()
    End Sub

    ''' <summary>
    ''' Shows what the page managed, in the window title, so the outcome is visible without a
    ''' debugger. The harness calls this on the UI thread, so touching controls is safe.
    ''' </summary>
    Private Sub ShowInsertResult(result As TextInsertResult)
        Dim summary As String
        If Not result.Inserted Then
            summary = "nothing inserted — click in a text field first"
        ElseIf result.KeptFormatting Then
            summary = "inserted WITH formatting (mode=Html)"
        Else
            summary = "inserted as plain text (mode=Text) — the field would not take the HTML"
        End If
        Me.Text = "Extract sample — " & summary
    End Sub

    ''' <summary>
    ''' The conclusion as the EHR holds it: RTF. Real integrations read this from their own
    ''' store — the constant stands in for that, so the menu items show the shape a real
    ''' integration takes rather than starting from HTML nobody would have.
    ''' </summary>
    Private Const ConclusionRtf As String =
        "{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22" &
        "{\b Assessment.} Findings consistent with the clinical picture; " &
        "{\i no further imaging indicated}.\par}"

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
                    ' The readable text lives on the Composition's SECTIONS, one per report
                    ' section (Macroscopie, Microscopie, Conclusie): Section.Title is the heading,
                    ' Section.Text.Div the XHTML the template rendered from the answers. The server
                    ' does not set Composition.Text, so there is no whole-document div to read.
                    '
                    ' Keeping the sections apart is the point of extracting rather than reading
                    ' QuestionnaireResponse.Text (which has the same content, already joined): an
                    ' EHR can file each section into its own field. Here they are just listed.
                    Dim report As String = String.Join(
                        Environment.NewLine & Environment.NewLine,
                        composition.Section.Select(
                            Function(s) If(s.Title, "(untitled section)").ToUpperInvariant() & Environment.NewLine & XhtmlToText(s.Text?.Div, s.Title)))

                    Dim title As String = If(String.IsNullOrEmpty(composition.Title), "Extracted Composition", composition.Title)
                    MessageBox.Show(report, title, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    ' No Composition: the template version has no Composition blueprint, so there is
                    ' nothing to build one from (a definition-based questionnaire extracts structured
                    ' resources like Observation instead). Fall back to what the Bundle contains.
                    Dim summary As String =
                        $"$extract produced a '{bundle.Type}' Bundle with {bundle.Entry.Count} entries, " &
                        "but no Composition. Does this template version have a Composition blueprint?"
                    MessageBox.Show(summary, "Extract result", MessageBoxButtons.OK, MessageBoxIcon.Information)
                End If
            End Using
        Catch ex As SdcOperationException
            MessageBox.Show($"Extraction failed: {ex.Message}", "Extract error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try

        isClosingConfirmed = True
        Me.Close()
    End Sub

    ''' <summary>
    ''' Flattens a section's XHTML narrative to text for the MessageBox: a line per paragraph,
    ''' heading or list item. A real EHR would convert to its own format (RTF, its editor's HTML)
    ''' instead; this only keeps the demo readable. Generated sections open with their own title
    ''' as a heading; that line is dropped, since the caller already prints the title.
    ''' </summary>
    Private Shared Function XhtmlToText(div As String, sectionTitle As String) As String
        If String.IsNullOrEmpty(div) Then Return "(empty)"
        Dim lines As New List(Of String)
        Dim current As New System.Text.StringBuilder()
        Try
            AppendBlocks(System.Xml.Linq.XElement.Parse(div), lines, current)
        Catch ex As System.Xml.XmlException
            Return div ' Not well-formed: show it as received rather than lose the section.
        End Try
        EndLine(lines, current)
        If lines.Count > 0 AndAlso String.Equals(lines(0), sectionTitle, StringComparison.OrdinalIgnoreCase) Then lines.RemoveAt(0)
        Return If(lines.Count = 0, "(empty)", String.Join(Environment.NewLine, lines))
    End Function

    Private Shared ReadOnly BlockElements As New HashSet(Of String) From {
        "p", "div", "li", "br", "tr", "h1", "h2", "h3", "h4", "h5", "h6"}

    ''' <summary>Appends a node's text to <paramref name="current"/>, ending a line at each block element.</summary>
    Private Shared Sub AppendBlocks(node As System.Xml.Linq.XNode, lines As List(Of String), current As System.Text.StringBuilder)
        Dim text As System.Xml.Linq.XText = TryCast(node, System.Xml.Linq.XText)
        If text IsNot Nothing Then
            current.Append(text.Value)
            Return
        End If
        Dim element As System.Xml.Linq.XElement = TryCast(node, System.Xml.Linq.XElement)
        If element Is Nothing Then Return
        Dim isBlock As Boolean = BlockElements.Contains(element.Name.LocalName)
        If isBlock Then EndLine(lines, current)
        For Each child As System.Xml.Linq.XNode In element.Nodes()
            AppendBlocks(child, lines, current)
        Next
        If isBlock Then EndLine(lines, current)
    End Sub

    Private Shared Sub EndLine(lines As List(Of String), current As System.Text.StringBuilder)
        Dim line As String = current.ToString().Trim()
        If line.Length > 0 Then lines.Add(line)
        current.Clear()
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
