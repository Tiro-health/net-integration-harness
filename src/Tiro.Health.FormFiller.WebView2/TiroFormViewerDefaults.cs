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
    }
}
