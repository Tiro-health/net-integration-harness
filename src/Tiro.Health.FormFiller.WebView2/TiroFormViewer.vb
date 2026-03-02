Imports System.IO
Imports System.Reflection
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Hl7.Fhir.Model
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Web.WebView2.Core
Imports Tiro.Health.SmartWebMessaging.Fhir.R5
Imports Tiro.Health.SmartWebMessaging.Events
Imports Task = System.Threading.Tasks.Task
Imports Tiro.Health.SmartWebMessaging.Message
Imports Sentry

Public Class TiroFormViewer
    Inherits UserControl

    Public Event FormSubmitted(ByVal sender As Object, ByVal e As FormSubmittedEventArgs)

    Private _logger As ILogger = NullLogger.Instance
    Private smartWebMessageHandler As SmartMessageHandler
    Private Const VirtualHostName As String = "appassets.local"

    ' Tracks if Webview is initialized
    Private ReadOnly _initializationTask As Threading.Tasks.Task

    ' Track if handshake has been received
    Private _handshakeReceivedSource As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

    ' Single transaction for entire form lifecycle
    Private _transaction As ITransactionTracer

    ' Track if control is disposed
    Private _isDisposed As Boolean = False

    Public Sub New()
        InitializeComponent()

        ' Ensure Sentry is active for this session
        If Not SentrySdk.IsEnabled Then
            SentrySdk.Init(Sub(o)
                               o.Dsn = "https://e2152463656fef5d6cf67ac91af87050@o4507651309043712.ingest.de.sentry.io/4510703529820240"
                               o.IsGlobalModeEnabled = True
                               o.TracesSampleRate = 1.0
                           End Sub)
        End If

        ' Start the single transaction for the form lifecycle
        _transaction = SentrySdk.StartTransaction("SDC Form", "sdc.form")

        smartWebMessageHandler = New SmartMessageHandler()
        AddHandler smartWebMessageHandler.HandshakeReceived, AddressOf OnHandshakeReceived
        AddHandler smartWebMessageHandler.FormSubmitted, AddressOf MyFormSubmitted

        _initializationTask = InitializeWebViewAsync()
    End Sub

    ''' <summary>
    ''' Call this from the designer's Dispose method to clean up Sentry transaction.
    ''' </summary>
    Friend Sub FinishSentryTransaction()
        _isDisposed = True
        Try
            If _transaction IsNot Nothing Then
                _transaction.Finish(SpanStatus.InternalError)
                _transaction = Nothing
            End If
        Catch

        End Try
        SentrySdk.Flush(TimeSpan.FromSeconds(1.0))

    End Sub

    Private Async Function InitializeWebViewAsync() As Threading.Tasks.Task
        Dim initSpan = _transaction?.StartChild("sdc.initialize", "Initialize WebView")

        Try
            Await WebView2Host.EnsureCoreWebView2Async()

            Dim coreWebView2 = WebView2Host.CoreWebView2
            AddHandler coreWebView2.WebMessageReceived, AddressOf SMARTWebMessageReceived
            AddHandler coreWebView2.PermissionRequested, AddressOf OnPermissionRequested

            ' SendMessage callback - creates a span for each outbound message
            smartWebMessageHandler.SendMessage = Function(jsonMessage As String) As Task(Of String)
                                                     If WebView2Host.CoreWebView2 IsNot Nothing AndAlso _transaction IsNot Nothing Then
                                                         Dim messageType = ExtractJsonField(jsonMessage, "messageType")
                                                         Dim spanName = If(Not String.IsNullOrEmpty(messageType), messageType, "outbound")

                                                         Dim span = _transaction.StartChild("sdc.send", spanName)
                                                         span.SetExtra("message", jsonMessage)
                                                         span.Finish(SpanStatus.Ok)

                                                         WebView2Host.CoreWebView2.PostWebMessageAsJson(jsonMessage)
                                                     End If
                                                     Return Task.FromResult(Of String)("")
                                                 End Function

            Dim startUri = "https://tiro-health.github.io/web-sdk-tutorial/html+js-smartwebmessaging/"
            WebView2Host.Source = New Uri(startUri)

            initSpan?.Finish(SpanStatus.Ok)

        Catch ex As Exception
            initSpan?.Finish(ex)
            SentrySdk.CaptureException(ex)
            Throw
        End Try
    End Function

    Private Sub SMARTWebMessageReceived(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
        If _isDisposed Then Return
        If WebView2Host?.CoreWebView2 Is Nothing Then Return

        Dim inboundJson As String = e?.WebMessageAsJson
        If String.IsNullOrEmpty(inboundJson) Then Return

        Dim messageType = ExtractJsonField(inboundJson, "messageType")
        Dim spanName = If(Not String.IsNullOrEmpty(messageType), messageType, "inbound")

        Dim span = _transaction?.StartChild("sdc.receive", spanName)
        span?.SetExtra("message", inboundJson)

        Try
            Dim responseJson As String = smartWebMessageHandler?.HandleMessage(inboundJson)

            If Not String.IsNullOrEmpty(responseJson) AndAlso Not _isDisposed AndAlso WebView2Host?.CoreWebView2 IsNot Nothing Then
                Dim responseSpan = _transaction?.StartChild("sdc.response", spanName & ".response")
                responseSpan?.SetExtra("message", responseJson)
                responseSpan?.Finish(SpanStatus.Ok)

                WebView2Host.CoreWebView2.PostWebMessageAsJson(responseJson)
            End If

            span?.Finish(SpanStatus.Ok)
        Catch ex As Exception
            span?.Finish(ex)
            SentrySdk.CaptureException(ex)
        End Try
    End Sub

    Private Sub OnPermissionRequested(sender As Object, e As CoreWebView2PermissionRequestedEventArgs)
        If e.PermissionKind = CoreWebView2PermissionKind.Microphone Then
            e.State = CoreWebView2PermissionState.Allow
        End If
    End Sub

    Private Sub OnHandshakeReceived(sender As Object, e As EventArgs)
        _handshakeReceivedSource.TrySetResult(True)
    End Sub

    Private Sub MyFormSubmitted(ByVal sender As Object, ByVal e As FormSubmittedEventArgs)
        Try
            RaiseEvent FormSubmitted(Me, e)
            _transaction?.Finish(SpanStatus.Ok)
        Catch ex As Exception
            _transaction?.Finish(ex)
            SentrySdk.CaptureException(ex)
        Finally
            _transaction = Nothing
        End Try
    End Sub

    Public Async Function SetContextAsync(questionnaireCanonicalUrl As String,
                                     patient As Patient,
                                     Optional encounter As Encounter = Nothing,
                                     Optional author As Practitioner = Nothing,
                                     Optional intitialResponse As QuestionnaireResponse = Nothing) As Threading.Tasks.Task
        Try
            If _transaction IsNot Nothing Then
                _transaction.SetTag("questionnaire_url", questionnaireCanonicalUrl)
            End If

            Await _initializationTask

            Dim handshakeTask = _handshakeReceivedSource.Task
            Dim timeoutTask = Task.Delay(30000)
            Dim completedTask = Await Task.WhenAny(handshakeTask, timeoutTask)

            If completedTask Is timeoutTask Then
                Dim timeoutEx As New TimeoutException($"Handshake not received for {questionnaireCanonicalUrl} within 30s.")
                SentrySdk.CaptureException(timeoutEx)
                If _transaction IsNot Nothing Then
                    _transaction.Finish(SpanStatus.DeadlineExceeded)
                    _transaction = Nothing
                End If
                Throw timeoutEx
            End If

            Await smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(questionnaireCanonicalUrl, intitialResponse, patient, encounter, author)

        Catch ex As Exception
            If _transaction IsNot Nothing Then
                _transaction.Finish(ex)
                _transaction = Nothing
            End If
            SentrySdk.CaptureException(ex)
            Throw
        End Try
    End Function

    Public Async Function SendFormRequestSubmitAsync(Optional responseHandler As Func(Of SmartMessageResponse, Task) = Nothing) As Threading.Tasks.Task
        Try
            Await _initializationTask

            Dim handshakeTask = _handshakeReceivedSource.Task
            Dim timeoutTask = Task.Delay(30000)
            Dim completedTask = Await Task.WhenAny(handshakeTask, timeoutTask)

            If completedTask Is timeoutTask Then
                Dim timeoutEx As New TimeoutException("Handshake timeout during Form Request Submit.")
                SentrySdk.CaptureException(timeoutEx)
                If _transaction IsNot Nothing Then
                    _transaction.Finish(SpanStatus.DeadlineExceeded)
                    _transaction = Nothing
                End If
                Throw timeoutEx
            End If

            Await smartWebMessageHandler.SendFormRequestSubmitAsync(responseHandler)

        Catch ex As Exception
            SentrySdk.CaptureException(ex)
            If _transaction IsNot Nothing Then
                _transaction.Finish(ex)
                _transaction = Nothing
            End If
            Throw
        End Try
    End Function

    Private Shared Function ExtractJsonField(json As String, fieldName As String) As String
        If String.IsNullOrEmpty(json) Then Return Nothing

        Try
            Dim searchKey = $"""{fieldName}"""
            Dim keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal)
            If keyIndex < 0 Then Return Nothing

            Dim colonIndex = json.IndexOf(":"c, keyIndex + searchKey.Length)
            If colonIndex < 0 Then Return Nothing

            Dim startQuote = json.IndexOf(""""c, colonIndex + 1)
            If startQuote < 0 Then Return Nothing

            Dim endQuote = startQuote + 1
            While endQuote < json.Length
                If json(endQuote) = """"c AndAlso json(endQuote - 1) <> "\"c Then
                    Exit While
                End If
                endQuote += 1
            End While

            If endQuote >= json.Length Then Return Nothing

            Return json.Substring(startQuote + 1, endQuote - startQuote - 1)
        Catch
            Return Nothing
        End Try
    End Function

End Class
