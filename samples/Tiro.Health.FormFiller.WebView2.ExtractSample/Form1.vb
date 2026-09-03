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
        AddInsertItem("Insert patient name",
                      Function() patient.Name(0).Text)

        AddInsertItem("Insert ""no known drug allergies""",
                      Function() "No known drug allergies.")

        ' The conclusion the EHR holds as RTF, flattened to plain text by WinForms' own RTF
        ' parser. Goes into any field; formatting dropped.
        AddInsertItem("Insert conclusion (plain text)",
                      Function() RtfToPlainText(ConclusionRtf))

        ' The same conclusion, keeping its formatting. This is the item to watch: the page
        ' offers the HTML to the field first and falls back to the plain text if the field
        ' won't take it, and the window title says which happened. Mode=Html means a rich-text
        ' answer kept the bold and italic; Mode=Text means that field can only hold plain text,
        ' which no amount of conversion quality would change.
        AddInsertItem("Insert conclusion (formatted)",
                      Function() RtfToPlainText(ConclusionRtf),
                      Function() ConvertRtfToHtml(ConclusionRtf))

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

    ''' <summary>
    ''' Adds a right-click item that inserts content at the caret. Wraps the three lines every
    ''' such item needs — build it, hide it where there is nothing to type into, add it — so the
    ''' menu above reads as a list of snippets rather than as plumbing.
    ''' </summary>
    ''' <param name="label">The menu text.</param>
    ''' <param name="text">Plain-text rendition, resolved at click time. Always required.</param>
    ''' <param name="html">
    ''' Optional body-level HTML fragment. Supplied only for content whose formatting matters;
    ''' the page falls back to <paramref name="text"/> when the field cannot take it.
    ''' </param>
    Private Sub AddInsertItem(label As String,
                              text As Func(Of String),
                              Optional html As Func(Of String) = Nothing)
        Dim item As New TiroContextMenuItem(
            label,
            Function(context) ShowInsertResult(
                TiroFormViewer.InsertContentAsync(text(), If(html Is Nothing, Nothing, html()))))
        item.IsVisible = Function(context) context.IsEditable
        TiroFormViewer.ContextMenuItems.Add(item)
    End Sub

    ''' <summary>
    ''' Awaits an insert and puts the outcome in the window title, so this experiment's result
    ''' is visible without a debugger. A real integration would show nothing on success and
    ''' "click in a field first" when nothing was focused.
    ''' </summary>
    ' Task is fully qualified on purpose: Hl7.Fhir.Model, imported above, also defines a Task
    ' (the FHIR resource), so the unqualified name is ambiguous here. The C# projects solve the
    ' same clash with a `using Task = System.Threading.Tasks.Task` alias, which VB can't apply
    ' to the generic Task(Of T).
    Private Async Function ShowInsertResult(
        pending As System.Threading.Tasks.Task(Of TextInsertResult)) As System.Threading.Tasks.Task
        Dim result As TextInsertResult = Await pending
        Dim summary As String
        If Not result.Inserted Then
            summary = "nothing inserted — click in a text field first"
        ElseIf result.KeptFormatting Then
            summary = "inserted WITH formatting (mode=Html)"
        Else
            summary = "inserted as plain text (mode=Text) — the field would not take the HTML"
        End If
        Me.Text = "Extract sample — " & summary
    End Function

    ''' <summary>
    ''' The conclusion as the EHR holds it: RTF. Real integrations read this from their own
    ''' store — the constant stands in for that, so the menu items show the shape a real
    ''' integration takes rather than starting from HTML nobody would have.
    ''' </summary>
    Private Const ConclusionRtf As String =
        "{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22" &
        "{\b Assessment.} Findings consistent with the clinical picture; " &
        "{\i no further imaging indicated}.\par}"

    ''' <summary>
    ''' Plain text out of RTF, using WinForms' own RTF parser — no library needed. A named
    ''' helper rather than an inline lambda because RichTextBox owns a Win32 handle: it has to
    ''' be disposed, and a menu item invoked once per click would otherwise leak one each time.
    ''' </summary>
    Private Shared Function RtfToPlainText(rtf As String) As String
        If String.IsNullOrEmpty(rtf) Then Return String.Empty
        Using box As New RichTextBox()
            box.Rtf = rtf
            Return box.Text
        End Using
    End Function

    ''' <summary>
    ''' RTF to HTML. A STAND-IN: it returns a fixed fragment matching <see cref="ConclusionRtf"/>
    ''' instead of parsing anything, so the sample can show the whole flow without taking a
    ''' dependency the other samples don't have.
    ''' </summary>
    ''' <remarks>
    ''' Replace the body with a real converter — <c>RtfPipe.Rtf.ToHtml(rtf)</c> on net48, or a
    ''' commercial engine if you already license one. Two things to require of whichever you pick:
    ''' <list type="bullet">
    ''' <item><description>
    ''' It must emit semantic tags (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>) or inline styles. A
    ''' clipboard HTML flavour is a fragment, so a converter that emits CSS classes plus a
    ''' stylesheet loses the stylesheet and every rule with it — underline and colour vanish
    ''' while bold and italic survive, which is a confusing way to discover the problem.
    ''' </description></item>
    ''' <item><description>
    ''' Fidelity beyond what the field can store is wasted. The rich-text answers keep inline
    ''' formatting and links; constructs whose editor nodes aren't registered there (tables,
    ''' possibly lists) flatten to paragraphs however good the conversion was.
    ''' </description></item>
    ''' </list>
    ''' Returns a body-level fragment: no &lt;html&gt; or &lt;head&gt; wrapper. The harness adds
    ''' the CF_HTML envelope.
    ''' </remarks>
    Private Shared Function ConvertRtfToHtml(rtf As String) As String
        If String.IsNullOrEmpty(rtf) Then Return String.Empty
        Return "<p><b>Assessment.</b> Findings consistent with the clinical picture; " &
               "<i>no further imaging indicated</i>.</p>"
    End Function

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
