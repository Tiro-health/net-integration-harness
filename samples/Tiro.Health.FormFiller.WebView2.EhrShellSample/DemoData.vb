Imports Hl7.Fhir.Model

Public Class EncounterRecord
    Public ReadOnly Property Encounter As Encounter
    Public ReadOnly Property Label As String

    Public Sub New(encounter As Encounter, label As String)
        Me.Encounter = encounter
        Me.Label = label
    End Sub
End Class

Public Class PatientRecord
    Public ReadOnly Property Patient As Patient
    Public ReadOnly Property Encounters As List(Of EncounterRecord)

    Public Sub New(patient As Patient, encounters As List(Of EncounterRecord))
        Me.Patient = patient
        Me.Encounters = encounters
    End Sub
End Class

Public Class TemplateOption
    Public Property Label As String
    Public Property CanonicalUrl As String
End Class

''' <summary>
''' Hardcoded fake EHR data: the "logged-in" practitioner, three patients with two
''' encounters each, and four questionnaire templates (three known-live on the
''' default Tiro SDC server plus one known-broken to demonstrate the failure path).
''' </summary>
Module DemoData

    Public ReadOnly Property Practitioner As Practitioner = NewPractitioner(
        id:="prac-1",
        displayName:="Dr. Anna van der Berg",
        given:="Anna",
        family:="van der Berg")

    Public ReadOnly Property Patients As List(Of PatientRecord) = New List(Of PatientRecord) From {
        NewPatientRecord("pat-1", "Leonardo da Vinci", "Leonardo", "da Vinci", "1452-04-15", AdministrativeGender.Male,
            New (id As String, label As String, startDate As String)() {
                ("enc-1-1", "2026-04-12 — GP visit", "2026-04-12"),
                ("enc-1-2", "2026-03-01 — Cardiology consult", "2026-03-01")
            }),
        NewPatientRecord("pat-2", "Marie Curie", "Marie", "Curie", "1867-11-07", AdministrativeGender.Female,
            New (id As String, label As String, startDate As String)() {
                ("enc-2-1", "2026-04-08 — Lab follow-up", "2026-04-08"),
                ("enc-2-2", "2026-02-15 — New referral", "2026-02-15")
            }),
        NewPatientRecord("pat-3", "Albert Einstein", "Albert", "Einstein", "1879-03-14", AdministrativeGender.Male,
            New (id As String, label As String, startDate As String)() {
                ("enc-3-1", "2026-04-01 — Chest pain workup", "2026-04-01")
            })
    }

    Public ReadOnly Property Templates As List(Of TemplateOption) = New List(Of TemplateOption) From {
        New TemplateOption With {
            .Label = "Chadsvasc",
            .CanonicalUrl = "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699"},
        New TemplateOption With {
            .Label = "MR KNIE test",
            .CanonicalUrl = "http://templates.tiro.health/templates/c32a3910844b479a9d9c7ea19ec405ab"},
        New TemplateOption With {
            .Label = "Mondholte tumoren",
            .CanonicalUrl = "http://templates.tiro.health/templates/049555af4ddf4a74b7d2572f7000b12c"}
    }

    Private Function NewPractitioner(id As String, displayName As String, given As String, family As String) As Practitioner
        Return New Practitioner() With {
            .Id = id,
            .Name = New List(Of HumanName) From {
                New HumanName() With {.Family = family, .Given = New List(Of String) From {given}, .Text = displayName}
            },
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {.System = "http://test.org/test/practitioner-ids", .Value = id}
            }
        }
    End Function

    Private Function NewPatient(id As String, displayName As String, given As String, family As String, birthDate As String, gender As AdministrativeGender) As Patient
        Return New Patient() With {
            .Id = id,
            .Name = New List(Of HumanName) From {
                New HumanName() With {.Family = family, .Given = New List(Of String) From {given}, .Text = displayName}
            },
            .BirthDate = birthDate,
            .Gender = gender,
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {.System = "http://test.org/test/patient-ids", .Value = id}
            }
        }
    End Function

    Private Function NewEncounter(id As String, startDate As String) As Encounter
        Return New Encounter() With {
            .Id = id,
            .Status = EncounterStatus.Completed,
            .ActualPeriod = New Period() With {.StartElement = New FhirDateTime(startDate)},
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {.System = "http://test.org/test/encounter-ids", .Value = id}
            }
        }
    End Function

    Private Function NewPatientRecord(
            patientId As String,
            displayName As String,
            given As String,
            family As String,
            birthDate As String,
            gender As AdministrativeGender,
            encounters As (id As String, label As String, startDate As String)()) As PatientRecord
        Dim p = NewPatient(patientId, displayName, given, family, birthDate, gender)
        Dim list = New List(Of EncounterRecord)()
        For Each tup In encounters
            list.Add(New EncounterRecord(NewEncounter(tup.id, tup.startDate), tup.label))
        Next
        Return New PatientRecord(p, list)
    End Function

End Module
