using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tiro.Health.FormFiller.WebView2.Telemetry
{
    /// <summary>
    /// Appends JSONL records to one file. The storage half of <see cref="FileTelemetrySink"/>;
    /// which file, and when to move to the next one, is <see cref="RollingTelemetryLog"/>'s job.
    /// One record per line, each carrying <c>type</c>, <c>ts</c> and <c>sid</c> so a line means
    /// something on its own: a <c>grep</c> for one session still works in a file holding a whole
    /// day of them, and a truncated tail costs one record rather than the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Records are serialized into a reusable buffer and only then copied to the file.</b>
    /// Writing a <see cref="Utf8JsonWriter"/> straight at the <see cref="FileStream"/> would leave
    /// half a record on disk when serialization throws part-way (an extra value that will not
    /// serialize), and half a record breaks the one-line-per-record invariant the whole format
    /// rests on. Buffering is also what lets the size cap be checked against the finished record,
    /// before any of it is committed.
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
        private readonly long _maxBytes;
        private readonly MemoryStream _buffer = new MemoryStream(512);

        private FileStream _stream;
        private long _bytesWritten;

        private TelemetryRecordWriter(FileStream stream, string path, long maxBytes)
        {
            _stream = stream;
            _maxBytes = maxBytes;
            FilePath = path;
            _bytesWritten = stream.Length;
        }

        /// <summary>Full path of the file being written.</summary>
        public string FilePath { get; }

        /// <summary>Bytes in the file, for the roller's size decisions.</summary>
        public long Bytes { get { lock (_gate) { return _bytesWritten; } } }

        /// <summary>
        /// Opens <paramref name="path"/> for append, or returns <c>null</c> when it cannot be
        /// opened. Failing to write telemetry must never fail the host, so there is no throwing
        /// overload: a null writer means the sink degrades to whatever its inner sink does.
        /// </summary>
        /// <remarks>
        /// <b><see cref="FileShare.Read"/> denies other writers, and that is load-bearing.</b>
        /// Readers are welcome — support copying the file while a clinic is running is the point —
        /// but a second <i>writer</i> on the same path is what the share mode has to refuse. A
        /// single <c>Write</c> call is not guaranteed atomic across handles, so two processes
        /// appending records could interleave halfway through a line and break the format. The
        /// refusal is also the signal <see cref="RollingTelemetryLog"/> uses to pick its own file:
        /// this returning null for a path is how a second process discovers it needs one.
        /// </remarks>
        /// <param name="path">Full path of the transcript to append to. Its directory is created.</param>
        /// <param name="maxBytes">Size cap. Records that would exceed it are refused, not written.</param>
        public static TelemetryRecordWriter TryOpen(string path, long maxBytes)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                return new TelemetryRecordWriter(stream, path, maxBytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes one record — <c>type</c>, <c>ts</c>, <c>sid</c>, then whatever
        /// <paramref name="fields"/> adds — and reports whether it went in.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the finished record would take the file past its cap. Nothing is
        /// written in that case and the writer stays usable, so the caller can roll to a fresh
        /// file and try again: <b>a full file must cost the oldest records, never the newest.</b>
        /// A record that fails for any other reason is swallowed and reported as written, since a
        /// sink that throws would take down the host it is reporting on.
        /// </returns>
        public bool TryWrite(string type, string sessionId, Action<Utf8JsonWriter> fields)
        {
            lock (_gate)
            {
                if (_stream == null) return true;

                try
                {
                    _buffer.SetLength(0);
                    using (var json = new Utf8JsonWriter(_buffer, WriterOptions))
                    {
                        json.WriteStartObject();
                        json.WriteString("type", type);
                        json.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                        json.WriteString("sid", sessionId ?? "process");
                        fields?.Invoke(json);
                        json.WriteEndObject();
                    }

                    if (_maxBytes > 0 && _bytesWritten + _buffer.Length + 1 > _maxBytes) return false;

                    var bytes = _buffer.GetBuffer();
                    var length = (int)_buffer.Length;
                    _stream.Write(bytes, 0, length);
                    _stream.WriteByte((byte)'\n');
                    _stream.Flush();
                    _bytesWritten += length + 1;
                    return true;
                }
                catch
                {
                    // Best-effort by contract. Reported as written so the caller does not roll the
                    // file over a fault that rolling cannot fix.
                    return true;
                }
            }
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
    }
}
