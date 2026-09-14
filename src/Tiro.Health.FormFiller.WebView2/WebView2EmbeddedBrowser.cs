using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        // cached and only the action behind one is swapped before the menu is shown.
        //
        // The key is the label plus how many times that label has already appeared in the menu
        // being built. Label alone would collapse two same-labelled entries in one menu into a
        // single native object added twice; the occurrence disambiguates them.
        private readonly Dictionary<string, CachedMenuItem> _menuItemCache =
            new Dictionary<string, CachedMenuItem>(StringComparer.Ordinal);

        // The cache is keyed by label, and labels are the host's — the viewer's own docs invite
        // ones that change with the patient. Without a ceiling the cache grows for the life of
        // the process and eventually walks into the 1000-item cap, which is per *environment*
        // and therefore shared by every WebView2 in the process. This is well above any real
        // menu and far below the cap, so eviction is rare and the cap unreachable.
        private const int MaxCachedMenuItems = 256;

        // Separators are interchangeable, so they're pooled by position rather than cached by
        // identity: the Nth separator in any menu reuses the Nth pooled object.
        private readonly List<CoreWebView2ContextMenuItem> _separatorPool =
            new List<CoreWebView2ContextMenuItem>();

        // Bumped once per context-menu request, so a cached item can say which menu it last
        // appeared in — that drives both eviction and the clearing of stale actions.
        private long _menuBuildSequence;

        private sealed class CachedMenuItem
        {
            public CoreWebView2ContextMenuItem Native;
            public EventHandler<object> Handler;
            public Action Action;
            public long LastUsed;
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
        public Func<TiroContextMenuContext, IReadOnlyList<TiroMenuEntry>, IReadOnlyList<TiroMenuEntry>> ContextMenuBuilder { get; set; }

        /// <summary>
        /// Hands WebView2's own entries to the builder and renders back whatever menu it
        /// returns. <c>Handled</c> is left false on purpose: WebView2 then shows its native menu
        /// — reordered and filtered, but still its own — so the click never leaves the page, the
        /// caret stays where the user right-clicked, and Ctrl+V still pastes there. Suppressing
        /// the menu to show a WinForms one instead would take focus out of the browser and lose
        /// that, which is why the host composes a list rather than drawing a menu.
        /// </summary>
        private void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            if (_disposed) return;
            var builder = ContextMenuBuilder;
            if (builder == null) return;

            var environment = _webView2.CoreWebView2?.Environment;
            if (environment == null) return;

            _menuBuildSequence++;

            // Snapshot before anything is removed: this is both what the builder is offered and
            // the record of which handles are legitimate for this click.
            var native = new List<CoreWebView2ContextMenuItem>(e.MenuItems);
            var offered = new List<TiroMenuEntry>(native.Count);
            foreach (var item in native)
                offered.Add(new TiroMenuEntry(item.Name, item.Label, MapKind(item.Kind), item.IsEnabled, item));

            IReadOnlyList<TiroMenuEntry> final;
            try
            {
                var target = e.ContextMenuTarget;
                var context = new TiroContextMenuContext(
                    target != null && target.IsEditable,
                    target != null && target.HasSelection ? target.SelectionText : null);
                final = builder(context, offered);
            }
            catch (Exception ex)
            {
                // The builder is the viewer's, which already guards the host's delegates. If one
                // still escapes, WebView2's own menu is shown untouched rather than the event
                // faulting mid-dispatch.
                Debug.Fail("Context menu builder threw: " + ex.Message);
                return;
            }
            if (final == null) return;

            // Everything below here is COM. A throw from any of it unwinds through WebView2's
            // callback into the WinForms message pump, where there is no handler — the process
            // dies from a right-click. Whatever fails, the menu is worth less than the session,
            // so the whole render is guarded and a partial menu is the worst outcome.
            try
            {
                Render(e, ContextMenuRenderPlan.Create(final, native), environment);
            }
            catch (Exception ex)
            {
                Debug.Fail("Context menu render failed: " + ex.Message);
            }
            finally
            {
                // An action holds the context of the menu it was built for, and that context
                // carries SelectionText — form content, clinical data. Items not in the menu
                // just built have no further use for theirs, so it does not outlive the click.
                ClearActionsNotUsedInCurrentMenu();
            }
        }

        private void Render(
            CoreWebView2ContextMenuRequestedEventArgs e, ContextMenuRenderPlan plan, CoreWebView2Environment environment)
        {
            if (plan.ClearExisting)
            {
                // Remove from the end: every mutation is then a plain Remove, which is what
                // WebView2 documents as supported alongside Add.
                for (var i = e.MenuItems.Count - 1; i >= 0; i--) e.MenuItems.RemoveAt(i);
            }

            foreach (var step in plan.Steps)
            {
                CoreWebView2ContextMenuItem rendered;
                switch (step.Kind)
                {
                    case ContextMenuRenderPlan.StepKind.BrowserItem:
                        rendered = step.BrowserHandle as CoreWebView2ContextMenuItem;
                        break;

                    case ContextMenuRenderPlan.StepKind.Separator:
                        rendered = SeparatorAt(environment, step.SeparatorIndex);
                        break;

                    default:
                        var cached = CachedItem(environment, step.Label, step.Occurrence);
                        cached.Action = step.Invoke;
                        // Legal only because this is a custom item; WebView2 reserves IsEnabled
                        // on its own entries, which is why TiroMenuEntry refuses to set it there.
                        cached.Native.IsEnabled = step.IsEnabled;
                        rendered = cached.Native;
                        break;
                }

                if (rendered != null) e.MenuItems.Add(rendered);
            }
        }

        private static TiroMenuEntryKind MapKind(CoreWebView2ContextMenuItemKind kind)
        {
            switch (kind)
            {
                case CoreWebView2ContextMenuItemKind.CheckBox: return TiroMenuEntryKind.CheckBox;
                case CoreWebView2ContextMenuItemKind.Radio: return TiroMenuEntryKind.Radio;
                case CoreWebView2ContextMenuItemKind.Separator: return TiroMenuEntryKind.Separator;
                case CoreWebView2ContextMenuItemKind.Submenu: return TiroMenuEntryKind.Submenu;
                default: return TiroMenuEntryKind.Command;
            }
        }

        private CoreWebView2ContextMenuItem SeparatorAt(CoreWebView2Environment environment, int index)
        {
            while (_separatorPool.Count <= index)
                _separatorPool.Add(environment.CreateContextMenuItem(
                    null, null, CoreWebView2ContextMenuItemKind.Separator));
            return _separatorPool[index];
        }

        private CachedMenuItem CachedItem(CoreWebView2Environment environment, string label, int occurrence)
        {
            // NUL rather than a printable separator: a label may contain anything the host
            // types, including whatever character was chosen here, but never a NUL.
            var key = occurrence.ToString(CultureInfo.InvariantCulture) + "\0" + label;
            if (_menuItemCache.TryGetValue(key, out var existing))
            {
                existing.LastUsed = _menuBuildSequence;
                return existing;
            }

            if (_menuItemCache.Count >= MaxCachedMenuItems) EvictColdMenuItems();

            var created = new CachedMenuItem
            {
                Native = environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command),
                LastUsed = _menuBuildSequence,
            };
            // Held in a field so it can be detached again: the subscription is what keeps the
            // native item — and through the action, this browser and the viewer — alive.
            created.Handler = (_, __) => created.Action?.Invoke();
            created.Native.CustomItemSelected += created.Handler;
            _menuItemCache[key] = created;
            return created;
        }

        /// <summary>
        /// Drops the least recently shown cached items, back to half the ceiling so eviction is
        /// amortised rather than happening on every menu once full. Items in the menu being
        /// built are never evicted — they are about to be rendered.
        /// </summary>
        private void EvictColdMenuItems()
        {
            var target = _menuItemCache.Count - (MaxCachedMenuItems / 2);
            if (target <= 0) return;

            var cold = new List<KeyValuePair<string, CachedMenuItem>>(_menuItemCache.Count);
            foreach (var pair in _menuItemCache)
                if (pair.Value.LastUsed != _menuBuildSequence) cold.Add(pair);
            cold.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

            for (var i = 0; i < cold.Count && i < target; i++)
            {
                ReleaseCachedItem(cold[i].Value);
                _menuItemCache.Remove(cold[i].Key);
            }
        }

        /// <summary>
        /// Detaches a cached item so nothing keeps it — or what its action closes over — alive.
        /// The native item has no Dispose; dropping the event subscription and the managed
        /// reference is what lets it go, and with it the environment's live-item budget.
        /// </summary>
        private static void ReleaseCachedItem(CachedMenuItem cached)
        {
            try
            {
                if (cached.Native != null && cached.Handler != null)
                    cached.Native.CustomItemSelected -= cached.Handler;
            }
            catch (Exception ex)
            {
                // Unsubscribing from a torn-down native item is not worth failing Dispose over.
                Debug.Fail("Detaching a context menu item failed: " + ex.Message);
            }
            cached.Handler = null;
            cached.Action = null;
            cached.Native = null;
        }

        /// <summary>
        /// Releases the actions of every cached item that is not in the menu just built. An
        /// action closes over the <see cref="TiroContextMenuContext"/> of its click, whose
        /// <c>SelectionText</c> is form content — clinical data — so it is not left reachable
        /// once the menu it belonged to is gone.
        /// </summary>
        private void ClearActionsNotUsedInCurrentMenu()
        {
            foreach (var cached in _menuItemCache.Values)
                if (cached.LastUsed != _menuBuildSequence) cached.Action = null;
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
            ContextMenuBuilder = null;
            // Clearing the dictionary alone would drop only this side of the reference: the
            // native item keeps its event sink, which keeps the action, which closes over the
            // viewer and the last click's selected clinical text. Detach each one first.
            foreach (var cached in _menuItemCache.Values) ReleaseCachedItem(cached);
            _menuItemCache.Clear();
            _separatorPool.Clear();
            // The WebView2 control itself is disposed by its parent UserControl
            // via the WinForms Controls-collection ownership chain.
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebView2EmbeddedBrowser));
        }
    }
}
