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

        /// <summary>
        /// Longest string written for a name-like field: a tag or extra key, a breadcrumb category,
        /// an operation, a release or environment. Shorter than a value because these are
        /// identifiers, and a caller who puts prose in one has already lost the plot.
        /// </summary>
        private const int MaxKeyLength = 256;

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
        /// Serializes a <c>SetExtra</c> value. Strings and primitives are written; <b>every other
        /// type is written as its type name and nothing else.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the highest-stakes member in the file, because <see cref="ITelemetrySpan.SetExtra"/>
        /// takes <see cref="object"/> and the transcript is built to be emailed to a vendor.
        /// </para>
        /// <para>
        /// An earlier version called <c>ToString()</c> on anything unrecognised, on the reasoning
        /// that it was not <i>reflection</i> over the graph. That reasoning was wrong in exactly the
        /// cases that matter: for a C# <c>record</c>, an anonymous type, a <c>JsonElement</c>, an
        /// <c>XElement</c>, a <c>StringBuilder</c>, a <c>Uri</c> or an <see cref="Exception"/>,
        /// <c>ToString()</c> <i>is</i> a full state dump. Attaching a QuestionnaireResponse while
        /// debugging a rejected submission — the obvious thing to do — put a named patient, an NHS
        /// number and a diagnosis into the file.
        /// </para>
        /// <para>
        /// So no caller code runs here at all. A type name still answers the question a reader
        /// actually has ("what was attached?") without carrying its contents, and it cannot throw,
        /// cannot block, and cannot re-enter this writer while its buffer is in use.
        /// </para>
        /// </remarks>
        internal static void WriteExtraValue(Utf8JsonWriter json, string name, object value)
        {
            if (value == null) { json.WriteNull(name); return; }

            switch (value)
            {
                case string s: json.WriteString(name, Trim(Redact(s), MaxValueLength)); return;
                case bool b: json.WriteBoolean(name, b); return;
                case int i: json.WriteNumber(name, i); return;
                case long l: json.WriteNumber(name, l); return;
                case short sh: json.WriteNumber(name, sh); return;
                case byte by: json.WriteNumber(name, by); return;
                case uint ui: json.WriteNumber(name, ui); return;
                case ulong ul: json.WriteNumber(name, ul); return;
                case double d: json.WriteNumber(name, d); return;
                case float f: json.WriteNumber(name, f); return;
                case decimal m: json.WriteNumber(name, m); return;
                case DateTime dt: json.WriteString(name, dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)); return;
                case Guid g: json.WriteString(name, g.ToString("D")); return;
                default:
                    json.WriteString(name, Trim("<" + TypeName(value.GetType()) + ">", MaxKeyLength));
                    return;
            }
        }

        /// <summary>
        /// A type's name without assembly qualification. <see cref="Type.FullName"/> spells generic
        /// arguments out in full — version, culture and public key token for each one — so an
        /// anonymous type of two strings arrives as 200-odd characters of metadata that tells a
        /// reader nothing. This keeps the namespace, which is the part that identifies what was
        /// attached, and renders arguments the way source would.
        /// </summary>
        private static string TypeName(Type type)
        {
            if (!type.IsGenericType) return type.FullName ?? type.Name;

            var name = type.FullName ?? type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);

            var arguments = type.GetGenericArguments();
            var rendered = new string[arguments.Length];
            for (var i = 0; i < arguments.Length; i++) rendered[i] = TypeName(arguments[i]);

            return name + "<" + string.Join(",", rendered) + ">";
        }

        /// <summary>
        /// Writes a name-like field — a key, category, operation, release or environment — redacted
        /// and capped at <see cref="MaxKeyLength"/>. Separate from <see cref="WriteValue"/> only in
        /// the cap; the point is that these went through <i>neither</i> before, so a breadcrumb
        /// category was an unbounded, unscrubbed channel sitting next to a scrubbed message on the
        /// same line.
        /// </summary>
        internal static void WriteKey(Utf8JsonWriter json, string name, string value)
        {
            if (value == null) json.WriteNull(name);
            else json.WriteString(name, Trim(Redact(value), MaxKeyLength));
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
