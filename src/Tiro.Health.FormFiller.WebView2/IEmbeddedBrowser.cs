using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Abstraction over an embedded browser engine. Lets <see cref="TiroFormViewer{TResource,TQR,TOO}"/>
    /// stay decoupled from any specific implementation (WebView2 today; WPF, CEF, a fake
    /// for tests tomorrow).
    /// </summary>
    public interface IEmbeddedBrowser : IDisposable
    {
        /// <summary>The WinForms control to embed in a parent container.</summary>
        Control Control { get; }

        /// <summary>
        /// Initialize the underlying browser. Idempotent — safe to await multiple times.
        /// Must complete before any other method on this interface is called.
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Raised when the embedded page posts a JSON message via the host bridge.
        /// The event arg is the raw JSON string.
        /// </summary>
        event EventHandler<string> MessageReceived;

        /// <summary>Post a JSON message to the embedded page.</summary>
        void PostMessage(string json);

        /// <summary>
        /// Map a virtual host name to a local folder so pages under
        /// <c>https://{hostName}/</c> serve from <paramref name="folderPath"/>.
        /// </summary>
        void MapVirtualHost(string hostName, string folderPath);

        /// <summary>Navigate the browser to the given URL.</summary>
        void Navigate(Uri url);
    }
}
