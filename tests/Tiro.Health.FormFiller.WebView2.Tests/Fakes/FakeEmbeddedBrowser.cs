using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2.Tests.Fakes
{
    /// <summary>
    /// Test double for <see cref="IEmbeddedBrowser"/>. Records every host-side interaction
    /// (posted messages, init scripts, virtual host mappings, navigations) and exposes
    /// <see cref="RaiseMessageReceived"/> so tests can simulate inbound page→host messages.
    /// </summary>
    public sealed class FakeEmbeddedBrowser : IEmbeddedBrowser, IContextMenuCapableBrowser
    {
        private readonly Control _control = new Control();

        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> PostedMessages { get; } = new List<string>();
        public List<string> InitializationScripts { get; } = new List<string>();
        public List<(string Host, string Folder)> VirtualHostMappings { get; } = new List<(string, string)>();
        public List<Uri> NavigatedUrls { get; } = new List<Uri>();

        public Control Control => _control;

        public event EventHandler<string> MessageReceived;

        /// <summary>
        /// Set by the viewer at init. Tests call it to model a right-click, since the real menu
        /// is Chromium's and never appears in a unit test.
        /// </summary>
        public Func<TiroContextMenuContext, IReadOnlyList<EmbeddedBrowserMenuItem>> ContextMenuItemsProvider { get; set; }

        /// <summary>The items a right-click on the given target would show.</summary>
        public IReadOnlyList<EmbeddedBrowserMenuItem> RequestContextMenu(bool isEditable = true, string selectionText = null)
            => ContextMenuItemsProvider?.Invoke(new TiroContextMenuContext(isEditable, selectionText));

        public Task InitializeAsync()
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        /// <summary>When set, the next PostMessage call throws this once — what a WebView2
        /// torn down under a WinForms dispose race does to a send in flight.</summary>
        public Exception ThrowOnNextPostMessage { get; set; }

        public void PostMessage(string json)
        {
            var ex = ThrowOnNextPostMessage;
            if (ex != null)
            {
                ThrowOnNextPostMessage = null;
                throw ex;
            }
            PostedMessages.Add(json);
        }

        /// <summary>When set, the next MapVirtualHost call throws this once.</summary>
        public Exception ThrowOnNextMapVirtualHost { get; set; }

        public void MapVirtualHost(string hostName, string folderPath)
        {
            var ex = ThrowOnNextMapVirtualHost;
            if (ex != null)
            {
                ThrowOnNextMapVirtualHost = null;
                throw ex;
            }
            VirtualHostMappings.Add((hostName, folderPath));
        }

        public void Navigate(Uri url) => NavigatedUrls.Add(url);

        public Task AddInitializationScriptAsync(string script)
        {
            if (!string.IsNullOrEmpty(script)) InitializationScripts.Add(script);
            return Task.CompletedTask;
        }

        /// <summary>Simulate an inbound message from the embedded page.</summary>
        public void RaiseMessageReceived(string json)
            => MessageReceived?.Invoke(this, json);

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _control.Dispose();
        }
    }
}
