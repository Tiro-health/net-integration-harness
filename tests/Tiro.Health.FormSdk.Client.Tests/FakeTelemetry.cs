using System;
using System.Collections.Generic;
using Tiro.Health.Telemetry;

namespace Tiro.Health.FormSdk.Client.Tests
{
    /// <summary>
    /// Recording test double for <see cref="ITelemetrySession"/> — captures every transaction the
    /// SDC client starts so tests can assert the emitted spans without a real backend.
    /// </summary>
    internal sealed class FakeTelemetrySession : ITelemetrySession
    {
        public List<FakeTelemetrySpan> Transactions { get; } = new();
        public Dictionary<string, string> Tags { get; } = new();

        public void SetTag(string key, string value) => Tags[key] = value;
        public void AddBreadcrumb(string category, string message) { }

        public ITelemetrySpan StartTransaction(string name, string operation)
        {
            var span = new FakeTelemetrySpan(name, operation);
            Transactions.Add(span);
            return span;
        }

        public string? GetSentryTraceHeader() => null;
        public IReadOnlyDictionary<string, string>? GetEmbeddedBootstrapConfig() => null;
        public void Dispose() { }
    }

    internal sealed class FakeTelemetrySpan : ITelemetrySpan
    {
        public string Name { get; }
        public string Operation { get; }
        public Dictionary<string, string> Tags { get; } = new();
        public Dictionary<string, object> Extras { get; } = new();
        public List<FakeTelemetrySpan> Children { get; } = new();
        public bool Finished { get; private set; }
        public bool Disposed { get; private set; }
        public TelemetrySpanStatus? FinalStatus { get; private set; }
        public Exception? FinalException { get; private set; }

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

        // Finish is idempotent per the ITelemetrySpan contract: the first call wins.
        public void Finish(TelemetrySpanStatus status)
        {
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

        public void Dispose()
        {
            Disposed = true;
            Finish(TelemetrySpanStatus.Ok);
        }
    }
}
