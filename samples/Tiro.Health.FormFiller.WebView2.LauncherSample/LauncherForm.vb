Imports Hl7.Fhir.Model

Public Class LauncherForm
    Private Const QuestionnaireUrl As String = "http://templates.tiro.health/templates/2630b8675c214707b1f86d1fbd4deb87"

    Private ReadOnly _patients As List(Of Patient) = BuildPatients()
    Private _openCount As Integer = 0

    Private Sub LauncherForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        For Each p In _patients
            PatientList.Items.Add(p.Name.First().Text)
        Next
        PatientList.SelectedIndex = 0
    End Sub

    Private Sub OpenButton_Click(sender As Object, e As EventArgs) Handles OpenButton.Click
        If PatientList.SelectedIndex < 0 Then Return
        Dim patient = _patients(PatientList.SelectedIndex)

        Using form As New QuestionnaireForm(QuestionnaireUrl, patient)
            form.ShowDialog(Me)
            _openCount += 1
            StatusLabel.Text = $"Opens: {_openCount}"
            TimingLabel.Text = $"Last load: {form.LoadDurationMs} ms"
        End Using
    End Sub

    Private Shared Function BuildPatients() As List(Of Patient)
        Return New List(Of Patient) From {
            NewPatient("Leonardo da Vinci", "Leonardo", "da Vinci", "1452-04-15", AdministrativeGender.Male, "test-123"),
            NewPatient("Marie Curie", "Marie", "Curie", "1867-11-07", AdministrativeGender.Female, "test-456"),
            NewPatient("Albert Einstein", "Albert", "Einstein", "1879-03-14", AdministrativeGender.Male, "test-789")
        }
    End Function

    Private Shared Function NewPatient(displayName As String, given As String, family As String, birthDate As String, gender As AdministrativeGender, identifierValue As String) As Patient
        Return New Patient() With {
            .Name = New List(Of HumanName) From {
                New HumanName() With {
                    .Family = family,
                    .Given = New List(Of String) From {given},
                    .Text = displayName
                }
            },
            .BirthDate = birthDate,
            .Gender = gender,
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {
                    .System = "http://test.org/test/patient-ids",
                    .Value = identifierValue
                }
            }
        }
    End Function
End Class
