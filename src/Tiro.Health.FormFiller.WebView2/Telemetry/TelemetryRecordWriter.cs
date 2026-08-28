using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Appends JSONL records to one file — the storage half of <see cref="FileTelemetrySink"/>.
    /// One record per line, each carrying <c>type</c>, <c>ts</c> and <c>sid</c> so a line means
    /// something on its own: a <c>grep</c> for one message type still says which session it came
    /// from, and a truncated tail costs one record rather than the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Records are serialized into a reusable buffer and only then copied to the file.</b>
    /// Writing a <see cref="Utf8JsonWriter"/> straight at the <see cref="FileStream"/> would leave
    /// half a record on disk when serialization throws part-way (an extra value that will not
    /// serialize), and half a record breaks the one-line-per-record invariant the whole format
    /// rests on. Buffering also lets the size cap be checked before any bytes are committed.
    /// </para>
    /// <para>
    /// <b>Every record is flushed as it is written, synchronously, on the calling thread.</b> Both
    /// halves of that are deliberate. Flushing per record costs a write syscall, but the case this
    /// file exists for is a host that wedged or died, and a buffered tail is exactly the part a
    /// dead process never writes. Staying synchronous costs tens of microseconds per record at a
    /// volume of tens of records per form session — this is not a hot path — and a background
    /// queue would reintroduce the lost tail it was meant to avoid. Note the flush reaches the OS,
    /// not the platter: a killed process keeps its records, a power cut need not.
    /// </para>
    /// </remarks>
    internal sealed class TelemetryRecordWriter : IDisposable
    {
        /// <summary>Longest string written for a caller-supplied value (messages, tags, stacks).</summary>
        private const int MaxValueLength = 2048;

        /// <summary>Longest string written for a <c>SetExtra</c> value — see <see cref="WriteExtraValue"/>.</summary>
        private const int MaxExtraLength = 512;

        /// <summary>
        /// Inner-sink failures are logged at most this many times per file. A backend that throws
        /// once per call would otherwise fill the file with its own failure and bury the session
        /// the file was opened to record.
        /// </summary>
        private const int MaxInnerErrorsLogged = 10;

        /// <summary>
        /// Relaxed escaping, deliberately. The default encoder escapes <c>&lt;</c>, <c>&gt;</c> and
        /// <c>&amp;</c> so output is safe to drop into HTML, which turns a stack frame like
        /// <c>&lt;Main&gt;$</c> into <c>\u003CMain\u003E$</c> — still valid JSON, and noticeably
        /// worse for the two readers this file has. Nothing here is ever interpolated into markup.
        /// Quotes, backslashes and control characters are still escaped, so the one-record-per-line
        /// invariant holds against a multi-line stack trace.
        /// </summary>
        private static readonly JsonWriterOptions WriterOptions =
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly object _gate = new object();
        private readonly string _sessionId;
        private readonly long _maxBytes;
        private readonly MemoryStream _buffer = new MemoryStream(512);

        private FileStream _stream;
        private long _bytesWritten;
        private int _innerErrorsLogged;
        private bool _stopped;

        private TelemetryRecordWriter(FileStream stream, string sessionId, long maxBytes)
        {
            _stream = stream;
            _sessionId = sessionId;
            _maxBytes = maxBytes;
        }

        /// <summary>Full path of the file being written, for diagnostics.</summary>
        public string FilePath { get; private set; }

        /// <summary>
        /// Opens <paramref name="path"/> for append, or returns <c>null</c> when it cannot be
        /// opened. Failing to write telemetry must never fail the host, so there is no throwing
        /// overload: a null writer means the sink degrades to whatever its inner sink does.
        /// </summary>
        /// <param name="path">Full path of the transcript to append to. Its directory is created.</param>
        /// <param name="shortSessionId">
        /// The abbreviated session id stamped on every line. Short rather than the full GUID
        /// because it is repeated on every record, and 36 characters of it per line is most of
        /// what makes a transcript unreadable at a glance; the full id is in the header, which is
        /// what pairs the file with a Sentry event.
        /// </param>
        /// <param name="maxBytes">Size cap; reaching it writes a <c>trunc</c> record and stops.</param>
        public static TelemetryRecordWriter TryOpen(string path, string shortSessionId, long maxBytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // FileShare.Read so support can copy or tail the file while the host still runs —
                // the machine is usually not one anybody can stop mid-clinic.
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                return new TelemetryRecordWriter(stream, shortSessionId, maxBytes) { FilePath = path, _bytesWritten = stream.Length };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes one record: <c>type</c>, <c>ts</c> and <c>sid</c>, then whatever
        /// <paramref name="fields"/> adds. Best-effort — any failure is swallowed, because a sink
        /// that throws would take down the host it is reporting on.
        /// </summary>
        public void Write(string type, Action<Utf8JsonWriter> fields)
        {
            lock (_gate)
            {
                if (_stopped || _stream == null) return;

                try
                {
                    _buffer.SetLength(0);
                    using (var json = new Utf8JsonWriter(_buffer, WriterOptions))
                    {
                        json.WriteStartObject();
                        json.WriteString("type", type);
                        json.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                        json.WriteString("sid", _sessionId);
                        fields?.Invoke(json);
                        json.WriteEndObject();
                    }

                    if (_maxBytes > 0 && _bytesWritten + _buffer.Length > _maxBytes)
                    {
                        // Say so in the file rather than just going quiet: a reader has no other way
                        // to tell a capped file from a session that stopped emitting.
                        _stopped = true;
                        WriteRaw(cap => cap.WriteNumber("limit_bytes", _maxBytes), "trunc");
                        return;
                    }

                    CommitBuffer();
                }
                catch
                {
                    // Best-effort by contract.
                }
            }
        }

        /// <summary>
        /// Records that the inner sink threw, and swallows it. This is where a Sentry that cannot
        /// be initialised — a bad DSN, a broken options object — becomes visible instead of
        /// vanishing. It does <b>not</b> catch a firewall silently dropping envelopes: the Sentry
        /// transport does not throw for that, so a healthy-looking session with nothing on the
        /// Sentry side stays the only signal.
        /// </summary>
        public void WriteInnerError(string member, Exception ex)
        {
            lock (_gate)
            {
                if (_innerErrorsLogged >= MaxInnerErrorsLogged) return;
                _innerErrorsLogged++;
            }

            Write("inner.error", json =>
            {
                json.WriteString("member", member);
                json.WriteString("exc", ex.GetType().FullName);
                json.WriteString("msg", Trim(Redact(ex.Message), MaxValueLength));
            });
        }

        /// <summary>Flushes the underlying stream. Cheap — records are already flushed per write.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                try { _stream?.Flush(); } catch { /* best-effort */ }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _stream?.Dispose(); } catch { /* best-effort */ }
                _stream = null;
            }
        }

        /// <summary>
        /// Serializes a <c>SetExtra</c> value. Strings and primitives go in as themselves;
        /// <b>anything else is written as a truncated <c>ToString()</c>, never reflected over.</b>
        /// The interface takes <see cref="object"/>, so serializing an arbitrary graph would put
        /// whatever a caller happened to attach — a FHIR resource, say — into a file whose whole
        /// purpose is to be sent somewhere. The file writes nothing Sentry would not have been
        /// given, and this is the member where that could otherwise stop being true.
        /// </summary>
        internal static void WriteExtraValue(Utf8JsonWriter json, string name, object value)
        {
            if (value == null) { json.WriteNull(name); return; }

            switch (value)
            {
                case string s: json.WriteString(name, Trim(Redact(s), MaxExtraLength)); return;
                case bool b: json.WriteBoolean(name, b); return;
                case int i: json.WriteNumber(name, i); return;
                case long l: json.WriteNumber(name, l); return;
                case double d: json.WriteNumber(name, d); return;
                case float f: json.WriteNumber(name, f); return;
                case decimal m: json.WriteNumber(name, m); return;
                default:
                    json.WriteString(name, Trim(Redact(Convert.ToString(value, CultureInfo.InvariantCulture)), MaxExtraLength));
                    return;
            }
        }

        /// <summary>Writes a caller-supplied string, redacted and length-capped.</summary>
        internal static void WriteValue(Utf8JsonWriter json, string name, string value)
        {
            if (value == null) json.WriteNull(name);
            else json.WriteString(name, Trim(Redact(value), MaxValueLength));
        }

        internal static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "…[trimmed]";
        }

        /// <summary>
        /// Replaces the user's profile directory with <c>%USERPROFILE%</c>. Windows account names
        /// are routinely a person's name, and they turn up throughout stack traces and file paths,
        /// so a log built to leave the hospital should not carry one. Deliberately narrow: a
        /// general-purpose scrubber over free text mangles the diagnostics without ever being able
        /// to promise much, and the standing rule that callers pass no PHI to telemetry is what
        /// actually keeps this file clean.
        /// </summary>
        internal static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && value.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0)
                value = value.Replace(profile, "%USERPROFILE%");

            return value;
        }

        /// <summary>Writes a record bypassing the size cap — used for the cap's own notice.</summary>
        private void WriteRaw(Action<Utf8JsonWriter> fields, string type)
        {
            try
            {
                _buffer.SetLength(0);
                using (var json = new Utf8JsonWriter(_buffer, WriterOptions))
                {
                    json.WriteStartObject();
                    json.WriteString("type", type);
                    json.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                    json.WriteString("sid", _sessionId);
                    fields?.Invoke(json);
                    json.WriteEndObject();
                }
                CommitBuffer();
            }
            catch
            {
                // Best-effort by contract.
            }
        }

        private void CommitBuffer()
        {
            var bytes = _buffer.GetBuffer();
            var length = (int)_buffer.Length;
            _stream.Write(bytes, 0, length);
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
            _bytesWritten += length + 1;
        }
    }
}
