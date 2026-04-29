using System;
using System.Collections.Generic;
using Tiro.Health.FormFiller.WebView2.Telemetry;

namespace Tiro.Health.FormFiller.WebView2.Tests.Fakes
{
    /// <summary>
    /// Test double for <see cref="ITelemetrySink"/> — records every session/exception/flush
    /// call so tests can assert telemetry contracts without depending on a real backend.
    /// </summary>
    public sealed class FakeTelemetrySink : ITelemetrySink
    {
        public List<FakeTelemetrySession> Sessions { get; } = new List<FakeTelemetrySession>();
        public List<Exception> CapturedExceptions { get; } = new List<Exception>();
        public bool Disposed { get; private set; }
        public bool Flushed { get; private set; }

        public ITelemetrySession BeginSession(string sessionId)
        {
            var session = new FakeTelemetrySession(sessionId);
            Sessions.Add(session);
            return session;
        }

        public void CaptureException(Exception ex) => CapturedExceptions.Add(ex);
        public void Flush(TimeSpan timeout) => Flushed = true;
        public void Dispose() => Disposed = true;
    }

    public sealed class FakeTelemetrySession : ITelemetrySession
    {
        public string SessionId { get; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>();
        public List<(string Category, string Message)> Breadcrumbs { get; } = new List<(string, string)>();
        public List<FakeTelemetrySpan> Transactions { get; } = new List<FakeTelemetrySpan>();
        public bool Disposed { get; private set; }

        public FakeTelemetrySession(string sessionId)
        {
            SessionId = sessionId;
        }

        public void SetTag(string key, string value) => Tags[key] = value;
        public void AddBreadcrumb(string category, string message)
            => Breadcrumbs.Add((category, message));

        public ITelemetrySpan StartTransaction(string name, string operation)
        {
            var span = new FakeTelemetrySpan(name, operation);
            Transactions.Add(span);
            return span;
        }

        public string GetSentryTraceHeader()
            => $"fake-trace-{SessionId.Substring(0, 8)}-deadbeef-1";

        public IReadOnlyDictionary<string, string> GetEmbeddedBootstrapConfig()
            => new Dictionary<string, string> { ["sentryTrace"] = GetSentryTraceHeader() };

        public void Dispose() => Disposed = true;
    }

    public sealed class FakeTelemetrySpan : ITelemetrySpan
    {
        public string Name { get; }
        public string Operation { get; }
        public bool Finished { get; private set; }
        public TelemetrySpanStatus? FinalStatus { get; private set; }
        public Exception FinalException { get; private set; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>();
        public Dictionary<string, object> Extras { get; } = new Dictionary<string, object>();
        public List<FakeTelemetrySpan> Children { get; } = new List<FakeTelemetrySpan>();

        public FakeTelemetrySpan(string name, string operation)
        {
            Name = name;
            Operation = operation;
        }

        public void SetTag(string key, string value) => Tags[key] = value;
        public void SetExtra(string key, object value) => Extras[key] = value;

        public ITelemetrySpan StartChild(string operation, string description)
        {
            var child = new FakeTelemetrySpan(description, operation);
            Children.Add(child);
            return child;
        }

        public void Finish(TelemetrySpanStatus status)
        {
            // ITelemetrySpan contract: Finish must be idempotent (subsequent calls are no-ops).
            if (Finished) return;
            Finished = true;
            FinalStatus = status;
        }

        public void Finish(Exception ex)
        {
            if (Finished) return;
            Finished = true;
            FinalException = ex;
        }
    }
}
