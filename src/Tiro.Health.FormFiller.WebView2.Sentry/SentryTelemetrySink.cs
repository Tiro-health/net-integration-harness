using System;
using System.Reflection;
using global::Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// Sentry-backed <see cref="ITelemetrySink"/>. The Sentry .NET SDK is process-global —
    /// the first <c>SentrySdk.Init</c> wins, so the host DSN/environment/release supplied
    /// to a second sink in the same process are not re-applied. The embedded-page DSN,
    /// in contrast, lives on the sink instance and is honored per-viewer.
    /// </summary>
    public sealed class SentryTelemetrySink : ITelemetrySink
    {
        // _dsn is captured for symmetry but only takes effect if this sink is the first
        // to call SentrySdk.Init. _embeddedDsn is per-sink and always honored — it's
        // injected into each viewer's WebView2 page bootstrap.
        private readonly string _dsn;
        private readonly string _embeddedDsn;
        private readonly string _environment;
        private readonly string _release;

        /// <summary>
        /// Default DSN for the host process — the <c>tirohealth/dotnet-winforms</c> project.
        /// Override via the DSN-taking ctors to redirect host telemetry to a different project.
        /// </summary>
        public const string DefaultDsn =
            "https://e2152463656fef5d6cf67ac91af87050@o4507651309043712.ingest.de.sentry.io/4510703529820240";

        /// <summary>
        /// Default DSN injected into the embedded browser page — the <c>tirohealth/javascript</c>
        /// project. Same Sentry org as the host so trace propagation gives a unified trace
        /// view across both projects.
        /// </summary>
        public const string DefaultEmbeddedDsn =
            "https://5b3c2798d1b788d50ee2f655ad3ca731@o4507651309043712.ingest.de.sentry.io/4510703453405264";

        /// <summary>
        /// Default environment tag. Override via the (dsn, environment, release) ctor for
        /// non-production deployments (e.g. "staging", "development").
        /// </summary>
        public const string DefaultEnvironment = "production";

        /// <summary>
        /// Default release identifier: <c>Tiro.Health.FormFiller.WebView2@&lt;version&gt;</c> where
        /// <c>&lt;version&gt;</c> is the core library's <see cref="AssemblyInformationalVersionAttribute"/>
        /// (which mirrors the NuGet package version).
        /// </summary>
        public static string DefaultRelease => ComputeDefaultRelease();

        /// <summary>Initialize Sentry with all defaults (host DSN, embedded DSN, environment, release).</summary>
        public SentryTelemetrySink() : this(DefaultDsn, DefaultEmbeddedDsn, DefaultEnvironment, DefaultRelease) { }

        /// <summary>Initialize Sentry with the supplied host DSN; embedded DSN, environment, and release default.</summary>
        public SentryTelemetrySink(string dsn) : this(dsn, DefaultEmbeddedDsn, DefaultEnvironment, DefaultRelease) { }

        /// <summary>
        /// Initialize Sentry with explicit host DSN, environment, and release. The embedded
        /// browser's DSN defaults to <see cref="DefaultEmbeddedDsn"/>.
        /// </summary>
        public SentryTelemetrySink(string dsn, string environment, string release)
            : this(dsn, DefaultEmbeddedDsn, environment, release) { }

        /// <summary>
        /// Initialize Sentry with explicit host DSN, embedded-browser DSN, environment, and
        /// release. The host-side init is skipped if the SDK is already enabled in this
        /// process (the Sentry .NET SDK is global — first init wins). The embedded-browser
        /// DSN is always honored per-sink.
        /// </summary>
        public SentryTelemetrySink(string dsn, string embeddedDsn, string environment, string release)
        {
            _dsn = dsn;
            _embeddedDsn = embeddedDsn;
            _environment = environment;
            _release = release;
            if (!SentrySdk.IsEnabled)
            {
                SentrySdk.Init(o =>
                {
                    o.Dsn = dsn;
                    o.Environment = environment;
                    o.Release = release;
                    o.IsGlobalModeEnabled = true;
                    o.TracesSampleRate = 1.0;
                });
            }
        }

        /// <summary>
        /// Initialize Sentry with caller-supplied options. The embedded-browser DSN defaults
        /// to <see cref="DefaultEmbeddedDsn"/>. Skipped if the SDK is already enabled.
        /// </summary>
        public SentryTelemetrySink(SentryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _dsn = options.Dsn;
            _embeddedDsn = DefaultEmbeddedDsn;
            _environment = options.Environment;
            _release = options.Release;
            if (!SentrySdk.IsEnabled) SentrySdk.Init(options);
        }

        private static string ComputeDefaultRelease()
        {
            var asm = typeof(ITelemetrySink).Assembly;
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString()
                       ?? "0.0.0";
            return "Tiro.Health.FormFiller.WebView2@" + version;
        }

        public ITelemetrySession BeginSession(string sessionId)
            => new SentryTelemetrySession(sessionId, _embeddedDsn, _environment, _release);

        public void CaptureException(Exception ex) => SentrySdk.CaptureException(ex);

        public void Flush(TimeSpan timeout) => SentrySdk.Flush(timeout);

        public void Dispose()
        {
            // The SDK is process-global; we don't shut it down here because other consumers
            // (or further TiroFormViewer instances) may still depend on it.
        }
    }
}
