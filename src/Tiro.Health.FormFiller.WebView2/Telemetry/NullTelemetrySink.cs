using System;
using System.Collections.Generic;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// No-op <see cref="ITelemetrySink"/>. Default for the form-filler when no telemetry backend
    /// is registered.
    /// </summary>
    public sealed class NullTelemetrySink : ITelemetrySink
    {
        public static readonly NullTelemetrySink Instance = new NullTelemetrySink();

        private NullTelemetrySink() { }

        public ITelemetrySession BeginSession(string sessionId) => NullSession.Instance;

        /// <summary>
        /// The no-op session and span, for decorators that need a harmless stand-in when the sink
        /// they wrap throws out of a member that has to return one (see
        /// <see cref="FileTelemetrySink"/>). Returning the caller's own span there would be worse
        /// than nothing: finishing the substitute would finish the real parent.
        /// </summary>
        internal static ITelemetrySession NoopSession => NullSession.Instance;

        /// <inheritdoc cref="NoopSession" />
        internal static ITelemetrySpan NoopSpan => NullSpan.Instance;

        public void CaptureException(Exception ex) { }

        /// <inheritdoc />
        public void CaptureMessage(string message) { }
        public void Flush(TimeSpan timeout) { }
        public void Dispose() { }

        private sealed class NullSession : ITelemetrySession
        {
            public static readonly NullSession Instance = new NullSession();
            public void SetTag(string key, string value) { }
            public void AddBreadcrumb(string category, string message) { }
            public ITelemetrySpan StartTransaction(string name, string operation) => NullSpan.Instance;
            public string GetSentryTraceHeader() => null;
            public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig() => null;
            public void Dispose() { }
        }

        private sealed class NullSpan : ITelemetrySpan
        {
            public static readonly NullSpan Instance = new NullSpan();
            public void SetTag(string key, string value) { }
            public void SetExtra(string key, object value) { }
            public ITelemetrySpan StartChild(string operation, string description) => Instance;
            public void Finish(TelemetrySpanStatus status) { }
            public void Finish(Exception ex) { }
            public void Dispose() { }
        }
    }
}
