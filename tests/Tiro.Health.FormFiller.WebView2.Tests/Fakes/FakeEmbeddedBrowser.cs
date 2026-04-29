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
    public sealed class FakeEmbeddedBrowser : IEmbeddedBrowser
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

        public Task InitializeAsync()
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public void PostMessage(string json) => PostedMessages.Add(json);

        public void MapVirtualHost(string hostName, string folderPath)
            => VirtualHostMappings.Add((hostName, folderPath));

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
