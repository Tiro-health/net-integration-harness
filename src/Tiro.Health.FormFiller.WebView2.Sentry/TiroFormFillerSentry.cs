using System;
using global::Sentry;
using Tiro.Health.FormFiller.WebView2;
using Tiro.Health.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// One-line startup integration for the Sentry telemetry adapter. Registers a global
    /// telemetry factory consulted by every <see cref="TiroFormViewer{TResource,TQR,TOO}"/>
    /// at construction. Designer-placed viewers pick up the configured sink automatically
    /// — no per-form code, no <c>Form_Load</c> wiring.
    /// <para>
    /// Call once during application startup, before any form containing a viewer is
    /// constructed (e.g. <c>Sub Main</c> or the <c>My.MyApplication.Startup</c> handler).
    /// Viewers constructed before <c>UseSentry</c> runs will use whatever sink was
    /// registered at that earlier moment — typically <see cref="NullTelemetrySink"/>.
    /// </para>
    /// </summary>
    public static class TiroFormFillerSentry
    {
        /// <summary>
        /// Configure Sentry with Tiro's built-in DSNs (host telemetry to
        /// <c>tirohealth/dotnet-winforms</c>, embedded-page telemetry to
        /// <c>tirohealth/javascript</c>). This is the recommended path: Tiro's support team
        /// can see your form sessions and help diagnose issues. The defaults are
        /// PHI-safe — no FHIR payloads are attached to spans.
        /// </summary>
        public static void UseSentry()
        {
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new SentryTelemetrySink();
        }

        /// <summary>
        /// Configure Sentry to route host telemetry to your own DSN. The embedded-page DSN
        /// remains the Tiro default (<see cref="SentryTelemetrySink.DefaultEmbeddedDsn"/>);
        /// use the overload taking a full <see cref="SentryTelemetrySink"/> if you need
        /// independent control over both sides.
        /// </summary>
        /// <param name="dsn">Sentry DSN for the .NET host process.</param>
        public static void UseSentry(string dsn)
        {
            if (string.IsNullOrEmpty(dsn)) throw new ArgumentException("DSN must not be null or empty.", nameof(dsn));
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new SentryTelemetrySink(dsn);
        }

        /// <summary>
        /// Configure Sentry with explicit host DSN, environment, and release. The
        /// embedded-page DSN remains the Tiro default.
        /// </summary>
        public static void UseSentry(string dsn, string environment, string release)
        {
            if (string.IsNullOrEmpty(dsn)) throw new ArgumentException("DSN must not be null or empty.", nameof(dsn));
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new SentryTelemetrySink(dsn, environment, release);
        }

        /// <summary>
        /// Configure Sentry with caller-supplied <see cref="SentryOptions"/>. Use this when
        /// you need fine control (custom <c>BeforeSend</c>, sampling, integrations, etc.).
        /// </summary>
        public static void UseSentry(SentryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            TiroFormViewerDefaults.TelemetrySinkFactory = () => new SentryTelemetrySink(options);
        }

        /// <summary>
        /// Configure the viewer to use a fully-constructed <see cref="SentryTelemetrySink"/>
        /// of your choice. Useful when both DSNs (host and embedded-page) need to be
        /// overridden, or when a single sink instance should be shared by all viewers.
        /// The same instance is returned by every <see cref="ITelemetrySink"/> request, so
        /// callers passing a single sink share Sentry SDK init and the embedded DSN.
        /// </summary>
        public static void UseSentry(SentryTelemetrySink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            TiroFormViewerDefaults.TelemetrySinkFactory = () => sink;
        }
    }
}
