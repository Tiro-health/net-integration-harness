using System;
using System.Diagnostics;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// One span's records. Writes a line per call and forwards to the inner span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This type owns no status.</b> First-finish-wins belongs to the real backend's span — the
    /// Sentry adapter already implements it, including the case where the SDK finishes a span
    /// behind the wrapper's back — so re-deciding an outcome here could only ever disagree with
    /// it. What the transcript records instead is <b>what the caller asked for</b>, with a repeat
    /// <c>Finish</c> flagged <c>"repeat":true</c>. That is the more useful record: a trace that
    /// shipped green over a failure is diagnosed by seeing the second call, not by seeing the
    /// outcome the backend kept.
    /// </para>
    /// <para>
    /// <b>Tags and extras are written as they arrive, not accumulated onto the end record.</b> The
    /// viewer tags a span after starting it (<c>messageType</c>, <c>questionnaire_url</c>), so
    /// holding them until <c>Finish</c> would lose exactly those tags on a span that never
    /// finishes — the wedged-viewer case this file is for.
    /// </para>
    /// </remarks>
    internal sealed class FileTelemetrySpan : ITelemetrySpan
    {
        private readonly TelemetryRecordWriter _writer;
        private readonly string _spanId;
        private readonly ITelemetrySpan _inner;
        private readonly Stopwatch _clock;
        private readonly object _gate = new object();

        private bool _endRecorded;

        private FileTelemetrySpan(TelemetryRecordWriter writer, string spanId, ITelemetrySpan inner)
        {
            _writer = writer;
            _spanId = spanId;
            _inner = inner;
            _clock = Stopwatch.StartNew();
        }

        /// <summary>Writes the <c>span.start</c> record and returns the span that continues it.</summary>
        public static FileTelemetrySpan Start(
            TelemetryRecordWriter writer,
            string spanId,
            string parentSpanId,
            string name,
            string operation,
            ITelemetrySpan inner)
        {
            writer.Write("span.start", json =>
            {
                json.WriteString("span", spanId);
                if (parentSpanId == null) json.WriteNull("parent"); else json.WriteString("parent", parentSpanId);
                TelemetryRecordWriter.WriteValue(json, "name", name);
                json.WriteString("op", operation);
            });

            return new FileTelemetrySpan(writer, spanId, inner);
        }

        internal static string NewSpanId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        public void SetTag(string key, string value)
        {
            _writer.Write("span.tag", json =>
            {
                json.WriteString("span", _spanId);
                json.WriteString("k", key);
                TelemetryRecordWriter.WriteValue(json, "v", value);
            });

            Guard(() => _inner.SetTag(key, value), "Span.SetTag");
        }

        public void SetExtra(string key, object value)
        {
            _writer.Write("span.extra", json =>
            {
                json.WriteString("span", _spanId);
                json.WriteString("k", key);
                TelemetryRecordWriter.WriteExtraValue(json, "v", value);
            });

            Guard(() => _inner.SetExtra(key, value), "Span.SetExtra");
        }

        public ITelemetrySpan StartChild(string operation, string description)
        {
            var childId = NewSpanId();
            // Not `_inner`: substituting this span for its own child would mean the child's
            // Finish finished the parent.
            var innerChild = Guard(
                () => _inner.StartChild(operation, description),
                "Span.StartChild",
                NullTelemetrySink.NoopSpan);

            return Start(_writer, childId, _spanId, description, operation, innerChild);
        }

        public void Finish(TelemetrySpanStatus status)
        {
            bool repeat;
            lock (_gate)
            {
                repeat = _endRecorded;
                _endRecorded = true;
            }

            WriteEnd(StatusName(status), repeat, null);
            Guard(() => _inner.Finish(status), "Span.Finish");
        }

        public void Finish(Exception ex)
        {
            bool repeat;
            lock (_gate)
            {
                repeat = _endRecorded;
                _endRecorded = true;
            }

            WriteEnd(StatusName(TelemetrySpanStatus.InternalError), repeat, ex);

            // The error record carries the stack; span.end carries the outcome. Two records rather
            // than one fat one keeps every line short enough to read, and keeps the stack — the one
            // multi-line thing here — off the line a reader scans for outcomes.
            _writer.Write("error", json =>
            {
                json.WriteString("span", _spanId);
                json.WriteString("exc", ex?.GetType().FullName ?? "null");
                TelemetryRecordWriter.WriteValue(json, "msg", ex?.Message);
                TelemetryRecordWriter.WriteValue(json, "stack", ex?.StackTrace);
            });

            Guard(() => _inner.Finish(ex), "Span.Finish");
        }

        /// <summary>
        /// Scope-exit finish. Records nothing when the span has already been finished — unlike an
        /// explicit repeat <c>Finish</c>, a <c>using</c> block's exit asserts no outcome, and every
        /// span in the viewer is scope-wrapped around an explicit <c>Finish</c>, so recording it
        /// would put a redundant line after every span in the file. The inner span is disposed
        /// either way, so its own guard runs.
        /// </summary>
        public void Dispose()
        {
            bool alreadyEnded;
            lock (_gate)
            {
                alreadyEnded = _endRecorded;
                _endRecorded = true;
            }

            if (!alreadyEnded) WriteEnd(StatusName(TelemetrySpanStatus.Ok), repeat: false, exception: null);
            Guard(() => _inner.Dispose(), "Span.Dispose");
        }

        private void WriteEnd(string status, bool repeat, Exception exception)
        {
            var elapsedMs = _clock.ElapsedMilliseconds;

            _writer.Write("span.end", json =>
            {
                json.WriteString("span", _spanId);
                json.WriteString("status", status);
                // Precomputed so nothing reading the file has to subtract two timestamps.
                json.WriteNumber("ms", elapsedMs);
                if (exception != null) json.WriteString("exc", exception.GetType().FullName);
                if (repeat) json.WriteBoolean("repeat", true);
            });
        }

        /// <summary>
        /// Status as a name, never the enum's integer. <c>Ok</c> is the zero value, so numbers
        /// would make the commonest outcome indistinguishable from an unset field — the same trap
        /// the Sentry adapter documents around torn reads, in a file where nobody can see the enum.
        /// </summary>
        internal static string StatusName(TelemetrySpanStatus status)
        {
            switch (status)
            {
                case TelemetrySpanStatus.Ok: return "ok";
                case TelemetrySpanStatus.InvalidArgument: return "invalid_argument";
                case TelemetrySpanStatus.Cancelled: return "cancelled";
                case TelemetrySpanStatus.DeadlineExceeded: return "deadline_exceeded";
                case TelemetrySpanStatus.InternalError: return "internal_error";
                default: return "unknown_error";
            }
        }

        private void Guard(Action call, string member)
        {
            try { call(); }
            catch (Exception ex) { _writer.WriteInnerError(member, ex); }
        }

        private T Guard<T>(Func<T> call, string member, T fallback)
        {
            try { return call(); }
            catch (Exception ex)
            {
                _writer.WriteInnerError(member, ex);
                return fallback;
            }
        }
    }
}
