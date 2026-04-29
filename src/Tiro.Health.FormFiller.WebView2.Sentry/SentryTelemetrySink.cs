using System;
using System.Reflection;
using global::Sentry;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Sentry
{
    /// <summary>
    /// Sentry-backed <see cref="ITelemetrySink"/>. Initializes the global Sentry SDK on first
    /// construction (the SDK itself is process-global by design); subsequent instances are
    /// no-ops with respect to init.
    /// </summary>
    public sealed class SentryTelemetrySink : ITelemetrySink
    {
        /// <summary>
        /// Default DSN used by Tiro-shipped binaries when no DSN is provided. Consumers should
        /// use the DSN-overload ctor (or the <see cref="SentryOptions"/> overload) to send
        /// telemetry to their own Sentry project.
        /// </summary>
        public const string DefaultDsn =
            "https://e2152463656fef5d6cf67ac91af87050@o4507651309043712.ingest.de.sentry.io/4510703529820240";

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

        /// <summary>Initialize Sentry with <see cref="DefaultDsn"/>, <see cref="DefaultEnvironment"/>, and <see cref="DefaultRelease"/>.</summary>
        public SentryTelemetrySink() : this(DefaultDsn, DefaultEnvironment, DefaultRelease) { }

        /// <summary>Initialize Sentry with the supplied DSN; environment and release default.</summary>
        public SentryTelemetrySink(string dsn) : this(dsn, DefaultEnvironment, DefaultRelease) { }

        /// <summary>
        /// Initialize Sentry with explicit DSN, environment, and release. Idempotent if the
        /// SDK is already enabled (subsequent calls are silently ignored).
        /// </summary>
        public SentryTelemetrySink(string dsn, string environment, string release)
        {
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
        /// Initialize Sentry with caller-supplied options. Use this to control sample rates,
        /// release/environment tags, transports, etc.
        /// </summary>
        public SentryTelemetrySink(SentryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!SentrySdk.IsEnabled)
            {
                SentrySdk.Init(options);
            }
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
            => new SentryTelemetrySession(sessionId);

        public void CaptureException(Exception ex) => SentrySdk.CaptureException(ex);

        public void Flush(TimeSpan timeout) => SentrySdk.Flush(timeout);

        public void Dispose()
        {
            // The SDK is process-global; we don't shut it down here because other consumers
            // (or further TiroFormViewer instances) may still depend on it.
        }
    }
}
