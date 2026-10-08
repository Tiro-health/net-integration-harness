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

    ' Read by the right-click menu when an item is picked.
    Private _patient As Patient

    ' The form's right-click menu, rebuilt on every right-click. Kept so the previous one can be disposed.
    Private _formMenu As ContextMenuStrip

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication
        AddHandler TiroFormViewer.ContextMenuOpening, AddressOf OnFormContextMenuOpening

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
        _patient = patient

        ' The clinician writing the report and the visit it belongs to. The form's Klinische
        ' informatie fields prefill from these: Auteur from %user (the author), Datum consult
        ' from %encounter. Leave either out and its field stays empty.
        Dim author As New Practitioner() With {
            .Id = "practitioner-1",
            .Name = New List(Of HumanName) From {
                New HumanName() With {
                    .Family = "Peeters",
                    .Given = New List(Of String) From {"An"},
                    .Text = "Dr. An Peeters"
                }
            }
        }

        Dim encounter As New Encounter() With {
            .Id = "encounter-1",
            .Status = EncounterStatus.Completed,
            .ActualPeriod = New Period() With {.StartElement = New FhirDateTime("2026-10-01T09:30:00+02:00")}
        }

        ' Any other named resource goes through launchContext, alongside the patient/encounter/
        ' author shorthand — here the report's specimens, which the form reads as %specimen for
        ' Weefseltype. The field shows type.text, so set it; a coding alone leaves the field empty.
        ' Several entries under one name reach the form as a collection (SDC launchContext
        ' multiplesAllowed): the template picks one with %specimen.where(identifier.value = 'B'),
        ' or counts them with %specimen.count(). Give each specimen an identifier to select on.
        Dim specimenA As New Specimen() With {
            .Id = "specimen-1",
            .Identifier = New List(Of Identifier) From {New Identifier("http://example.org/specimen-label", "A")},
            .Type = New CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0487", "TISS", "Tissue") With {.Text = "Huid"},
            .Subject = New ResourceReference("Patient/test-123")
        }

        Dim specimenB As New Specimen() With {
            .Id = "specimen-2",
            .Identifier = New List(Of Identifier) From {New Identifier("http://example.org/specimen-label", "B")},
            .Type = New CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0487", "LYMPH", "Lymph node") With {.Text = "Lymfeklier"},
            .Subject = New ResourceReference("Patient/test-123")
        }

        ' A pathology report with Macroscopie / Microscopie / Conclusie sections. Its Composition
        ' blueprint (authored with the template) is what makes $extract return one Composition
        ' section per report section — see HandleFormSubmitted. No |version: the latest published
        ' version loads, whose Weefseltype reads %specimen.where(identifier.value = 'B').
        Await TiroFormViewer.SetContextAsync(
            "http://templates.tiro.health/templates/44ed83d0ee324811a170dd9b4098bb3a",
            patient:=patient,
            encounter:=encounter,
            author:=author,
            launchContext:=New List(Of LaunchContext(Of Resource)) From {
                New LaunchContext(Of Resource)("specimen", contentResource:=specimenA),
                New LaunchContext(Of Resource)("specimen", contentResource:=specimenB)
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
    ''' The form's right-click menu, drawn by the EHR. Inserting still lands at the caret: the
    ''' menu takes focus out of the page, but the page remembers the field and caret the
    ''' clinician right-clicked in.
    ''' </summary>
    Private Sub OnFormContextMenuOpening(sender As Object, e As TiroContextMenuOpeningEventArgs)
        ' Only take over where there is something to insert into. Elsewhere — a checkbox, a
        ' label, a selection to copy — the browser's own menu is the useful one.
        If Not e.Context.IsEditable Then Return
        e.Handled = True

        _formMenu?.Dispose()
        _formMenu = New ContextMenuStrip()

        Dim insertMenu As New ToolStripMenuItem("Insert")
        insertMenu.DropDownItems.Add(InsertMenuItem("Patient name", Function() _patient.Name(0).Text))
        insertMenu.DropDownItems.Add(InsertMenuItem("""No known drug allergies""", Function() "No known drug allergies."))

        ' The conclusion the EHR holds as RTF, plain or keeping its formatting. With both, the page
        ' offers the HTML to the field first and falls back to the plain text if the field won't
        ' take it; the window title says which happened. ToHtml keeps what a field can store
        ' (emphasis, paragraphs) and flattens the rest — pass your own converter's output if you
        ' have one. Submenus nest as deep as the EHR likes: it's a plain WinForms menu.
        Dim conclusionMenu As New ToolStripMenuItem("Conclusion")
        conclusionMenu.DropDownItems.Add(InsertMenuItem("Plain text", Function() TiroRtf.ToPlainText(ConclusionRtf)))
        conclusionMenu.DropDownItems.Add(InsertMenuItem("Formatted",
                                                        Function() TiroRtf.ToPlainText(ConclusionRtf),
                                                        Function() TiroRtf.ToHtml(ConclusionRtf)))
        insertMenu.DropDownItems.Add(conclusionMenu)

        _formMenu.Items.Add(insertMenu)
        _formMenu.Show(TiroFormViewer, e.Location)
    End Sub

    ''' <summary>
    ''' One menu item that inserts at the caret. The content is resolved when the item is
    ''' picked, not when the menu is built.
    ''' </summary>
    Private Function InsertMenuItem(label As String, text As Func(Of String), Optional html As Func(Of String) = Nothing) As ToolStripMenuItem
        Dim item As New ToolStripMenuItem(label)
        AddHandler item.Click,
            Async Sub(s As Object, args As EventArgs)
                Try
                    Dim result As TextInsertResult =
                        Await TiroFormViewer.InsertContentAsync(text(), If(html Is Nothing, Nothing, html()))
                    ShowInsertResult(result)
                Catch ex As Exception
                    ' An Async Sub has no caller to report to; catch here so a failed insert
                    ' can't crash the sample.
                    Me.Text = "Extract sample — insert failed: " & ex.Message
                End Try
            End Sub
        Return item
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
                    ' One section per part of the report (Macroscopie, Microscopie, Conclusie),
                    ' each with its own text. Keeping them apart is why you'd extract: an EHR can
                    ' file each section into its own field. Here they are just listed.
                    Dim report As New System.Text.StringBuilder()
                    For Each section As Composition.SectionComponent In composition.Section
                        report.AppendLine(section.Title.ToUpperInvariant())
                        report.AppendLine(SectionText(section.Text.Div))
                        report.AppendLine()
                    Next

                    MessageBox.Show(report.ToString(), composition.Title, MessageBoxButtons.OK, MessageBoxIcon.Information)
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
    ''' A section's text is XHTML: an &lt;h2&gt; with the title, then a block per answer.
    ''' This keeps the text blocks (&lt;p&gt;, &lt;div&gt;, &lt;li&gt;), one per line. Only the
    ''' innermost ones are read: a rich-text answer brings its own paragraphs, which the server
    ''' currently nests inside another &lt;p&gt;, and reading both would list it twice.
    ''' </summary>
    Private Shared Function SectionText(div As String) As String
        Return String.Join(Environment.NewLine,
            System.Xml.Linq.XElement.Parse(div).Descendants().
                Where(Function(e) IsTextBlock(e) AndAlso Not e.Descendants().Any(AddressOf IsTextBlock)).
                Select(Function(e) e.Value))
    End Function

    Private Shared Function IsTextBlock(e As System.Xml.Linq.XElement) As Boolean
        Return e.Name.LocalName = "p" OrElse e.Name.LocalName = "div" OrElse e.Name.LocalName = "li"
    End Function

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
