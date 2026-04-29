using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message;

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
        private ITelemetrySink _telemetry;
        private bool _ownsTelemetrySink;

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

        // Telemetry session — one per viewer lifetime. All transactions started via
        // _session share the same trace id, so Sentry's trace view groups them.
        private ITelemetrySession _session;

        // Set inside OnBrowserMessageReceived for inbound notification messages so
        // OnFormSubmitted can mark outcome-aware status on the active receive transaction.
        // Read/written only on the WinForms UI thread (WebView2 dispatches inbound messages
        // serially), so no Interlocked is needed.
        private ITelemetrySpan _currentReceiveTransaction;

        // Explicit lifecycle state. Backed by int so Interlocked CAS/Exchange can transition
        // it atomically. Reads go through Volatile.Read for visibility across threads.
        private int _state = (int)TiroFormViewerState.Initializing;

        /// <summary>Current lifecycle state. See <see cref="TiroFormViewerState"/>.</summary>
        public TiroFormViewerState State => (TiroFormViewerState)Volatile.Read(ref _state);

        /// <summary>CAS transition: only moves state if currently equals <paramref name="from"/>.</summary>
        private bool TryTransition(TiroFormViewerState from, TiroFormViewerState to)
            => Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

        /// <summary>
        /// Advances to <paramref name="to"/> unless already <see cref="TiroFormViewerState.Disposed"/>
        /// (which is terminal). Returns the previous state.
        /// </summary>
        private TiroFormViewerState AdvanceUnlessDisposed(TiroFormViewerState to)
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (current == (int)TiroFormViewerState.Disposed) return TiroFormViewerState.Disposed;
                if (Interlocked.CompareExchange(ref _state, (int)to, current) == current)
                    return (TiroFormViewerState)current;
            }
        }

        /// <summary>Unconditional transition to Disposed; returns the previous state.</summary>
        private TiroFormViewerState MarkDisposed()
            => (TiroFormViewerState)Interlocked.Exchange(ref _state, (int)TiroFormViewerState.Disposed);

        /// <summary>Fast-path guard for <see cref="SetContextAsync"/>.</summary>
        private void GuardCanSetContext()
        {
            switch (State)
            {
                case TiroFormViewerState.Disposed:
                    throw new ObjectDisposedException(GetType().Name);
                case TiroFormViewerState.Submitted:
                    throw new InvalidOperationException(
                        "Cannot set context: the form has already been submitted. Create a new viewer for a second form.");
                case TiroFormViewerState.ContextSet:
                    throw new InvalidOperationException(
                        "Context has already been set on this viewer. Create a new viewer for a second form.");
                    // Initializing and Ready are both valid; SetContextAsync internally
                    // awaits handshake if still Initializing.
            }
        }

        /// <summary>Fast-path guard for <see cref="SendFormRequestSubmitAsync"/>.</summary>
        private void GuardCanSendFormRequest()
        {
            switch (State)
            {
                case TiroFormViewerState.Disposed:
                    throw new ObjectDisposedException(GetType().Name);
                case TiroFormViewerState.Submitted:
                    throw new InvalidOperationException("The form has already been submitted.");
                    // Initializing, Ready, and ContextSet are all valid.
            }
        }

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
            // IMPORTANT: all FHIR/telemetry references must stay in InitializeRuntime(), NOT here.
            // The JIT resolves every type referenced in this method body before executing any code,
            // so even an early return cannot guard against types referenced further down in this method.
            if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
                return;
            _browser = CreateBrowser();
            _smartWebMessageHandler = CreateMessageHandler();
            _telemetry = CreateTelemetrySink();
            _ownsTelemetrySink = true;
            InitializeRuntime();
        }

        /// <summary>
        /// DI ctor for tests and advanced consumers. Bypasses the factory methods —
        /// dependencies are injected directly. Not used by the designer. The injected
        /// <paramref name="telemetry"/> sink (if any) is NOT disposed by this control;
        /// that ownership stays with the caller. Pass <c>null</c> to fall back to
        /// <see cref="CreateTelemetrySink"/>.
        /// </summary>
        protected TiroFormViewer(
            IEmbeddedBrowser browser,
            SmartMessageHandlerBase<TResource, TQR, TOO> handler,
            ITelemetrySink telemetry = null)
        {
            InitializeComponent();
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _smartWebMessageHandler = handler ?? throw new ArgumentNullException(nameof(handler));
            if (telemetry != null)
            {
                _telemetry = telemetry;
                _ownsTelemetrySink = false;
            }
            else
            {
                _telemetry = CreateTelemetrySink();
                _ownsTelemetrySink = true;
            }
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

        /// <summary>
        /// Constructs the telemetry sink. Default returns <see cref="NullTelemetrySink.Instance"/> —
        /// the core library is telemetry-free unless overridden. The R5/R4 closed bindings
        /// override this to plug in <c>SentryTelemetrySink</c> from the
        /// <c>Tiro.Health.FormFiller.WebView2.Sentry</c> package.
        /// </summary>
        protected virtual ITelemetrySink CreateTelemetrySink() => NullTelemetrySink.Instance;

        private void InitializeRuntime()
        {
            _session = _telemetry.BeginSession(Guid.NewGuid().ToString());
            _session.AddBreadcrumb("lifecycle", "TiroFormViewer constructed");

            // Propagate the session's Sentry trace header into every outbound SMART
            // Web Messaging envelope as _meta.sentry.trace, so the JS Sentry SDK in the
            // embedded page can continue the trace and its spans land alongside the .NET
            // spans in the same trace.
            _smartWebMessageHandler.MetaProvider = _ =>
            {
                var trace = _session?.GetSentryTraceHeader();
                if (string.IsNullOrEmpty(trace)) return null;
                return new MessageMeta { Sentry = new SentryTraceMeta { Trace = trace } };
            };

            _smartWebMessageHandler.HandshakeReceived += OnHandshakeReceived;
            _smartWebMessageHandler.FormSubmitted += OnFormSubmitted;
            _smartWebMessageHandler.CloseApplication += OnCloseApplication;

            _browser.MessageReceived += OnBrowserMessageReceived;
            _browser.Control.Dock = DockStyle.Fill;
            this.Controls.Add(_browser.Control);

            _initializationTask = InitializeBrowserAsync();
        }

        /// <summary>
        /// Called from Dispose to close the telemetry session and flush pending events.
        /// </summary>
        internal void EndTelemetrySession()
        {
            MarkDisposed();
            try { _session?.AddBreadcrumb("lifecycle", "TiroFormViewer disposed"); } catch { /* best-effort */ }
            try { _session?.Dispose(); } catch { /* best-effort */ }
            try { _telemetry?.Flush(TimeSpan.FromSeconds(1.0)); } catch { /* best-effort */ }
        }

        private async Task InitializeBrowserAsync()
        {
            var initSpan = _session?.StartTransaction("Initialize WebView", "swm.lifecycle.init");

            try
            {
                await _browser.InitializeAsync();

                // Inject host telemetry config as window.__tiroSentryConfig before the page
                // runs any of its own scripts. The bridge below consumes this to bootstrap
                // its Sentry SDK with the host's DSN/env/release and to set the sentry-trace
                // meta tag so the pageload transaction inherits the .NET trace from the
                // very first span (rather than after the handshake response).
                var bootstrap = _session?.GetEmbeddedBootstrapConfig();
                if (bootstrap != null && bootstrap.Count > 0)
                {
                    var configJson = System.Text.Json.JsonSerializer.Serialize(bootstrap);
                    var bootstrapScript = "window.__tiroSentryConfig=" + configJson + ";";
                    await _browser.AddInitializationScriptAsync(bootstrapScript);
                }

                // Inject the SMART Web Messaging bridge — owns protocol, transport,
                // telemetry instrumentation, and <tiro-form-filler> auto-wiring on the
                // page side. Page is UI-only; it interacts via window.tiro, the
                // <tiro-form-filler> element's events, and document tiro-* CustomEvents.
                await _browser.AddInitializationScriptAsync(BridgeJs.SwmBridge);

                _smartWebMessageHandler.SendMessage = (string jsonMessage) =>
                {
                    if (State != TiroFormViewerState.Disposed)
                        _browser.PostMessage(jsonMessage);
                    return Task.FromResult("");
                };

                var contentFolder = !string.IsNullOrEmpty(WebContentFolder)
                    ? WebContentFolder
                    : DefaultWebContent.FolderPath;

                _browser.MapVirtualHost(VirtualHostName, contentFolder);
                _browser.Navigate(new Uri($"https://{VirtualHostName}/index.html"));

                initSpan?.Finish(TelemetrySpanStatus.Ok);
            }
            catch (Exception ex)
            {
                initSpan?.Finish(ex);
                _telemetry.CaptureException(ex);
                throw;
            }
        }

        private void OnBrowserMessageReceived(object sender, string inboundJson)
        {
            if (State == TiroFormViewerState.Disposed) return;
            if (string.IsNullOrEmpty(inboundJson)) return;

            // Responses to our outbound sends carry responseToMessageId; the original send's
            // wrapped response handler (registered by Send*Async below) will finish that send's
            // transaction. We don't start a new transaction for responses — they'd just clutter
            // the trace.
            var responseToMessageId = JsonProbe.ExtractStringField(inboundJson, "responseToMessageId");
            if (!string.IsNullOrEmpty(responseToMessageId))
            {
                try { _smartWebMessageHandler?.HandleMessage(inboundJson); }
                catch (Exception ex) { _telemetry.CaptureException(ex); }
                return;
            }

            // Inbound notification (status.handshake, form.submitted, ui.done, ...) — start a
            // dedicated swm.receive transaction and stash it so OnFormSubmitted can set an
            // outcome-aware status on it before the receive completes.
            var messageType = JsonProbe.ExtractStringField(inboundJson, "messageType") ?? "unknown";
            var transaction = _session?.StartTransaction(messageType, "swm.receive");
            transaction?.SetTag("messageType", messageType);
            transaction?.SetExtra("message", inboundJson);
            _currentReceiveTransaction = transaction;

            try
            {
                var responseJson = _smartWebMessageHandler?.HandleMessage(inboundJson);

                if (!string.IsNullOrEmpty(responseJson) && State != TiroFormViewerState.Disposed)
                {
                    var responseSpan = transaction?.StartChild("swm.send", "response");
                    responseSpan?.SetExtra("message", responseJson);
                    responseSpan?.Finish(TelemetrySpanStatus.Ok);
                    _browser.PostMessage(responseJson);
                }

                // OnFormSubmitted may have already finished the transaction with an outcome-aware
                // status; ITelemetrySpan.Finish is required to be idempotent (subsequent calls
                // are no-ops), so this is safe.
                transaction?.Finish(TelemetrySpanStatus.Ok);
            }
            catch (Exception ex)
            {
                transaction?.Finish(ex);
                _telemetry.CaptureException(ex);
            }
            finally
            {
                _currentReceiveTransaction = null;
            }
        }

        private void OnHandshakeReceived(object sender, EventArgs e)
        {
            TryTransition(TiroFormViewerState.Initializing, TiroFormViewerState.Ready);
            _handshakeReceivedSource.TrySetResult(true);
            _session?.AddBreadcrumb("lifecycle", "Handshake received");
        }

        private void OnCloseApplication(object sender, CloseApplicationEventArgs e)
        {
            CloseApplication?.Invoke(this, e);
        }

        private void OnFormSubmitted(object sender, FormSubmittedEventArgs<TQR, TOO> e)
        {
            // Advance to Submitted from any non-terminal state. Preserves Disposed if the
            // handler races with Dispose (terminal invariant).
            AdvanceUnlessDisposed(TiroFormViewerState.Submitted);

            var success = IsOutcomeSuccessful(e.Outcome);
            _session?.AddBreadcrumb("lifecycle", success ? "Form submitted (success)" : "Form submitted (validation errors)");

            // We're inside HandleMessage which is inside OnBrowserMessageReceived — the active
            // receive transaction is _currentReceiveTransaction. Mark it with the outcome-aware
            // status now; OnBrowserMessageReceived's final Finish(Ok) will be a no-op.
            try
            {
                FormSubmitted?.Invoke(this, e);
                _currentReceiveTransaction?.Finish(success ? TelemetrySpanStatus.Ok : TelemetrySpanStatus.InvalidArgument);
            }
            catch (Exception ex)
            {
                _currentReceiveTransaction?.Finish(ex);
                _telemetry.CaptureException(ex);
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
            GuardCanSetContext();

            var span = _session?.StartTransaction("sdc.displayQuestionnaire", "swm.send");
            span?.SetTag("messageType", "sdc.displayQuestionnaire");
            span?.SetTag("questionnaire_url", questionnaireCanonicalUrl);

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            {
                linkedCts.CancelAfter(HandshakeTimeoutMs);
                try
                {
                    await _initializationTask.WaitAsync(linkedCts.Token);
                    await WaitForHandshakeAsync(span, linkedCts.Token, cancellationToken,
                        timeoutMessage: $"Handshake not received for {questionnaireCanonicalUrl} within 30s.");

                    var wrappedHandler = WrapForRoundTrip(span, cancellationToken, originalHandler: null);

                    await _smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(
                        questionnaireCanonicalUrl: questionnaireCanonicalUrl,
                        questionnaireResponse: intitialResponse,
                        patient: patient,
                        encounter: encounter,
                        author: author,
                        responseHandler: wrappedHandler,
                        cancellationToken: linkedCts.Token);

                    // Ready → ContextSet on successful send. If Dispose / Submit raced in,
                    // the CAS fails silently — we leave the terminal state in place.
                    TryTransition(TiroFormViewerState.Ready, TiroFormViewerState.ContextSet);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
                {
                    span?.Finish(TelemetrySpanStatus.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    span?.Finish(ex);
                    _telemetry.CaptureException(ex);
                    throw;
                }
            }
        }

        public async Task SendFormRequestSubmitAsync(
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            GuardCanSendFormRequest();

            var span = _session?.StartTransaction("ui.form.requestSubmit", "swm.send");
            span?.SetTag("messageType", "ui.form.requestSubmit");

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            {
                linkedCts.CancelAfter(HandshakeTimeoutMs);
                try
                {
                    await _initializationTask.WaitAsync(linkedCts.Token);
                    await WaitForHandshakeAsync(span, linkedCts.Token, cancellationToken,
                        timeoutMessage: "Handshake timeout during Form Request Submit.");

                    var wrappedHandler = WrapForRoundTrip(span, cancellationToken, originalHandler: responseHandler);

                    await _smartWebMessageHandler.SendFormRequestSubmitAsync(wrappedHandler, linkedCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
                {
                    span?.Finish(TelemetrySpanStatus.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    span?.Finish(ex);
                    _telemetry.CaptureException(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Wraps a caller-supplied (or null) response handler so the supplied <paramref name="span"/>
        /// is finished when the response arrives, when caller cancellation fires, or when the
        /// viewer's lifetime ends. Multi-finish is safe per the <see cref="ITelemetrySpan"/> contract.
        /// Cancellation registrations live on <see cref="CancellationToken"/>s that outlive
        /// the <c>using</c> block of <c>SetContextAsync</c>/<c>SendFormRequestSubmitAsync</c>
        /// (the user's token and <c>_lifetimeCts.Token</c>), so a late response can still finish
        /// the span correctly.
        /// </summary>
        private Func<SmartMessageResponse, Task> WrapForRoundTrip(
            ITelemetrySpan span,
            CancellationToken userToken,
            Func<SmartMessageResponse, Task> originalHandler)
        {
            if (span == null) return originalHandler;

            var lifetimeReg = _lifetimeCts.Token.Register(() =>
            {
                try { span.Finish(TelemetrySpanStatus.Cancelled); } catch { /* best-effort */ }
            });

            CancellationTokenRegistration userReg = default;
            if (userToken.CanBeCanceled)
            {
                userReg = userToken.Register(() =>
                {
                    try { span.Finish(TelemetrySpanStatus.Cancelled); } catch { /* best-effort */ }
                });
            }

            return async response =>
            {
                try { lifetimeReg.Dispose(); } catch { /* ignore */ }
                try { userReg.Dispose(); } catch { /* ignore */ }

                try
                {
                    if (originalHandler != null)
                        await originalHandler(response);
                    span.Finish(TelemetrySpanStatus.Ok);
                }
                catch (Exception ex)
                {
                    try { span.Finish(ex); } catch { /* best-effort */ }
                    throw;
                }
            };
        }

        /// <summary>
        /// Awaits the handshake task, observing the linked cancellation source (user token + lifetime + 30s timeout).
        /// Distinguishes the three cancellation sources so cancellation rethrows, lifetime disposal rethrows,
        /// and the bare timeout is translated to a <see cref="TimeoutException"/> with the supplied message.
        /// On a bare timeout, finishes the supplied <paramref name="sendSpan"/> with DeadlineExceeded so the
        /// outbound transaction is closed before the exception bubbles.
        /// </summary>
        private async Task WaitForHandshakeAsync(ITelemetrySpan sendSpan, CancellationToken linkedToken, CancellationToken userToken, string timeoutMessage)
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
                _telemetry.CaptureException(timeoutEx);
                sendSpan?.Finish(TelemetrySpanStatus.DeadlineExceeded);
                throw timeoutEx;
            }
        }
    }
}
