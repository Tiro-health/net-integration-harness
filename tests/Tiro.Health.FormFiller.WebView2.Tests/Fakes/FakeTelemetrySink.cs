using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Every Finish call, in order — including the ones first-wins discards, and both
        /// overloads. FinalStatus alone cannot distinguish "finished once" from "finished
        /// three times and the first one happened to win", and the difference matters: the
        /// bug this instrumentation was added for was a real adapter that did NOT honour
        /// first-wins while this fake always did, so the fake absorbed the divergence it was
        /// standing in for.
        /// <para>
        /// Assert on the recorded STATUS, not on the call count, unless the count is what a
        /// test is genuinely about. ITelemetrySpan permits repeat finishes; a test demanding
        /// exactly one pins a caller's implementation choice rather than the contract, and
        /// goes red when a caller legitimately changes.
        /// </para>
        /// <para>Note a <c>using</c>-scoped span records a trailing Ok, since Dispose finishes.</para>
        /// </summary>
        public List<(TelemetrySpanStatus? Status, Exception Exception)> FinishCalls { get; }
            = new List<(TelemetrySpanStatus?, Exception)>();

        /// <summary>
        /// Exceptions handed to <see cref="Finish(Exception)"/> after the span had already
        /// finished. The contract allows associating these for trace linkage, so they are kept
        /// apart from <see cref="FinalException"/> rather than discarded or conflated with it —
        /// which is what the real Sentry adapter does.
        /// </summary>
        public List<Exception> LateAssociatedExceptions { get; } = new List<Exception>();

        /// <summary>Statuses from <see cref="FinishCalls"/>, for terser assertions.</summary>
        public IEnumerable<TelemetrySpanStatus> FinishStatuses
            => FinishCalls.Where(c => c.Status.HasValue).Select(c => c.Status.Value);

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
            FinishCalls.Add((status, null));
            // ITelemetrySpan contract: first finish wins.
            if (Finished) return;
            Finished = true;
            FinalStatus = status;
        }

        public void Finish(Exception ex)
        {
            FinishCalls.Add((null, ex));
            if (Finished)
            {
                // Not discarded: the contract lets a repeat exception finish associate its
                // exception for trace linkage, and the Sentry adapter does exactly that. A
                // fake that dropped it would disagree with the adapter in precisely the case
                // FinishCalls exists to make visible — while still leaving FinalStatus and
                // FinalException, which belong to the winning finish, untouched.
                LateAssociatedExceptions.Add(ex);
                return;
            }

            Finished = true;
            FinalException = ex;
        }

        // Mirrors the production contract: Dispose finishes with Ok unless already finished,
        // and records that the span was disposed (for `using`-scope assertions).
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            Finish(TelemetrySpanStatus.Ok);
        }
    }
}
