using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// <see cref="IEmbeddedBrowser"/> adapter over <see cref="WebView2Control"/>. Owns the
    /// underlying WinForms control. Microphone permission is auto-granted only for pages
    /// served from <see cref="TrustedMicrophoneOriginHost"/>; other origins fall through
    /// to WebView2's default-deny behaviour.
    /// </summary>
    public sealed class WebView2EmbeddedBrowser : IEmbeddedBrowser, IContextMenuCapableBrowser
    {
        // Must match TiroFormViewer.VirtualHostName — that's the host the viewer maps to
        // its content folder and navigates to. Hardcoded both sides because there is one
        // and only one virtual host in this harness.
        private const string TrustedMicrophoneOriginHost = "appassets.example";

        private readonly WebView2Control _webView2;
        private bool _coreSubscribed;
        private bool _disposed;

        // Custom context-menu items are created against the WebView2 environment, which caps
        // the number of live ones (1000) and whose docs ask for reuse across events. A menu
        // requested on every right-click would otherwise mint a new item each time, so they're
        // cached by label — the label being the identity the user sees anyway — and only the
        // action behind one is swapped before the menu is shown.
        private readonly Dictionary<string, CachedMenuItem> _menuItemCache =
            new Dictionary<string, CachedMenuItem>(StringComparer.Ordinal);
        private CoreWebView2ContextMenuItem _menuSeparator;

        private sealed class CachedMenuItem
        {
            public CoreWebView2ContextMenuItem Native;
            public Action Action;
        }

        public WebView2EmbeddedBrowser()
            : this(new WebView2Control())
        {
        }

        public WebView2EmbeddedBrowser(WebView2Control webView2)
        {
            _webView2 = webView2 ?? throw new ArgumentNullException(nameof(webView2));
            _webView2.Dock = DockStyle.Fill;
        }

        public Control Control => _webView2;

        public event EventHandler<string> MessageReceived;

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            await _webView2.EnsureCoreWebView2Async();
            if (_coreSubscribed) return;
            _webView2.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView2.CoreWebView2.PermissionRequested += OnPermissionRequested;
            _webView2.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;
            _coreSubscribed = true;
        }

        public void PostMessage(string json)
        {
            ThrowIfDisposed();
            if (_webView2.CoreWebView2 == null) return;

            // PostWebMessageAsJson is COM-thread-affine to the WebView2 control's UI
            // thread. Host-side async continuations (notably the netstandard2.0 WaitAsync
            // polyfill, which schedules on TaskScheduler.Default) can resume off the UI
            // thread, so marshal explicitly. On the UI thread this is a single property
            // check; off-UI it's fire-and-forget into the WinForms message pump, which
            // matches PostWebMessageAsJson's own fire-and-forget semantics.
            if (_webView2.InvokeRequired)
            {
                try
                {
                    _webView2.BeginInvoke((Action)(() =>
                    {
                        if (_disposed || _webView2.CoreWebView2 == null) return;
                        _webView2.CoreWebView2.PostWebMessageAsJson(json);
                    }));
                }
                catch (ObjectDisposedException) { /* lost the race with Dispose */ }
                catch (InvalidOperationException ex)
                {
                    // Handle not created — this means PostMessage was called before
                    // InitializeAsync completed, which violates the IEmbeddedBrowser
                    // contract. Surface loudly in DEBUG so the precondition violation
                    // is caught in tests; silently swallow in RELEASE so production
                    // doesn't crash on a transient WinForms hiccup.
                    Debug.Fail("PostMessage before WebView2 handle creation: " + ex.Message);
                }
            }
            else
            {
                _webView2.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        public void MapVirtualHost(string hostName, string folderPath)
        {
            ThrowIfDisposed();
            if (_webView2.CoreWebView2 == null)
                throw new InvalidOperationException("InitializeAsync must complete before MapVirtualHost.");
            // DenyCors: the page can load its own assets (same-origin) but cross-origin
            // fetch/XHR cannot read them. The form viewer's content is page-local — there
            // is no legitimate reason for an external origin to read these files.
            _webView2.CoreWebView2.SetVirtualHostNameToFolderMapping(
                hostName, folderPath, CoreWebView2HostResourceAccessKind.DenyCors);
        }

        public void Navigate(Uri url)
        {
            ThrowIfDisposed();
            if (url == null) throw new ArgumentNullException(nameof(url));
            _webView2.Source = url;
        }

        public async Task AddInitializationScriptAsync(string script)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(script)) return;
            if (_webView2.CoreWebView2 == null)
                throw new InvalidOperationException("InitializeAsync must complete before AddInitializationScriptAsync.");
            await _webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }

        /// <inheritdoc />
        public Func<TiroContextMenuContext, IReadOnlyList<EmbeddedBrowserMenuItem>> ContextMenuItemsProvider { get; set; }

        /// <summary>
        /// Appends the host's items to the browser's own context menu. <c>Handled</c> is left
        /// false on purpose: WebView2 then shows its native menu *including* what was added
        /// here, so the click never leaves the page — the caret stays where the user
        /// right-clicked and Ctrl+V still pastes there. Suppressing the menu to show a WinForms
        /// one instead would take focus out of the browser and lose that.
        /// </summary>
        private void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            if (_disposed) return;
            var provider = ContextMenuItemsProvider;
            if (provider == null) return;

            IReadOnlyList<EmbeddedBrowserMenuItem> items;
            try
            {
                var target = e.ContextMenuTarget;
                var context = new TiroContextMenuContext(
                    target != null && target.IsEditable,
                    target != null && target.HasSelection ? target.SelectionText : null);
                items = provider(context);
            }
            catch (Exception ex)
            {
                // The provider is the viewer's, which already guards the host's delegates. If
                // one still escapes, the menu appears without host items rather than the
                // WebView2 event faulting mid-dispatch.
                Debug.Fail("Context menu provider threw: " + ex.Message);
                return;
            }
            if (items == null || items.Count == 0) return;

            var environment = _webView2.CoreWebView2?.Environment;
            if (environment == null) return;

            // A separator only earns its place if there's something above it to separate from.
            if (e.MenuItems.Count > 0) e.MenuItems.Add(Separator(environment));

            foreach (var item in items)
            {
                if (item == null) continue;
                var cached = CachedItem(environment, item.Label);
                if (cached == null) continue;
                cached.Action = item.Invoke;
                e.MenuItems.Add(cached.Native);
            }
        }

        private CoreWebView2ContextMenuItem Separator(CoreWebView2Environment environment)
            => _menuSeparator ?? (_menuSeparator = environment.CreateContextMenuItem(
                null, null, CoreWebView2ContextMenuItemKind.Separator));

        private CachedMenuItem CachedItem(CoreWebView2Environment environment, string label)
        {
            if (_menuItemCache.TryGetValue(label, out var existing)) return existing;

            var created = new CachedMenuItem
            {
                Native = environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command),
            };
            // Subscribed once for the lifetime of the cached item; the handler reads whichever
            // action was attached for the menu currently on screen.
            created.Native.CustomItemSelected += (_, __) => created.Action?.Invoke();
            _menuItemCache[label] = created;
            return created;
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed) return;
            var json = e?.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return;
            MessageReceived?.Invoke(this, json);
        }

        private static void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (e.PermissionKind != CoreWebView2PermissionKind.Microphone) return;

            // Only auto-grant when the requesting page is served from our virtual host.
            // Anything else (cross-origin iframe, post-redirect navigation off-host,
            // consumer-supplied content that loaded a CDN page) falls through to WebView2's
            // default — which denies and may show the OS prompt.
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                && string.Equals(uri.Host, TrustedMicrophoneOriginHost, StringComparison.OrdinalIgnoreCase))
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_coreSubscribed && _webView2.CoreWebView2 != null)
            {
                _webView2.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView2.CoreWebView2.PermissionRequested -= OnPermissionRequested;
                _webView2.CoreWebView2.ContextMenuRequested -= OnContextMenuRequested;
            }
            ContextMenuItemsProvider = null;
            _menuItemCache.Clear();
            _menuSeparator = null;
            // The WebView2 control itself is disposed by its parent UserControl
            // via the WinForms Controls-collection ownership chain.
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebView2EmbeddedBrowser));
        }
    }
}
