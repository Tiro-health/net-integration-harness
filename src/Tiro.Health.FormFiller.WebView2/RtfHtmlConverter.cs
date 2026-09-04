using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// Turns RTF into the HTML fragment a form's rich-text answer can store. Reached through
    /// <see cref="TiroRtf.ToHtml"/>.
    /// </summary>
    /// <remarks>
    /// Scoped deliberately to what the target field can keep — inline emphasis and paragraphs.
    /// Everything else is flattened rather than approximated, because a table or a list has no
    /// node in that editor and would collapse to paragraphs however carefully it was converted.
    /// Chasing fidelity past the field's ceiling would only produce markup that gets thrown away.
    /// </remarks>
    internal sealed class RtfHtmlConverter
    {
        // Destinations whose *content* is machinery rather than text. Skipped whole, along with
        // anything marked \* (an ignorable destination), which is also how the HYPERLINK
        // instruction inside a \field disappears while its visible \fldrslt text survives.
        private static readonly HashSet<string> SkippedDestinations = new HashSet<string>(StringComparer.Ordinal)
        {
            "fonttbl", "colortbl", "stylesheet", "listtable", "listoverridetable", "info",
            "pict", "object", "header", "footer", "headerl", "headerr", "footerl", "footerr",
            "footnote", "annotation", "themedata", "colorschememapping", "latentstyles",
            "datastore", "xmlnstbl", "pgptbl", "revtbl", "generator",
        };

        private struct Format
        {
            public bool Bold;
            public bool Italic;
            public bool Underline;
            /// <summary>Characters to skip after a \uN, from \ucN. Group-scoped, default 1.</summary>
            public int UnicodeSkip;
        }

        private readonly string _rtf;
        private int _pos;

        private Format _format;
        private readonly Stack<Format> _groups = new Stack<Format>();

        private readonly List<string> _paragraphs = new List<string>();
        private readonly StringBuilder _paragraph = new StringBuilder();
        private bool _openBold, _openItalic, _openUnderline;

        // \'hh bytes are buffered so a run of them decodes as one sequence. Single-byte
        // codepages don't need it; a double-byte one (Shift-JIS, GBK) does, because there a
        // character is spread over two escapes and decoding them separately yields two
        // replacement characters instead of one letter.
        private readonly List<byte> _pendingBytes = new List<byte>();
        private Encoding _encoding = ResolveEncoding(1252);

        internal RtfHtmlConverter(string rtf)
        {
            _rtf = rtf;
            _format.UnicodeSkip = 1;
        }

        internal string Run()
        {
            while (_pos < _rtf.Length)
            {
                var c = _rtf[_pos];
                if (c == '{')
                {
                    _pos++;
                    _groups.Push(_format);
                }
                else if (c == '}')
                {
                    _pos++;
                    FlushBytes();
                    if (_groups.Count > 0) _format = _groups.Pop();
                }
                else if (c == '\\')
                {
                    ReadControl();
                }
                else if (c == '\r' || c == '\n')
                {
                    // Line breaks in the source are formatting of the RTF file, not of the
                    // document. Only \par and \line break a paragraph.
                    _pos++;
                }
                else
                {
                    FlushBytes();
                    AppendText(c.ToString());
                    _pos++;
                }
            }

            FlushBytes();
            EndParagraph();
            return string.Join(string.Empty, _paragraphs.ToArray());
        }

        // -----------------------------------------------------------------------------
        // Control words and symbols
        // -----------------------------------------------------------------------------

        private void ReadControl()
        {
            _pos++;                                   // past the backslash
            if (_pos >= _rtf.Length) return;

            var c = _rtf[_pos];
            if (!char.IsLetter(c))
            {
                ReadControlSymbol(c);
                return;
            }

            var start = _pos;
            while (_pos < _rtf.Length && char.IsLetter(_rtf[_pos])) _pos++;
            var word = _rtf.Substring(start, _pos - start);

            // Optional signed parameter.
            int? parameter = null;
            var negative = false;
            if (_pos < _rtf.Length && _rtf[_pos] == '-') { negative = true; _pos++; }
            if (_pos < _rtf.Length && char.IsDigit(_rtf[_pos]))
            {
                var digits = _pos;
                while (_pos < _rtf.Length && char.IsDigit(_rtf[_pos])) _pos++;
                if (int.TryParse(_rtf.Substring(digits, _pos - digits), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var value))
                    parameter = negative ? -value : value;
            }
            // A single space after a control word is its delimiter, not text.
            if (_pos < _rtf.Length && _rtf[_pos] == ' ') _pos++;

            Apply(word, parameter);
        }

        private void ReadControlSymbol(char symbol)
        {
            switch (symbol)
            {
                case '\\': case '{': case '}':
                    FlushBytes();
                    AppendText(symbol.ToString());
                    _pos++;
                    break;

                case '\'':
                    // \'hh — one byte in the current codepage. Buffered, not decoded yet.
                    _pos++;
                    if (_pos + 1 < _rtf.Length
                        && byte.TryParse(_rtf.Substring(_pos, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out var b))
                    {
                        _pendingBytes.Add(b);
                        _pos += 2;
                    }
                    break;

                case '*':
                    // Ignorable destination: the reader is told it may skip the whole group.
                    _pos++;
                    SkipRestOfGroup();
                    break;

                case '~':
                    FlushBytes();
                    AppendText(" ");             // non-breaking space
                    _pos++;
                    break;

                case '_':
                    FlushBytes();
                    AppendText("‑");             // non-breaking hyphen
                    _pos++;
                    break;

                case '-':
                    _pos++;                           // optional hyphen: renders as nothing
                    break;

                case '\r': case '\n':
                    // An escaped newline means \par in some writers' output.
                    _pos++;
                    EndParagraph();
                    break;

                default:
                    _pos++;                           // unknown symbol: drop it
                    break;
            }
        }

        private void Apply(string word, int? parameter)
        {
            switch (word)
            {
                // --- character formatting -------------------------------------------------
                case "b": _format.Bold = parameter != 0; break;
                case "i": _format.Italic = parameter != 0; break;
                case "ul": _format.Underline = parameter != 0; break;
                case "ulnone": _format.Underline = false; break;
                case "plain":
                    _format.Bold = _format.Italic = _format.Underline = false;
                    break;

                // --- breaks ---------------------------------------------------------------
                case "par":
                case "sect":
                    FlushBytes();
                    EndParagraph();
                    break;
                case "line":
                    FlushBytes();
                    AppendRaw("<br>");
                    break;
                case "tab":
                case "emspace":
                case "enspace":
                    FlushBytes();
                    AppendText(" ");
                    break;

                // A flattened table still reads: cells separated, rows broken. Without these,
                // \trowd markup collapses into "Heart rate78 bpm" with the values run together.
                case "cell":
                    FlushBytes();
                    AppendText(" ");
                    break;
                case "row":
                    FlushBytes();
                    EndParagraph();
                    break;

                // Characters RTF spells as control words rather than bytes. Word emits these
                // constantly, and without them the punctuation simply disappears from the text.
                case "bullet": FlushBytes(); AppendText("\u2022"); break;
                case "endash": FlushBytes(); AppendText("\u2013"); break;
                case "emdash": FlushBytes(); AppendText("\u2014"); break;
                case "lquote": FlushBytes(); AppendText("\u2018"); break;
                case "rquote": FlushBytes(); AppendText("\u2019"); break;
                case "ldblquote": FlushBytes(); AppendText("\u201C"); break;
                case "rdblquote": FlushBytes(); AppendText("\u201D"); break;

                // --- encoding -------------------------------------------------------------
                case "ansicpg":
                    if (parameter.HasValue) SetCodePage(parameter.Value);
                    break;
                case "uc":
                    if (parameter.HasValue && parameter.Value >= 0) _format.UnicodeSkip = parameter.Value;
                    break;
                case "u":
                    if (parameter.HasValue) AppendUnicode(parameter.Value);
                    break;

                // --- ignored, but their group's text is kept ------------------------------
                case "pard":
                    // Paragraph defaults. Character formatting is \plain's job, so nothing here.
                    break;

                default:
                    if (SkippedDestinations.Contains(word))
                    {
                        FlushBytes();
                        SkipRestOfGroup();
                    }
                    // Everything else — fonts, colours, sizes, alignment, list plumbing — is
                    // formatting the target field cannot store. Dropped, while the text it
                    // wraps is kept.
                    break;
            }
        }

        // -----------------------------------------------------------------------------
        // Text, encoding and output
        // -----------------------------------------------------------------------------

        private void SetCodePage(int codePage) => _encoding = ResolveEncoding(codePage) ?? _encoding;

        /// <summary>
        /// The encoding for a codepage, or null when the platform doesn't have it. Windows-1252
        /// is built into .NET Framework, so the common case never fails; the guard is here so an
        /// exotic \ansicpg — or a host that registers fewer codepages — degrades to the current
        /// one instead of throwing out of a converter.
        /// </summary>
        private static Encoding ResolveEncoding(int codePage)
        {
            try { return Encoding.GetEncoding(codePage); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>Decodes the buffered \'hh bytes as one sequence and appends the result.</summary>
        private void FlushBytes()
        {
            if (_pendingBytes.Count == 0) return;
            var bytes = _pendingBytes.ToArray();
            _pendingBytes.Clear();
            AppendText(_encoding.GetString(bytes));
        }

        /// <summary>
        /// Appends the character a \uN names, then skips the ASCII fallback that follows it.
        /// </summary>
        /// <remarks>
        /// The parameter is a signed 16-bit value, so codepoints above U+7FFF arrive negative.
        /// The fallback is there for readers that don't understand \u at all; a reader that does
        /// must skip exactly \uc characters, counting a \'hh escape as one. Skipping the wrong
        /// number is how stray question marks end up in the text.
        /// </remarks>
        private void AppendUnicode(int codeUnit)
        {
            FlushBytes();
            if (codeUnit < 0) codeUnit += 65536;
            AppendText(((char)codeUnit).ToString());

            for (var skipped = 0; skipped < _format.UnicodeSkip && _pos < _rtf.Length; skipped++)
            {
                if (_rtf[_pos] == '\\' && _pos + 1 < _rtf.Length && _rtf[_pos + 1] == '\'')
                {
                    _pos += 4;                        // \'hh counts as one character
                }
                else if (_rtf[_pos] == '{' || _rtf[_pos] == '}' || _rtf[_pos] == '\\')
                {
                    break;                            // structure, not fallback text
                }
                else
                {
                    _pos++;
                }
            }
        }

        private void SkipRestOfGroup()
        {
            var depth = 1;
            while (_pos < _rtf.Length && depth > 0)
            {
                var c = _rtf[_pos];
                if (c == '\\' && _pos + 1 < _rtf.Length) { _pos += 2; continue; }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                _pos++;
            }
            // The closing brace was consumed here, so restore the enclosing group's format.
            if (_groups.Count > 0) _format = _groups.Pop();
        }

        private void AppendText(string text)
        {
            SyncTags();
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': _paragraph.Append("&amp;"); break;
                    case '<': _paragraph.Append("&lt;"); break;
                    case '>': _paragraph.Append("&gt;"); break;
                    default: _paragraph.Append(c); break;
                }
            }
        }

        private void AppendRaw(string html)
        {
            SyncTags();
            _paragraph.Append(html);
        }

        /// <summary>
        /// Brings the open tags in line with the current format.
        /// </summary>
        /// <remarks>
        /// Closes everything and reopens what is needed, rather than closing selectively. It
        /// costs the occasional redundant <c>&lt;/b&gt;&lt;b&gt;</c> and guarantees the nesting
        /// is well formed, which selective closing does not: turning bold off inside italic
        /// would otherwise emit <c>&lt;b&gt;&lt;i&gt;…&lt;/b&gt;</c>.
        /// </remarks>
        private void SyncTags()
        {
            var wanted = _format;
            if (_openBold == wanted.Bold && _openItalic == wanted.Italic
                && _openUnderline == wanted.Underline) return;

            CloseTags();
            if (wanted.Bold) { _paragraph.Append("<b>"); _openBold = true; }
            if (wanted.Italic) { _paragraph.Append("<i>"); _openItalic = true; }
            if (wanted.Underline) { _paragraph.Append("<u>"); _openUnderline = true; }
        }

        private void CloseTags()
        {
            if (_openUnderline) { _paragraph.Append("</u>"); _openUnderline = false; }
            if (_openItalic) { _paragraph.Append("</i>"); _openItalic = false; }
            if (_openBold) { _paragraph.Append("</b>"); _openBold = false; }
        }

        private void EndParagraph()
        {
            CloseTags();
            var text = _paragraph.ToString();
            _paragraph.Length = 0;

            // Empty paragraphs are dropped: a trailing \par is near-universal in RTF writers,
            // and an empty <p> in an answer is noise rather than content.
            if (text.Trim().Length == 0) return;
            _paragraphs.Add("<p>" + text + "</p>");
        }
    }
}
