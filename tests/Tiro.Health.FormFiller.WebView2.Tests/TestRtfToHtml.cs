using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// <see cref="TiroRtf.ToHtml"/> — the convenience RTF-to-HTML converter.
    /// </summary>
    /// <remarks>
    /// Every expected string here was produced by running the converter and reading the output,
    /// then judged against what the RTF actually says — not written from what the implementation
    /// looked like it would do. The fixtures are deliberately the constructs that break
    /// hand-rolled converters: character encoding, group scoping, and destinations whose content
    /// is machinery rather than text.
    /// </remarks>
    [TestClass]
    public class TestRtfToHtml
    {
        private const string Preamble = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22";

        [TestMethod]
        public void EmphasisBecomesSemanticTags()
        {
            // Semantic tags rather than CSS classes, because what is handed over is a fragment
            // with no stylesheet — class-based styling would lose every rule with it.
            var html = TiroRtf.ToHtml(Preamble
                + @"{\b Assessment.} Findings consistent with the clinical picture; "
                + @"{\i no further imaging indicated}.\par}");

            Assert.AreEqual(
                "<p><b>Assessment.</b> Findings consistent with the clinical picture; "
                + "<i>no further imaging indicated</i>.</p>",
                html);
        }

        [TestMethod]
        public void NestedEmphasisStaysWellFormed()
        {
            // Closing every tag and reopening what is still wanted, rather than closing
            // selectively: it costs a redundant </b><b> and guarantees the nesting is legal.
            // Turning bold off inside italic would otherwise emit <b><i>…</b>.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi{\b bold {\i both} bold}\par}");

            Assert.AreEqual("<p><b>bold </b><b><i>both</i></b><b> bold</b></p>", html);
        }

        [TestMethod]
        public void FormattingIsScopedToItsGroup()
        {
            // The brace is what ends the bold, not a \b0 — so a converter that doesn't keep a
            // stack of formatting states bolds the rest of the document.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi normal {\b bold} normal again\par}");

            Assert.AreEqual("<p>normal <b>bold</b> normal again</p>", html);
        }

        [TestMethod]
        public void UnderlineIsTurnedOffByUlnoneAsWellAsUl0()
        {
            Assert.AreEqual("<p><u>under</u>plain</p>",
                TiroRtf.ToHtml(@"{\rtf1\ansi\ul under\ulnone plain\par}"));
            Assert.AreEqual("<p><u>under</u>plain</p>",
                TiroRtf.ToHtml(@"{\rtf1\ansi\ul under\ul0 plain\par}"));
        }

        [TestMethod]
        public void PlainResetsAllCharacterFormatting()
        {
            Assert.AreEqual("<p><b><i>both</i></b>neither</p>",
                TiroRtf.ToHtml(@"{\rtf1\ansi\b\i both\plain neither\par}"));
        }

        [TestMethod]
        public void ParagraphsAndLineBreaks()
        {
            Assert.AreEqual("<p>one</p><p>two</p>", TiroRtf.ToHtml(@"{\rtf1\ansi one\par two\par}"));
            Assert.AreEqual("<p>first<br>second</p>", TiroRtf.ToHtml(@"{\rtf1\ansi first\line second\par}"));
        }

        [TestMethod]
        public void ATrailingEmptyParagraphIsDropped()
        {
            // Near-universal in RTF writers' output, and an empty <p> in a clinical answer is
            // noise rather than content.
            Assert.AreEqual("<p>text</p>", TiroRtf.ToHtml(@"{\rtf1\ansi text\par\par}"));
        }

        [TestMethod]
        public void CharacterEncodingSurvives()
        {
            // The reason this converter is worth having. \'hh is a byte in the codepage the
            // document declares, and \uN a codepoint followed by fallback characters to skip.
            // Getting either wrong is how an accent in a clinical note turns to mojibake — and
            // ASCII-only fixtures never reveal it.
            var html = TiroRtf.ToHtml(Preamble + @"Temp\'e9rature 37,8 \'b0C, 5 \u181?mol/L\par}");

            Assert.AreEqual("<p>Température 37,8 °C, 5 µmol/L</p>", html);
        }

        [TestMethod]
        public void TheUnicodeFallbackCharacterIsSkipped_NotEmitted()
        {
            // \u181? means "µ, and a reader that can't do Unicode should show ?". Emitting both
            // leaves stray question marks through the text.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi 5 \u181?mol\par}");

            Assert.AreEqual("<p>5 µmol</p>", html);
            Assert.IsFalse(html.Contains("?"), "the fallback character must not survive");
        }

        [TestMethod]
        public void HtmlSpecialCharactersAreEscaped()
        {
            Assert.AreEqual("<p>a &lt; b &amp; c &gt; d</p>",
                TiroRtf.ToHtml(@"{\rtf1\ansi a < b & c > d\par}"));
        }

        [TestMethod]
        public void EscapedBracesAndBackslashesBecomeLiteralText()
        {
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi literal \{brace\} and backslash \\\par}");

            StringAssert.Contains(html, "literal {brace} and backslash \\");
        }

        [TestMethod]
        public void MachineryDestinationsAreSkippedWholesale()
        {
            // Their content is tables and definitions, not document text. A converter that
            // treats them as text emits font names and colour numbers into the answer.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi{\fonttbl{\f0 Calibri;}}"
                + @"{\colortbl;\red255\green0\blue0;}{\stylesheet{\s0 Normal;}}visible\par}");

            Assert.AreEqual("<p>visible</p>", html);
        }

        [TestMethod]
        public void AHyperlinksVisibleTextSurvivesEvenThoughTheTargetDoesNot()
        {
            // {\field{\*\fldinst{HYPERLINK "..."}}{\fldrslt{\ul text}}} — the instruction is
            // marked \*, so it is skipped, and the result group is ordinary text. Skipping the
            // whole \field instead would delete the words the clinician wrote.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi see {\field{\*\fldinst{HYPERLINK ""https://x.example""}}"
                + @"{\fldrslt{\ul AF guidance}}} now\par}");

            Assert.AreEqual("<p>see <u>AF guidance</u> now</p>", html);
            Assert.IsFalse(html.Contains("HYPERLINK"), "the field instruction must not leak into the text");
        }

        [TestMethod]
        public void ATableFlattensButStaysReadable()
        {
            // The field's editor has no table node, so structure cannot survive. What it must
            // not do is run the cells together: "Heart rate78 bpm" is worse than losing the grid.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi\trowd\cellx3000\cellx6000 Heart rate\cell 78 bpm\cell\row"
                + @"\trowd\cellx3000\cellx6000 BP\cell 128/76\cell\row}");

            Assert.AreEqual("<p>Heart rate 78 bpm </p><p>BP 128/76 </p>", html);
        }

        [TestMethod]
        public void PunctuationSpelledAsControlWordsIsKept()
        {
            // Word emits these constantly. Without them the quotes and dashes simply vanish,
            // which reads as text corruption rather than as lost formatting.
            var html = TiroRtf.ToHtml(@"{\rtf1\ansi \ldblquote abrupt\rdblquote  onset \endash  mild \bullet  item\par}");

            StringAssert.Contains(html, "“abrupt”");
            StringAssert.Contains(html, "–");
            StringAssert.Contains(html, "•");
        }

        [TestMethod]
        public void NothingToConvertYieldsAnEmptyString()
        {
            Assert.AreEqual(string.Empty, TiroRtf.ToHtml(null));
            Assert.AreEqual(string.Empty, TiroRtf.ToHtml(""));
        }

        [TestMethod]
        public void MalformedRtfDoesNotThrow()
        {
            // Unlike ToPlainText, which lets RichTextBox reject junk, this walks whatever it is
            // given. A menu item failing on a stray brace would be worse than a partial result.
            Assert.AreEqual("<p>text</p>", TiroRtf.ToHtml(@"{\rtf1\ansi text\par"));
            Assert.AreEqual("<p>text</p>", TiroRtf.ToHtml(@"\rtf1 text\par}}}"));
            TiroRtf.ToHtml(@"{\rtf1\ansi \u");
            TiroRtf.ToHtml(@"{\rtf1\ansi \'z");
            TiroRtf.ToHtml("{");
        }

        [TestMethod]
        public void TheOutputIsAFragment_NotAWholeDocument()
        {
            // AddInsertItem and the page both expect body-level markup.
            var html = TiroRtf.ToHtml(Preamble + @"text\par}");

            Assert.IsFalse(html.Contains("<html"), "no document wrapper");
            Assert.IsFalse(html.Contains("<head"), "no head");
            Assert.IsFalse(html.Contains("<body"), "no body");
            StringAssert.StartsWith(html, "<p>");
        }
    }
}
