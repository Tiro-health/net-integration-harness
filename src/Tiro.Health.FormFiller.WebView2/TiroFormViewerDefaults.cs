using System;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Application-wide defaults consulted by <see cref="TiroFormViewer{TResource,TQR,TOO}"/>
    /// at construction time. Set once during application startup (e.g. in <c>Sub Main</c> or
    /// the <c>My.MyApplication.Startup</c> handler), before any viewer is constructed —
    /// values read here are sampled by each viewer's ctor and not re-read afterwards.
    /// </summary>
    public static class TiroFormViewerDefaults
    {
        /// <summary>
        /// Factory invoked by <see cref="TiroFormViewer{T,Q,O}.CreateTelemetrySink"/> on each
        /// viewer construction. <c>null</c> (the default) means no telemetry — the viewer
        /// falls back to <see cref="NullTelemetrySink"/>.
        /// <para>
        /// The Sentry adapter package <c>Tiro.Health.FormFiller.WebView2.Sentry</c> exposes
        /// a one-line helper, <c>TiroFormFillerSentry.UseSentry()</c>, that initializes Sentry
        /// and assigns this property — that's the recommended way to opt in. Set it directly
        /// only when supplying a custom <see cref="ITelemetrySink"/> implementation.
        /// </para>
        /// <para>
        /// Assignment is not thread-safe; the contract is "set once during startup before any
        /// viewer exists." Reassignment after a viewer has been constructed does not affect
        /// already-constructed viewers (each viewer captures its sink at ctor time).
        /// </para>
        /// </summary>
        public static Func<ITelemetrySink> TelemetrySinkFactory { get; set; }

        /// <summary>
        /// Factory for the <see cref="System.Net.Http.HttpClient"/> the SDC server version check
        /// (GH-62) uses. <c>null</c> (the default) means an internally-owned, process-wide client
        /// with no credentials.
        /// <para>
        /// Nothing needs this today: the SDC server holds its own service-account credentials and
        /// requires none from the caller (GH-39), so the default client reaches both a
        /// hospital-local instance and the open hosted ones. It exists for the scheme GH-39
        /// settled on if that ever changes — a static API key, read from host config and attached
        /// as a request header:
        /// </para>
        /// <code>
        /// Dim probeClient As New HttpClient()
        /// probeClient.DefaultRequestHeaders.Add("X-Api-Key", keyFromHostConfig)
        /// TiroFormViewerDefaults.SdcProbeHttpClientFactory = Function() probeClient
        /// </code>
        /// <para>
        /// The gap this closes is narrow but real: <c>SdcClient</c> takes an
        /// <see cref="System.Net.Http.HttpClient"/> already, so a host can authenticate the
        /// client's probe; the viewer had no equivalent, and a probe that cannot present a
        /// credential the server demands reads as "version unknown" and silently disarms the
        /// check. Note the failure is a disarmed check, not a broken launch — 401 fails open like
        /// any other unreadable version.
        /// </para>
        /// <para>
        /// The factory is invoked per check; return a shared, long-lived client (a client per call
        /// burns sockets). Same "set once at startup" contract as
        /// <see cref="TelemetrySinkFactory"/>, except this one is read at each check rather than
        /// sampled in the ctor.
        /// </para>
        /// </summary>
        public static Func<System.Net.Http.HttpClient> SdcProbeHttpClientFactory { get; set; }
    }
}
