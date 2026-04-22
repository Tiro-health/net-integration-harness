using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Tiro.Health.SmartWebMessaging.Fhir.R5;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message;
using Sentry;

namespace Tiro.Health.FormFiller.WebView2
{
    public partial class TiroFormViewer : UserControl
    {
        public event EventHandler<FormSubmittedEventArgs> FormSubmitted;

        private ILogger _logger = NullLogger.Instance;
        private SmartMessageHandler _smartWebMessageHandler;
        private const string VirtualHostName = "appassets.local";

        // Tracks if WebView is initialized
        private Task _initializationTask;

        // Track if handshake has been received
        private readonly TaskCompletionSource<bool> _handshakeReceivedSource =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Single transaction for entire form lifecycle
        private ITransactionTracer _transaction;

        // Track if control is disposed
        private bool _isDisposed = false;

        public TiroFormViewer()
        {
            InitializeComponent();
            // Skip all runtime initialization at design time.
            // IMPORTANT: all FHIR/Sentry references must stay in InitializeRuntime(), NOT here.
            // The JIT resolves every type referenced in this method body before executing any code,
            // so even an early return cannot guard against types referenced further down in this method.
            if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
                return;
            InitializeRuntime();
        }

        private void InitializeRuntime()
        {
            if (!SentrySdk.IsEnabled)
            {
                SentrySdk.Init(o =>
                {
                    o.Dsn = "https://e2152463656fef5d6cf67ac91af87050@o4507651309043712.ingest.de.sentry.io/4510703529820240";
                    o.IsGlobalModeEnabled = true;
                    o.TracesSampleRate = 1.0;
                });
            }

            _transaction = SentrySdk.StartTransaction("SDC Form", "sdc.form");

            _smartWebMessageHandler = new SmartMessageHandler();
            _smartWebMessageHandler.HandshakeReceived += OnHandshakeReceived;
            _smartWebMessageHandler.FormSubmitted += OnFormSubmitted;

            _initializationTask = InitializeWebViewAsync();
        }

        /// <summary>
        /// Called from Dispose to clean up the Sentry transaction.
        /// </summary>
        internal void FinishSentryTransaction()
        {
            _isDisposed = true;
            try
            {
                if (_transaction != null)
                {
                    _transaction.Finish(SpanStatus.InternalError);
                    _transaction = null;
                }
            }
            catch { }
            SentrySdk.Flush(TimeSpan.FromSeconds(1.0));
        }

        private async Task InitializeWebViewAsync()
        {
            var initSpan = _transaction?.StartChild("sdc.initialize", "Initialize WebView");

            try
            {
                await WebView2Host.EnsureCoreWebView2Async();

                var coreWebView2 = WebView2Host.CoreWebView2;
                coreWebView2.WebMessageReceived += SMARTWebMessageReceived;
                coreWebView2.PermissionRequested += OnPermissionRequested;

                _smartWebMessageHandler.SendMessage = (string jsonMessage) =>
                {
                    if (WebView2Host.CoreWebView2 != null && _transaction != null)
                    {
                        var messageType = ExtractJsonField(jsonMessage, "messageType");
                        var spanName = !string.IsNullOrEmpty(messageType) ? messageType : "outbound";

                        var span = _transaction.StartChild("sdc.send", spanName);
                        span.SetExtra("message", jsonMessage);
                        span.Finish(SpanStatus.Ok);

                        WebView2Host.CoreWebView2.PostWebMessageAsJson(jsonMessage);
                    }
                    return Task.FromResult("");
                };
                // TODO pass local index.html with virtual host mapping
                var startUri = "https://tiro-health.github.io/web-sdk-tutorial/html+js-smartwebmessaging/"; 
                WebView2Host.Source = new Uri(startUri);

                initSpan?.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                initSpan?.Finish(ex);
                SentrySdk.CaptureException(ex);
                throw;
            }
        }

        private void SMARTWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_isDisposed || WebView2Host?.CoreWebView2 == null) return;

            var inboundJson = e?.WebMessageAsJson;
            if (string.IsNullOrEmpty(inboundJson)) return;

            var messageType = ExtractJsonField(inboundJson, "messageType");
            var spanName = !string.IsNullOrEmpty(messageType) ? messageType : "inbound";

            var span = _transaction?.StartChild("sdc.receive", spanName);
            span?.SetExtra("message", inboundJson);

            try
            {
                var responseJson = _smartWebMessageHandler?.HandleMessage(inboundJson);

                if (!string.IsNullOrEmpty(responseJson) && !_isDisposed && WebView2Host?.CoreWebView2 != null)
                {
                    var responseSpan = _transaction?.StartChild("sdc.response", spanName + ".response");
                    responseSpan?.SetExtra("message", responseJson);
                    responseSpan?.Finish(SpanStatus.Ok);

                    WebView2Host.CoreWebView2.PostWebMessageAsJson(responseJson);
                }

                span?.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                span?.Finish(ex);
                SentrySdk.CaptureException(ex);
            }
        }

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        }

        private void OnHandshakeReceived(object sender, EventArgs e)
        {
            _handshakeReceivedSource.TrySetResult(true);
        }

        private void OnFormSubmitted(object sender, FormSubmittedEventArgs e)
        {
            try
            {
                FormSubmitted?.Invoke(this, e);
                _transaction?.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                _transaction?.Finish(ex);
                SentrySdk.CaptureException(ex);
            }
            finally
            {
                _transaction = null;
            }
        }

        public async Task SetContextAsync(
            string questionnaireCanonicalUrl,
            Patient patient,
            Encounter encounter = null,
            Practitioner author = null,
            QuestionnaireResponse intitialResponse = null)
        {
            try
            {
                _transaction?.SetTag("questionnaire_url", questionnaireCanonicalUrl);

                await _initializationTask;

                var handshakeTask = _handshakeReceivedSource.Task;
                var timeoutTask = Task.Delay(30000);
                var completedTask = await Task.WhenAny(handshakeTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    var timeoutEx = new TimeoutException($"Handshake not received for {questionnaireCanonicalUrl} within 30s.");
                    SentrySdk.CaptureException(timeoutEx);
                    if (_transaction != null)
                    {
                        _transaction.Finish(SpanStatus.DeadlineExceeded);
                        _transaction = null;
                    }
                    throw timeoutEx;
                }

                await _smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(questionnaireCanonicalUrl, intitialResponse, patient, encounter, author);
            }
            catch (Exception ex)
            {
                if (_transaction != null)
                {
                    _transaction.Finish(ex);
                    _transaction = null;
                }
                SentrySdk.CaptureException(ex);
                throw;
            }
        }

        public async Task SendFormRequestSubmitAsync(Func<SmartMessageResponse, Task> responseHandler = null)
        {
            try
            {
                await _initializationTask;

                var handshakeTask = _handshakeReceivedSource.Task;
                var timeoutTask = Task.Delay(30000);
                var completedTask = await Task.WhenAny(handshakeTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    var timeoutEx = new TimeoutException("Handshake timeout during Form Request Submit.");
                    SentrySdk.CaptureException(timeoutEx);
                    if (_transaction != null)
                    {
                        _transaction.Finish(SpanStatus.DeadlineExceeded);
                        _transaction = null;
                    }
                    throw timeoutEx;
                }

                await _smartWebMessageHandler.SendFormRequestSubmitAsync(responseHandler);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                if (_transaction != null)
                {
                    _transaction.Finish(ex);
                    _transaction = null;
                }
                throw;
            }
        }

        private static string ExtractJsonField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                var searchKey = $"\"{fieldName}\"";
                var keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
                if (keyIndex < 0) return null;

                var colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
                if (colonIndex < 0) return null;

                var startQuote = json.IndexOf('"', colonIndex + 1);
                if (startQuote < 0) return null;

                var endQuote = startQuote + 1;
                while (endQuote < json.Length)
                {
                    if (json[endQuote] == '"' && json[endQuote - 1] != '\\')
                        break;
                    endQuote++;
                }

                if (endQuote >= json.Length) return null;

                return json.Substring(startQuote + 1, endQuote - startQuote - 1);
            }
            catch
            {
                return null;
            }
        }
    }
}
