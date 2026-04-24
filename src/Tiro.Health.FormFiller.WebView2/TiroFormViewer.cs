using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message;
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

        // Track if control is disposed. volatile so writes on the UI thread during Dispose
        // are immediately visible to the WebView2 message callback and any async continuations
        // that use it as a fast-path skip guard.
        private volatile bool _isDisposed = false;

        // Cancelled in Dispose; linked into every async operation so in-flight waits
        // observe control teardown and fail fast with OperationCanceledException.
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        // Default deadline for handshake waits, applied on top of any caller-supplied token.
        private const int HandshakeTimeoutMs = 30000;

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
                var tx = Interlocked.Exchange(ref _transaction, null);
                tx?.Finish(SpanStatus.InternalError);
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
                    // Snapshot the transaction once — another thread may null it between
                    // the null-check and StartChild otherwise (double-finish race).
                    var tx = _transaction;
                    if (!_isDisposed && tx != null)
                    {
                        var messageType = JsonProbe.ExtractStringField(jsonMessage, "messageType");
                        var spanName = !string.IsNullOrEmpty(messageType) ? messageType : "outbound";

                        var span = tx.StartChild("sdc.send", spanName);
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

            var messageType = JsonProbe.ExtractStringField(inboundJson, "messageType");
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
            // Consume the transaction atomically — if Dispose or an async catch already
            // claimed it, tx is null here and the span is already finished.
            var tx = Interlocked.Exchange(ref _transaction, null);
            try
            {
                FormSubmitted?.Invoke(this, e);
                var status = IsOutcomeSuccessful(e.Outcome) ? SpanStatus.Ok : SpanStatus.InvalidArgument;
                tx?.Finish(status);
            }
            catch (Exception ex)
            {
                tx?.Finish(ex);
                SentrySdk.CaptureException(ex);
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
            TQR intitialResponse = default,
            CancellationToken cancellationToken = default)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            {
                linkedCts.CancelAfter(HandshakeTimeoutMs);
                try
                {
                    _transaction?.SetTag("questionnaire_url", questionnaireCanonicalUrl);

                    await _initializationTask.WaitAsync(linkedCts.Token);
                    await WaitForHandshakeAsync(linkedCts.Token, cancellationToken,
                        timeoutMessage: $"Handshake not received for {questionnaireCanonicalUrl} within 30s.");

                    await _smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(
                        questionnaireCanonicalUrl: questionnaireCanonicalUrl,
                        questionnaireResponse: intitialResponse,
                        patient: patient,
                        encounter: encounter,
                        author: author,
                        cancellationToken: linkedCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _transaction, null)?.Finish(SpanStatus.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _transaction, null)?.Finish(ex);
                    SentrySdk.CaptureException(ex);
                    throw;
                }
            }
        }

        public async Task SendFormRequestSubmitAsync(
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            {
                linkedCts.CancelAfter(HandshakeTimeoutMs);
                try
                {
                    await _initializationTask.WaitAsync(linkedCts.Token);
                    await WaitForHandshakeAsync(linkedCts.Token, cancellationToken,
                        timeoutMessage: "Handshake timeout during Form Request Submit.");

                    await _smartWebMessageHandler.SendFormRequestSubmitAsync(responseHandler, linkedCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _transaction, null)?.Finish(SpanStatus.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _transaction, null)?.Finish(ex);
                    SentrySdk.CaptureException(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Awaits the handshake task, observing the linked cancellation source (user token + lifetime + 30s timeout).
        /// Distinguishes the three cancellation sources so cancellation rethrows, lifetime disposal rethrows,
        /// and the bare timeout is translated to a <see cref="TimeoutException"/> with the supplied message.
        /// </summary>
        private async Task WaitForHandshakeAsync(CancellationToken linkedToken, CancellationToken userToken, string timeoutMessage)
        {
            try
            {
                await _handshakeReceivedSource.Task.WaitAsync(linkedToken);
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                var timeoutEx = new TimeoutException(timeoutMessage);
                SentrySdk.CaptureException(timeoutEx);
                Interlocked.Exchange(ref _transaction, null)?.Finish(SpanStatus.DeadlineExceeded);
                throw timeoutEx;
            }
        }
    }
}
