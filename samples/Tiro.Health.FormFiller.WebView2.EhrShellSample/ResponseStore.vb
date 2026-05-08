Imports Hl7.Fhir.Model

''' <summary>
''' One saved entry in the in-memory store. Carries the QR plus enough EHR
''' context to render a reports list (patient, encounter label, template,
''' timestamp).
''' </summary>
Public Class ResponseEntry
    Public ReadOnly Property Patient As Patient
    Public ReadOnly Property Encounter As Encounter
    Public ReadOnly Property EncounterLabel As String
    Public ReadOnly Property Template As TemplateOption
    Public ReadOnly Property Response As QuestionnaireResponse
    Public ReadOnly Property SavedAt As DateTime

    Public Sub New(patient As Patient, encounter As Encounter, encounterLabel As String,
                   template As TemplateOption, response As QuestionnaireResponse, savedAt As DateTime)
        Me.Patient = patient
        Me.Encounter = encounter
        Me.EncounterLabel = encounterLabel
        Me.Template = template
        Me.Response = response
        Me.SavedAt = savedAt
    End Sub

    Public ReadOnly Property DisplayLabel As String
        Get
            Return $"{SavedAt:yyyy-MM-dd HH:mm} — {Template.Label}    ·    {EncounterLabel}"
        End Get
    End Property
End Class

''' <summary>
''' In-memory store keyed by (patientId, encounterId, templateUrl). Stand-in
''' for a real EHR persistence layer. Saving the same combination overwrites
''' the previous entry (same as a real "edit existing report" flow). Reports
''' for a given patient can be listed (newest first) for the reports view.
''' </summary>
Public Class ResponseStore

    Private ReadOnly _store As New Dictionary(Of String, ResponseEntry)()

    Public Function GetEntry(patient As Patient, encounter As Encounter, template As TemplateOption) As ResponseEntry
        Dim entry As ResponseEntry = Nothing
        _store.TryGetValue(MakeKey(patient.Id, encounter.Id, template.CanonicalUrl), entry)
        Return entry
    End Function

    Public Sub SaveResponse(patient As Patient, encounter As Encounter, encounterLabel As String,
                            template As TemplateOption, response As QuestionnaireResponse)
        _store(MakeKey(patient.Id, encounter.Id, template.CanonicalUrl)) =
            New ResponseEntry(patient, encounter, encounterLabel, template, response, DateTime.Now)
    End Sub

    Public Function GetReportsFor(patient As Patient) As List(Of ResponseEntry)
        Dim list = New List(Of ResponseEntry)()
        For Each entry In _store.Values
            If entry.Patient.Id = patient.Id Then list.Add(entry)
        Next
        list.Sort(Function(a, b) b.SavedAt.CompareTo(a.SavedAt))
        Return list
    End Function

    Private Shared Function MakeKey(patientId As String, encounterId As String, templateUrl As String) As String
        Return $"{patientId}|{encounterId}|{templateUrl}"
    End Function
End Class
