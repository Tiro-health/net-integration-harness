Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class QuestionnaireForm
    Private ReadOnly _questionnaireUrl As String
    Private ReadOnly _patient As Patient
    Private _isFormSubmitted As Boolean = False

    ''' <summary>
    ''' Milliseconds from Form.Load to SetContextAsync completing — i.e. the full "time to ready" the user perceives.
    ''' Populated once the questionnaire has been requested; read by the launcher after ShowDialog returns.
    ''' </summary>
    Public Property LoadDurationMs As Long = -1

    Public Sub New(questionnaireUrl As String, patient As Patient)
        InitializeComponent()
        _questionnaireUrl = questionnaireUrl
        _patient = patient
    End Sub

    Private Async Sub QuestionnaireForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf OnFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf OnCloseApplication

        Dim sw = Stopwatch.StartNew()
        Await TiroFormViewer.SetContextAsync(_questionnaireUrl, _patient)
        sw.Stop()
        LoadDurationMs = sw.ElapsedMilliseconds
        Text = $"Questionnaire — ready in {LoadDurationMs} ms"
    End Sub

    Private Sub OnFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        _isFormSubmitted = True
        Me.Close()
    End Sub

    Private Sub OnCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        _isFormSubmitted = True
        Me.Close()
    End Sub

    Private Async Sub QuestionnaireForm_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        If Not _isFormSubmitted Then
            e.Cancel = True
            Await TiroFormViewer.SendFormRequestSubmitAsync()
        End If
    End Sub
End Class
