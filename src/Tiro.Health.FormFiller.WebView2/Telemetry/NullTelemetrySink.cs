using System;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// No-op <see cref="ITelemetrySink"/>. Default for the core library, which does not
    /// depend on any specific telemetry backend.
    /// </summary>
    public sealed class NullTelemetrySink : ITelemetrySink
    {
        public static readonly NullTelemetrySink Instance = new NullTelemetrySink();

        private NullTelemetrySink() { }

        public ITelemetrySession BeginSession(string sessionId) => NullSession.Instance;
        public void CaptureException(Exception ex) { }
        public void Flush(TimeSpan timeout) { }
        public void Dispose() { }

        private sealed class NullSession : ITelemetrySession
        {
            public static readonly NullSession Instance = new NullSession();
            public void SetTag(string key, string value) { }
            public void AddBreadcrumb(string category, string message) { }
            public ITelemetrySpan StartTransaction(string name, string operation) => NullSpan.Instance;
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
        }
    }
}
