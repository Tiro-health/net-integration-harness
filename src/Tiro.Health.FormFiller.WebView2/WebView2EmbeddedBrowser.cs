using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// <see cref="IEmbeddedBrowser"/> adapter over <see cref="WebView2Control"/>. Owns the
    /// underlying WinForms control; auto-grants microphone permission for voice input.
    /// </summary>
    public sealed class WebView2EmbeddedBrowser : IEmbeddedBrowser
    {
        private readonly WebView2Control _webView2;
        private bool _coreSubscribed;
        private bool _disposed;

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
            _coreSubscribed = true;
        }

        public void PostMessage(string json)
        {
            ThrowIfDisposed();
            if (_webView2.CoreWebView2 == null) return;
            _webView2.CoreWebView2.PostWebMessageAsJson(json);
        }

        public void MapVirtualHost(string hostName, string folderPath)
        {
            ThrowIfDisposed();
            if (_webView2.CoreWebView2 == null)
                throw new InvalidOperationException("InitializeAsync must complete before MapVirtualHost.");
            _webView2.CoreWebView2.SetVirtualHostNameToFolderMapping(
                hostName, folderPath, CoreWebView2HostResourceAccessKind.Allow);
        }

        public void Navigate(Uri url)
        {
            ThrowIfDisposed();
            if (url == null) throw new ArgumentNullException(nameof(url));
            _webView2.Source = url;
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
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                e.State = CoreWebView2PermissionState.Allow;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_coreSubscribed && _webView2.CoreWebView2 != null)
            {
                _webView2.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView2.CoreWebView2.PermissionRequested -= OnPermissionRequested;
            }
            // The WebView2 control itself is disposed by its parent UserControl
            // via the WinForms Controls-collection ownership chain.
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebView2EmbeddedBrowser));
        }
    }
}
