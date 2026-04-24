using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message;
using Tiro.Health.SmartWebMessaging.Message.Payload;
using Sentry;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// FHIR-version-agnostic abstract base. Derive a closed sealed subclass
    /// (e.g. <c>TiroFormViewerR5</c>) that binds <typeparamref name="TResource"/>,
    /// <typeparamref name="TQR"/>, and <typeparamref name="TOO"/> to the concrete
    /// FHIR types and supplies the version-specific <see cref="SmartMessageHandlerBase{T,Q,O}"/>.
    /// </summary>
    public abstract partial class TiroFormViewer<TResource, TQR, TOO> : UserControl
        where TResource : Resource
    {
        public event EventHandler<FormSubmittedEventArgs<TQR, TOO>> FormSubmitted;
        public event EventHandler<CloseApplicationEventArgs> CloseApplication;

        /// <summary>
        /// Optional folder containing a consumer-supplied <c>index.html</c> (and any supporting assets).
        /// When null, the <c>index.html</c> shipped with this package is used.
        /// Set this before the control's handle is created (e.g. via object initializer, or before
        /// adding the control to its parent form).
        /// </summary>
        public string WebContentFolder { get; set; }

        private ILogger _logger = NullLogger.Instance;
        private SmartMessageHandlerBase<TResource, TQR, TOO> _smartWebMessageHandler;
        private IEmbeddedBrowser _browser;

        /// <summary>
        /// The underlying SMART Web Messaging handler. Cast to the version-specific handler type
        /// (e.g. <c>Tiro.Health.SmartWebMessaging.Fhir.R5.SmartMessageHandler</c>) to access version-specific send overloads.
        /// </summary>
        public SmartMessageHandlerBase<TResource, TQR, TOO> MessageHandler => _smartWebMessageHandler;

        private const string VirtualHostName = "appassets.example"; // https://github.com/MicrosoftEdge/WebView2Feedback/issues/2381

        // Tracks if WebView is initialized
        private Task _initializationTask;

        // Track if handshake has been received
        private readonly TaskCompletionSource<bool> _handshakeReceivedSource =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Single transaction for entire form lifecycle
        private ITransactionTracer _transaction;

        // Track if control is disposed
        private bool _isDisposed = false;

        /// <summary>
        /// Default ctor used by the WinForms designer and at runtime in closed subclasses.
        /// Construction-time dependencies (browser + handler) come from the <c>Create*</c> factory methods.
        /// </summary>
        protected TiroFormViewer()
        {
            InitializeComponent();
            // Skip all runtime initialization at design time.
            // IMPORTANT: all FHIR/Sentry references must stay in InitializeRuntime(), NOT here.
            // The JIT resolves every type referenced in this method body before executing any code,
            // so even an early return cannot guard against types referenced further down in this method.
            if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
                return;
            _browser = CreateBrowser();
            _smartWebMessageHandler = CreateMessageHandler();
            InitializeRuntime();
        }

        /// <summary>
        /// DI ctor for tests and advanced consumers. Bypasses the factory methods —
        /// both dependencies are injected directly. Not used by the designer.
        /// </summary>
        protected TiroFormViewer(IEmbeddedBrowser browser, SmartMessageHandlerBase<TResource, TQR, TOO> handler)
        {
            InitializeComponent();
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _smartWebMessageHandler = handler ?? throw new ArgumentNullException(nameof(handler));
            InitializeRuntime();
        }

        /// <summary>
        /// Constructs the version-specific <see cref="SmartMessageHandlerBase{T,Q,O}"/> for this control.
        /// Called once, during runtime initialization.
        /// </summary>
        protected abstract SmartMessageHandlerBase<TResource, TQR, TOO> CreateMessageHandler();

        /// <summary>
        /// Constructs the embedded browser adapter. Override in tests to inject a fake.
        /// Default: <see cref="WebView2EmbeddedBrowser"/>.
        /// </summary>
        protected virtual IEmbeddedBrowser CreateBrowser() => new WebView2EmbeddedBrowser();

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

            _smartWebMessageHandler.HandshakeReceived += OnHandshakeReceived;
            _smartWebMessageHandler.FormSubmitted += OnFormSubmitted;
            _smartWebMessageHandler.CloseApplication += OnCloseApplication;

            _browser.MessageReceived += OnBrowserMessageReceived;
            _browser.Control.Dock = DockStyle.Fill;
            this.Controls.Add(_browser.Control);

            _initializationTask = InitializeBrowserAsync();
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

        private async Task InitializeBrowserAsync()
        {
            var initSpan = _transaction?.StartChild("sdc.initialize", "Initialize WebView");

            try
            {
                await _browser.InitializeAsync();

                _smartWebMessageHandler.SendMessage = (string jsonMessage) =>
                {
                    if (!_isDisposed && _transaction != null)
                    {
                        var messageType = ExtractJsonField(jsonMessage, "messageType");
                        var spanName = !string.IsNullOrEmpty(messageType) ? messageType : "outbound";

                        var span = _transaction.StartChild("sdc.send", spanName);
                        span.SetExtra("message", jsonMessage);
                        span.Finish(SpanStatus.Ok);

                        _browser.PostMessage(jsonMessage);
                    }
                    return Task.FromResult("");
                };

                var contentFolder = !string.IsNullOrEmpty(WebContentFolder)
                    ? WebContentFolder
                    : DefaultWebContent.FolderPath;

                _browser.MapVirtualHost(VirtualHostName, contentFolder);
                _browser.Navigate(new Uri($"https://{VirtualHostName}/index.html"));

                initSpan?.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                initSpan?.Finish(ex);
                SentrySdk.CaptureException(ex);
                throw;
            }
        }

        private void OnBrowserMessageReceived(object sender, string inboundJson)
        {
            if (_isDisposed) return;
            if (string.IsNullOrEmpty(inboundJson)) return;

            var messageType = ExtractJsonField(inboundJson, "messageType");
            var spanName = !string.IsNullOrEmpty(messageType) ? messageType : "inbound";

            var span = _transaction?.StartChild("sdc.receive", spanName);
            span?.SetExtra("message", inboundJson);

            try
            {
                var responseJson = _smartWebMessageHandler?.HandleMessage(inboundJson);

                if (!string.IsNullOrEmpty(responseJson) && !_isDisposed)
                {
                    var responseSpan = _transaction?.StartChild("sdc.response", spanName + ".response");
                    responseSpan?.SetExtra("message", responseJson);
                    responseSpan?.Finish(SpanStatus.Ok);

                    _browser.PostMessage(responseJson);
                }

                span?.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                span?.Finish(ex);
                SentrySdk.CaptureException(ex);
            }
        }

        private void OnHandshakeReceived(object sender, EventArgs e)
        {
            _handshakeReceivedSource.TrySetResult(true);
        }

        private void OnCloseApplication(object sender, CloseApplicationEventArgs e)
        {
            CloseApplication?.Invoke(this, e);
        }

        private void OnFormSubmitted(object sender, FormSubmittedEventArgs<TQR, TOO> e)
        {
            try
            {
                FormSubmitted?.Invoke(this, e);
                var status = IsOutcomeSuccessful(e.Outcome) ? SpanStatus.Ok : SpanStatus.InvalidArgument;
                _transaction?.Finish(status);
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

        /// <summary>
        /// Returns true if the submitted outcome indicates success (no error/fatal-severity issues).
        /// Default: treat all outcomes as successful. Version-specific subclasses
        /// (<c>TiroFormViewerR5</c>/<c>TiroFormViewerR4</c>) override this to call
        /// <c>OperationOutcome.Success</c>.
        /// </summary>
        protected virtual bool IsOutcomeSuccessful(TOO outcome) => true;

        public async Task SetContextAsync(
            string questionnaireCanonicalUrl,
            TResource patient = default,
            TResource encounter = default,
            TResource author = default,
            TQR intitialResponse = default)
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

                var launchContext = BuildLaunchContext(patient, encounter, author);
                await _smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(
                    questionnaire: (object)questionnaireCanonicalUrl,
                    questionnaireResponse: intitialResponse,
                    launchContext: launchContext);
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

        private static List<LaunchContext<TResource>> BuildLaunchContext(TResource patient, TResource encounter, TResource author)
        {
            var ctx = new List<LaunchContext<TResource>>();
            if (patient != null) ctx.Add(new LaunchContext<TResource>("patient", contentResource: patient));
            if (encounter != null) ctx.Add(new LaunchContext<TResource>("encounter", contentResource: encounter));
            if (author != null) ctx.Add(new LaunchContext<TResource>("user", contentResource: author));
            return ctx.Count > 0 ? ctx : null;
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
