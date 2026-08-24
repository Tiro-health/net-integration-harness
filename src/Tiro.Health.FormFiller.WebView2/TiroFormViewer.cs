using System;
using System.Reflection;
using System.Collections.Generic;
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
using Tiro.Health.SmartWebMessaging.Message.Payload;

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
        public event EventHandler<FormDirtyChangedEventArgs> FormDirtyChanged;

        /// <summary>
        /// The embedded page rejected one of the host's requests — its handler threw, or it
        /// did not recognise the message type. These failures used to be silent: a send
        /// completes once the message is posted, and nothing inspected the response, so a
        /// request that the page refused looked successful. Subscribe to log or alert;
        /// the failure is also captured to telemetry.
        /// </summary>
        public event EventHandler<PageErrorEventArgs> PageError;

        /// <summary>
        /// Optional folder containing a consumer-supplied <c>index.html</c> (and any supporting assets).
        /// When null, the <c>index.html</c> shipped with this package is used.
        /// The value is read once, at the first <see cref="SetContextAsync"/> call (the point the page is
        /// navigated), so set it any time before then — an object initializer or <c>Form_Load</c> both work.
        /// Setting it after the first <see cref="SetContextAsync"/> has no effect.
        /// </summary>
        public string WebContentFolder { get; set; }

        /// <summary>
        /// The <c>sdc-endpoint-address</c> attribute applied to every <c>&lt;tiro-form-filler&gt;</c>
        /// on the embedded page. The bridge sets this before <c>tiro-web-sdk</c> initializes the
        /// element, overwriting any value baked into <c>index.html</c>. The closed bindings
        /// (<c>TiroFormViewerR5</c>/<c>R4</c>) seed this with the Tiro-hosted SDC server in
        /// their constructors so out-of-the-box use works; hosts override before
        /// <see cref="SetContextAsync"/> to point at a different server.
        /// </summary>
        public string SdcEndpointAddress { get; set; }

        /// <summary>
        /// Optional override for the <c>data-endpoint-address</c> attribute on every
        /// <c>&lt;tiro-form-filler&gt;</c> element. Unlike <see cref="SdcEndpointAddress"/>,
        /// this has no default — set it when the form needs to reach a data server (e.g.
        /// hospital-hosted FHIR data store). Set before <see cref="SetContextAsync"/>.
        /// </summary>
        public string DataEndpointAddress { get; set; }

        /// <summary>
        /// The <c>read-only</c> attribute applied to every <c>&lt;tiro-form-filler&gt;</c> on the
        /// embedded page, overwriting any value baked into <c>index.html</c>. Renders the form
        /// view-only — no answer can be changed. Defaults to <c>false</c>.
        /// <para>
        /// Read once when the <c>sdc.configure</c> payload is built, so set it before
        /// <see cref="SetContextAsync"/> — setting it afterwards has no effect. A viewer cannot
        /// be flipped between editable and view-only mid-session; use one viewer per role
        /// (the form component locks itself after a final submit regardless of this property;
        /// a saved draft leaves it editable so the user can carry on).
        /// </para>
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// The telemetry sink the viewer uses for instrumentation. Resolved at construction
        /// time from <see cref="CreateTelemetrySink"/> — which on the base implementation
        /// reads <see cref="TiroFormViewerDefaults.TelemetrySinkFactory"/>, defaulting to
        /// <see cref="NullTelemetrySink"/> when no factory is registered. To enable Sentry
        /// telemetry, install <c>Tiro.Health.FormFiller.WebView2.Sentry</c> and call
        /// <c>TiroFormFillerSentry.UseSentry()</c> once at application startup, before any
        /// viewer is constructed.
        /// </summary>
        public ITelemetrySink TelemetrySink => _telemetry;

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

        // Cache key for the navigated page. The assembly's informational version, which the
        // publish workflow sets from the release tag.
        private static readonly string HarnessVersion =
            typeof(TiroFormViewer<TResource, TQR, TOO>).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(TiroFormViewer<TResource, TQR, TOO>).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // Tracks if WebView is initialized
        private Task _initializationTask;

        // Guards one-time navigation to the content folder. Navigation is deferred out of the
        // eager (constructor-started) InitializeBrowserAsync into the first SetContextAsync, so
        // WebContentFolder is read at a deterministic, caller-controlled point rather than
        // whenever the async WebView2 init happens to finish — which was a race with callers
        // that set the folder in Form_Load. 0 = not navigated, 1 = navigated.
        private int _navigated;

        // Track if handshake has been received
        private readonly TaskCompletionSource<bool> _handshakeReceivedSource =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Terminal web-sdk failure (GH-61). Also faulted into the TCS, but kept as a
        // field so a bad handshake AFTER a successful one still fails later operations.
        private volatile Exception _webSdkFailure;

        // Telemetry session — one per viewer lifetime. All transactions started via
        // _session share the same trace id, so Sentry's trace view groups them.
        private ITelemetrySession _session;

        // Set inside OnBrowserMessageReceived for inbound notification messages so
        // OnFormSubmitted can mark outcome-aware status on the active receive transaction.
        // Read/written only on the WinForms UI thread (WebView2 dispatches inbound messages
        // serially). A nested inbound (caused by a FormSubmitted subscriber pumping the
        // message loop, e.g. MessageBox.Show) will overwrite then null the field — that's
        // safe because OnFormSubmitted captures the field into a local before invoking
        // the user handler, so its post-pump Finish call still sees the right span.
        private ITelemetrySpan _currentReceiveTransaction;

        // Explicit lifecycle state. Backed by int so Interlocked CAS/Exchange can transition
        // it atomically. Reads go through Volatile.Read for visibility across threads.
        private int _state = (int)TiroFormViewerState.Initializing;

        /// <summary>Current lifecycle state. See <see cref="TiroFormViewerState"/>.</summary>
        public TiroFormViewerState State => (TiroFormViewerState)Volatile.Read(ref _state);

        /// <summary>
        /// The tiro-web-sdk version the page reported at handshake, or null when the running
        /// SDK predates the version field (atticus-frontend#2927). Diagnostics only — it is
        /// not asserted against; see <c>build/web-sdk/README.md</c>.
        /// </summary>
        public string PageWebSdkVersion { get; private set; }

        /// <summary>
        /// Whether the user has made any changes to the displayed form since it loaded.
        /// Kept in sync from the page's <c>ui.form.dirtyChanged</c> notifications; also
        /// raised as <see cref="FormDirtyChanged"/>. Pre-populated/auto-<c>$populate</c>d
        /// answers do not count as dirty — only genuine user edits do.
        /// </summary>
        public bool IsDirty { get; private set; }

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
                case TiroFormViewerState.Initializing:
                case TiroFormViewerState.Ready:
                    // No questionnaire has been displayed yet (SetContextAsync hasn't run), so
                    // there is nothing to submit. Reject fast: since navigation is deferred to
                    // SetContextAsync, waiting here would block on a handshake that can never
                    // arrive and surface as a misleading 30s "handshake timeout".
                    throw new InvalidOperationException(
                        "Cannot submit before a form is displayed. Call SetContextAsync first.");
                    // Only ContextSet is valid.
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
        /// The telemetry sink is resolved via <see cref="CreateTelemetrySink"/>, which on the base
        /// reads <see cref="TiroFormViewerDefaults.TelemetrySinkFactory"/> — so applications opt
        /// into telemetry once at startup (e.g. <c>TiroFormFillerSentry.UseSentry()</c>) and every
        /// Designer-placed viewer picks it up. The session begins eagerly so init/handshake
        /// telemetry is captured before the first <see cref="SetContextAsync"/>.
        /// </summary>
        protected TiroFormViewer()
        {
            InitializeComponent();
            // Skip all runtime initialization at design time.
            // IMPORTANT: all FHIR/telemetry references must stay in InitializeWiring(), NOT here.
            // The JIT resolves every type referenced in this method body before executing any code,
            // so even an early return cannot guard against types referenced further down in this method.
            if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
                return;
            _browser = CreateBrowser();
            _smartWebMessageHandler = CreateMessageHandler();
            _telemetry = CreateTelemetrySink();
            _ownsTelemetrySink = true;
            // BeginSession must run BEFORE InitializeWiring: the latter kicks off
            // InitializeBrowserAsync, whose synchronous prefix emits a "swm.lifecycle.init"
            // span. The session has to be alive by then for that span to be recorded.
            BeginSession();
            InitializeWiring();
        }

        /// <summary>
        /// DI ctor for tests and advanced consumers. Bypasses the factory methods —
        /// dependencies are injected directly. Not used by the designer. The injected
        /// <paramref name="telemetry"/> sink (if any) is NOT disposed by this control;
        /// that ownership stays with the caller. Pass <c>null</c> to fall back to
        /// <see cref="CreateTelemetrySink"/>. The session begins eagerly to match the
        /// parameterless ctor's contract.
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
            BeginSession();
            InitializeWiring();
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
        protected virtual ITelemetrySink CreateTelemetrySink()
            => TiroFormViewerDefaults.TelemetrySinkFactory?.Invoke() ?? NullTelemetrySink.Instance;

        private void BeginSession()
        {
            _session = _telemetry.BeginSession(Guid.NewGuid().ToString());
            _session.AddBreadcrumb("lifecycle", "TiroFormViewer constructed");
        }

        private void InitializeWiring()
        {
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
            _smartWebMessageHandler.FormDirtyChanged += OnFormDirtyChanged;

            _browser.MessageReceived += OnBrowserMessageReceived;
            _browser.Control.Dock = DockStyle.Fill;
            this.Controls.Add(_browser.Control);

            _initializationTask = InitializeBrowserAsync();
            // Observe faults so a viewer that's constructed and disposed without ever
            // being awaited (e.g. WebView2 runtime missing → init throws → form closes
            // before SetContextAsync is called) doesn't trip TaskScheduler.UnobservedTaskException.
            // Touching .Exception marks it observed; SetContextAsync still surfaces the
            // fault when it awaits the task.
            _initializationTask.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

                // Pre-warm the ~6MB SDK extraction off the UI thread so the first
                // SetContextAsync doesn't pay it synchronously. Failures are cached by
                // the Lazy and resurface with context at NavigateToContent.
                _ = Task.Run(() => { try { _ = WebSdkAssets.FolderPath; } catch { /* rethrown on use */ } });

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

                // Note: endpoint config (SdcEndpointAddress, DataEndpointAddress) is no
                // longer pre-injected here. It now travels as a protocol-conformant
                // sdc.configure message sent after handshake — see SetContextAsync.

                // Tell the bridge where to load the embedded web-sdk from. Injected rather
                // than hardcoded because the file name carries the SDK version (cache-busting
                // across pin bumps) and the bridge is a static asset. Must precede the
                // bridge, which reads it at module scope.
                await _browser.AddInitializationScriptAsync(
                    "window.__tiroSdkUrl=" + System.Text.Json.JsonSerializer.Serialize(WebSdkAssets.BundleUrl) + ";");

                // Inject the SMART Web Messaging bridge — owns protocol, transport,
                // telemetry instrumentation, and <tiro-form-filler> auto-wiring on the
                // page side. Page is UI-only; it interacts via window.tiro, the
                // <tiro-form-filler> element's events, and document tiro-* CustomEvents.
                await _browser.AddInitializationScriptAsync(BridgeJs.SwmBridge);

                _smartWebMessageHandler.SendMessage = (string jsonMessage) =>
                {
                    if (State != TiroFormViewerState.Disposed)
                        _browser.PostMessage(jsonMessage);
                    return Task.CompletedTask;
                };

                // Navigation (MapVirtualHost + Navigate) is intentionally NOT done here. It's
                // deferred to the first SetContextAsync via NavigateToContent() so WebContentFolder
                // is read at a deterministic, caller-controlled point. Doing it here would read the
                // folder whenever this async init happened to finish — a race with callers that set
                // WebContentFolder in Form_Load.
                initSpan?.Finish(TelemetrySpanStatus.Ok);
            }
            catch (Exception ex)
            {
                initSpan?.Finish(ex);
                _telemetry.CaptureException(ex);
                throw;
            }
        }

        // Maps the virtual host to the resolved content folder and navigates to index.html.
        // Idempotent: only the first call navigates (a SetContextAsync retried after a handshake
        // timeout must not reload the page). Reads WebContentFolder here — at the first
        // SetContextAsync — so any value set before that call (object initializer, Form_Load, …)
        // is guaranteed to take effect.
        private void NavigateToContent()
        {
            if (Interlocked.CompareExchange(ref _navigated, 1, 0) != 0) return;

            try
            {
                var contentFolder = !string.IsNullOrEmpty(WebContentFolder)
                    ? WebContentFolder
                    : DefaultWebContent.FolderPath;

                _browser.MapVirtualHost(VirtualHostName, contentFolder);
                // Embedded web-sdk on its own host, mapped before Navigate. DenyCors is fine
                // for a plain <script src>; the bridge must not add a crossorigin attribute.
                _browser.MapVirtualHost(WebSdkAssets.VirtualHostName, WebSdkAssets.FolderPath);
                // ?v=<harness version> for the same reason the SDK's file name carries its
                // version: WebView2 caches by URL and virtual-host responses carry no cache
                // headers, so at a constant URL an upgraded harness could load the previous
                // release's page. That matters concretely — a cached pre-GH-60 page still
                // carries a CDN <script> tag, which now collides with the injected SDK. A
                // query string busts the cache without renaming anyone's files, so it works
                // for a consumer-supplied WebContentFolder too.
                _browser.Navigate(new Uri($"https://{VirtualHostName}/index.html?v={Uri.EscapeDataString(HarnessVersion)}"));
            }
            catch
            {
                // Roll back so a retried SetContextAsync re-attempts navigation and
                // surfaces the real cause instead of a handshake timeout.
                Interlocked.Exchange(ref _navigated, 0);
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
            // Deliberately NOT attaching the raw message JSON here. SMART Web Messaging
            // payloads carry FHIR resources (Patient in launch context, full
            // QuestionnaireResponse on form.submitted, etc.); putting them on a Sentry
            // span would exfiltrate PHI to whichever Sentry project the sink is wired
            // up to. messageType + tracing + timing + exceptions stay enough to diagnose
            // the vast majority of integration issues; if you need payload capture for
            // dev work, do it in a custom ITelemetrySink in your own (non-shared) project.
            _currentReceiveTransaction = transaction;

            try
            {
                var responseJson = _smartWebMessageHandler?.HandleMessage(inboundJson);

                if (!string.IsNullOrEmpty(responseJson) && State != TiroFormViewerState.Disposed)
                {
                    var responseSpan = transaction?.StartChild("swm.send", "response");
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

        private void OnHandshakeReceived(object sender, HandshakeReceivedEventArgs e)
        {
            var (reported, source) = ExtractClient(e?.Payload);
            PageWebSdkVersion = reported;

            // A refused session is terminal: keep answering repeat handshakes with the
            // same error so the page never sees a success ack (tiro-connected).
            var existing = _webSdkFailure;
            if (existing != null) throw existing;

            var failure = EvaluateWebSdkReport(source);
            if (failure != null)
            {
                _webSdkFailure = failure;
                _telemetry.CaptureException(failure);
                _session?.AddBreadcrumb("lifecycle", "Handshake rejected: " + failure.Message);
                // Awaiters of the handshake throw instead of proceeding; state stays
                // Initializing. The rethrow turns the ack into an error response so the
                // page fires tiro-disconnected rather than tiro-connected.
                _handshakeReceivedSource.TrySetException(failure);
                throw failure;
            }

            TryTransition(TiroFormViewerState.Initializing, TiroFormViewerState.Ready);
            _handshakeReceivedSource.TrySetResult(true);
            _session?.AddBreadcrumb("lifecycle",
                reported == null ? "Handshake received" : $"Handshake received (tiro-web-sdk {reported})");
        }

        // The page must be running the bundle we served. `source` is what proves it:
        // "collision" means the page loaded its own copy, "error" that ours never loaded.
        // The reported VERSION is not compared — the served URL carries the version, so a
        // stale bundle cannot load, and the virtual host reads from local disk with no
        // network or proxy that could substitute other bytes. See build/web-sdk/README.md.
        private Exception EvaluateWebSdkReport(string source)
        {
            if (source == "collision" || source == "error")
                return new WebSdkLoadException(source);
            return null;
        }

        private static (string Version, string Source) ExtractClient(RequestPayload payload)
        {
            if (payload?.ExtraFields == null) return (null, null);
            if (!payload.ExtraFields.TryGetValue("client", out var client)
                || client.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);
            return (client.GetStringOrNull("version"), client.GetStringOrNull("source"));
        }

        private void OnCloseApplication(object sender, CloseApplicationEventArgs e)
        {
            CloseApplication?.Invoke(this, e);
        }

        private void OnFormDirtyChanged(object sender, FormDirtyChangedEventArgs e)
        {
            IsDirty = e.IsDirty;
            FormDirtyChanged?.Invoke(this, e);
        }

        private void OnFormSubmitted(object sender, FormSubmittedEventArgs<TQR, TOO> e)
        {
            // Only a final response is terminal — see IsResponseFinal. AdvanceUnlessDisposed
            // preserves Disposed if the handler races with Dispose (terminal invariant).
            if (IsResponseFinal(e.Response)) AdvanceUnlessDisposed(TiroFormViewerState.Submitted);

            var success = IsOutcomeSuccessful(e.Outcome);
            _session?.AddBreadcrumb("lifecycle", success ? "Form submitted (success)" : "Form submitted (validation errors)");

            // We're inside HandleMessage which is inside OnBrowserMessageReceived — the
            // active receive transaction is _currentReceiveTransaction. Capture into a
            // local before invoking the user handler: if the handler pumps the message
            // loop (e.g. MessageBox.Show), a nested inbound will overwrite then null the
            // field, but our local still points at the right span. OnBrowserMessageReceived's
            // final Finish(Ok) will be a no-op since Finish is idempotent.
            var ourReceiveTransaction = _currentReceiveTransaction;
            try
            {
                FormSubmitted?.Invoke(this, e);
                ourReceiveTransaction?.Finish(success ? TelemetrySpanStatus.Ok : TelemetrySpanStatus.InvalidArgument);
            }
            catch (Exception ex)
            {
                ourReceiveTransaction?.Finish(ex);
                _telemetry.CaptureException(ex);
                // Rethrow so SmartMessageHandlerBase.HandleRequestMessage's catch turns this
                // into an error response back to the JS bridge. Without the rethrow, the
                // handler returned a base-success ack while the host-side subscriber failed,
                // and the page fired tiro-submitted as if persistence had succeeded.
                throw;
            }
        }

        /// <summary>
        /// Returns true if the submitted outcome indicates success (no error/fatal-severity issues).
        /// Default: treat all outcomes as successful. Version-specific subclasses
        /// (<c>TiroFormViewerR5</c>/<c>TiroFormViewerR4</c>) override this to call
        /// <c>OperationOutcome.Success</c>.
        /// </summary>
        protected virtual bool IsOutcomeSuccessful(TOO outcome) => true;

        /// <summary>
        /// Returns true if the submitted response ends the session — i.e. it is not a draft.
        /// A draft (<c>QuestionnaireResponse.status = in-progress</c>, produced by
        /// <c>SendFormRequestSubmitAsync(intent: "save-draft")</c>) keeps the viewer usable so
        /// the user can keep filling and submit later. Default: treat every response as final.
        /// Version-specific subclasses override this to inspect the FHIR status.
        /// </summary>
        protected virtual bool IsResponseFinal(TQR response) => true;

        /// <summary>
        /// Displays the questionnaire at <paramref name="questionnaireCanonicalUrl"/> and sets the
        /// initial launch context. <paramref name="patient"/>/<paramref name="encounter"/>/
        /// <paramref name="author"/> are shorthand for the well-known "patient"/"encounter"/"user"
        /// launch context entries; <paramref name="launchContext"/> carries any additional named
        /// resource (e.g. coverage, device, or an app-specific launch parameter) alongside them.
        /// </summary>
        public async Task SetContextAsync(
            string questionnaireCanonicalUrl,
            TResource patient = default,
            TResource encounter = default,
            TResource author = default,
            TQR initialResponse = default,
            List<LaunchContext<TResource>> launchContext = null,
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

                    // Browser environment + bridge scripts are ready; now navigate to the content
                    // folder. Deferred to here (not the eager init task) so WebContentFolder is read
                    // at this caller-controlled point — no race with hosts that set it in Form_Load.
                    // Idempotent, so a retried SetContextAsync won't reload the page.
                    NavigateToContent();

                    await WaitForHandshakeAsync(span, linkedCts.Token, cancellationToken,
                        timeoutMessage: $"Handshake not received for {questionnaireCanonicalUrl} within 30s.");

                    // After handshake, send the protocol's sdc.configure message with the
                    // endpoint addresses and renderer flags so the bridge can apply them to
                    // the form-filler before it initializes. Only emit when there's something
                    // to say. Both the SDC server and read-only land on
                    // payload.configuration (the protocol's renderer-specific extension
                    // point) — the SDC server because it isn't a terminology server in the
                    // strict SDC SWM sense, read-only because it's a renderer concern with no
                    // field of its own in the dialect. The data server maps cleanly to
                    // payload.dataServer.
                    if (!string.IsNullOrEmpty(SdcEndpointAddress) || !string.IsNullOrEmpty(DataEndpointAddress) || ReadOnly)
                    {
                        // Built per-key so each flag travels independently — read-only has to
                        // reach the page even when no endpoint override is set. Only emitted
                        // when true: false is already the page-side default, so there's
                        // nothing to assert.
                        var configurationMap = new System.Collections.Generic.Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(SdcEndpointAddress)) configurationMap["sdcServer"] = SdcEndpointAddress;
                        if (ReadOnly) configurationMap["readOnly"] = true;
                        object configuration = configurationMap.Count == 0 ? null : (object)configurationMap;

                        // sdc.configure is fire-and-forget — no response message — so the
                        // span finishes synchronously on send completion. Mirrors the JS
                        // bridge's swm.receive span so the trace shows both halves.
                        var configureSpan = _session?.StartTransaction("sdc.configure", "swm.send");
                        configureSpan?.SetTag("messageType", "sdc.configure");
                        if (!string.IsNullOrEmpty(SdcEndpointAddress)) configureSpan?.SetTag("sdc_server", SdcEndpointAddress);
                        if (!string.IsNullOrEmpty(DataEndpointAddress)) configureSpan?.SetTag("data_server", DataEndpointAddress);
                        if (ReadOnly) configureSpan?.SetTag("read_only", "true");

                        try
                        {
                            await _smartWebMessageHandler.SendSdcConfigureAsync(
                                terminologyServer: null,
                                dataServer: string.IsNullOrEmpty(DataEndpointAddress) ? null : DataEndpointAddress,
                                configuration: configuration,
                                cancellationToken: linkedCts.Token);
                            configureSpan?.Finish(TelemetrySpanStatus.Ok);
                        }
                        catch (OperationCanceledException)
                        {
                            configureSpan?.Finish(TelemetrySpanStatus.Cancelled);
                            throw;
                        }
                        catch
                        {
                            configureSpan?.Finish(TelemetrySpanStatus.InternalError);
                            throw;
                        }
                    }

                    var wrappedHandler = WrapForRoundTrip("sdc.displayQuestionnaire", span, cancellationToken, originalHandler: null);

                    await _smartWebMessageHandler.SendSdcDisplayQuestionnaireAsync(
                        questionnaireCanonicalUrl: questionnaireCanonicalUrl,
                        questionnaireResponse: initialResponse,
                        patient: patient,
                        encounter: encounter,
                        author: author,
                        launchContext: launchContext,
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
            string intent = null,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            GuardCanSendFormRequest();

            var span = _session?.StartTransaction("ui.form.requestSubmit", "swm.send");
            span?.SetTag("messageType", "ui.form.requestSubmit");
            span?.SetTag("intent", intent ?? "finalize");

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            {
                linkedCts.CancelAfter(HandshakeTimeoutMs);
                try
                {
                    await _initializationTask.WaitAsync(linkedCts.Token);
                    await WaitForHandshakeAsync(span, linkedCts.Token, cancellationToken,
                        timeoutMessage: "Handshake timeout during Form Request Submit.");

                    var wrappedHandler = WrapForRoundTrip("ui.form.requestSubmit", span, cancellationToken, originalHandler: responseHandler);

                    await _smartWebMessageHandler.SendFormRequestSubmitAsync(intent, wrappedHandler, linkedCts.Token);
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
        /// Uses a single linked CTS so a long-lived user token doesn't accumulate dead
        /// callbacks across many sends — every exit path disposes the CTS, which releases
        /// its registrations on both source tokens at once.
        /// </summary>
        private Func<SmartMessageResponse, Task> WrapForRoundTrip(
            string messageType,
            ITelemetrySpan span,
            CancellationToken userToken,
            Func<SmartMessageResponse, Task> originalHandler)
        {
            var sentinel = CancellationTokenSource.CreateLinkedTokenSource(userToken, _lifetimeCts.Token);
            sentinel.Token.Register(() =>
            {
                try { span?.Finish(TelemetrySpanStatus.Cancelled); } catch { /* best-effort */ }
                // Dispose-from-callback is allowed; the CTS handles re-entrancy and this
                // releases the registrations on both source tokens.
                try { sentinel.Dispose(); } catch { /* best-effort */ }
            });

            return async response =>
            {
                // Success path: dispose unregisters the cancel callback so it never fires
                // — span will be finished below based on the user handler's outcome.
                try { sentinel.Dispose(); } catch { /* best-effort */ }

                // A page that threw in its handler, or didn't recognise the message type,
                // answers with an error payload instead of an ack. Nothing used to inspect
                // it, so the request looked successful and the failure existed only in the
                // WebView console. Report before handing the response on.
                if (response?.Payload is ErrorResponse error)
                    OnPageError(messageType, error, span);

                try
                {
                    if (originalHandler != null)
                        await originalHandler(response);
                    span?.Finish(TelemetrySpanStatus.Ok);
                }
                catch (Exception ex)
                {
                    try { span?.Finish(ex); } catch { /* best-effort */ }
                    throw;
                }
            };
        }

        // Surfaces a page-side rejection three ways, because each reaches a different
        // audience: the PageError event for integrator code, telemetry for support, and an
        // error span status so the trace isn't misleadingly green.
        private void OnPageError(string messageType, ErrorResponse error, ITelemetrySpan span)
        {
            var failure = new PageOperationException(messageType, error.ErrorType, error.ErrorMessage);
            try { _telemetry.CaptureException(failure); } catch { /* best-effort */ }
            try { span?.Finish(TelemetrySpanStatus.InternalError); } catch { /* best-effort */ }
            _session?.AddBreadcrumb("lifecycle", failure.Message);
            PageError?.Invoke(this, new PageErrorEventArgs(messageType, error.ErrorType, error.ErrorMessage));
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
            // A terminal web-sdk failure beats a completed TCS: a bad handshake after a
            // successful one cannot fault the one-shot TCS, but must still fail here.
            var webSdkFailure = _webSdkFailure;
            if (webSdkFailure != null)
            {
                sendSpan?.Finish(TelemetrySpanStatus.InternalError);
                throw webSdkFailure;
            }

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
