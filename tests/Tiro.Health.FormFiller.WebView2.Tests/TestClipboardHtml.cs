using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tiro.Health.FormFiller.WebView2.Tests
{
    /// <summary>
    /// The CF_HTML envelope. Its four offsets are <em>byte</em> offsets into the UTF-8 encoding,
    /// and getting that wrong is the classic way this code fails: with ASCII-only content byte
    /// and character counts coincide, so a character-counting implementation passes every naive
    /// test and then truncates the moment a real clinical note carries an accent or a µ.
    /// <para>
    /// So these tests don't assert on the header text — they decode the result as UTF-8, slice
    /// it at the offsets the header declares, and check what is actually there.
    /// </para>
    /// <para>
    /// <see cref="TiroClipboard.SetHtml"/> itself isn't covered here beyond its empty guard: it
    /// needs a real clipboard on an STA thread, which is a machine-wide resource another process
    /// can hold. Asserting on it would make this suite flaky for no gain, since the part that
    /// carries the risk is the framing below. Verified by hand on Windows instead.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestClipboardHtml
    {
        /// <summary>The offsets a CF_HTML header declares.</summary>
        private static (int StartHtml, int EndHtml, int StartFragment, int EndFragment) Offsets(string cfHtml)
        {
            int Read(string field)
            {
                var match = Regex.Match(cfHtml, field + @":(\d{10})\r\n");
                Assert.IsTrue(match.Success, $"header is missing a well-formed {field}");
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return (Read("StartHTML"), Read("EndHTML"), Read("StartFragment"), Read("EndFragment"));
        }

        /// <summary>The bytes between two offsets, decoded — i.e. what a reader would see.</summary>
        private static string Slice(string cfHtml, int from, int to)
        {
            var bytes = Encoding.UTF8.GetBytes(cfHtml);
            Assert.IsTrue(to <= bytes.Length, $"offset {to} runs past the {bytes.Length}-byte payload");
            return Encoding.UTF8.GetString(bytes, from, to - from);
        }

        [TestMethod]
        public void TheDeclaredOffsetsPointExactlyAtTheFragment()
        {
            const string fragment = "<p>Pulse regular at 78 bpm.</p>";

            var cfHtml = TiroClipboard.ToCfHtml(fragment);
            var offsets = Offsets(cfHtml);

            Assert.AreEqual(fragment, Slice(cfHtml, offsets.StartFragment, offsets.EndFragment),
                "StartFragment must land just past the marker comment and EndFragment just before its closer");
        }

        [TestMethod]
        public void StartHtmlAndEndHtmlSpanTheWholeDocument()
        {
            var cfHtml = TiroClipboard.ToCfHtml("<p>x</p>");
            var offsets = Offsets(cfHtml);

            var document = Slice(cfHtml, offsets.StartHtml, offsets.EndHtml);
            StringAssert.StartsWith(document, "<html>", "StartHTML must point at the opening tag, not into the header");
            StringAssert.EndsWith(document, "</html>", "EndHTML must point just past the closing tag");
            Assert.AreEqual(Encoding.UTF8.GetByteCount(cfHtml), offsets.EndHtml,
                "nothing may follow </html>, so EndHTML is the payload length");
        }

        [TestMethod]
        public void OffsetsAreByteOffsets_NotCharacterCounts()
        {
            // The regression this file exists for. Every character here is multi-byte in UTF-8,
            // so a character-counting implementation reports offsets that are too small and the
            // slice comes back truncated — while the ASCII tests above still pass.
            const string fragment = "<p>Température 37,8 °C — 5 µmol/L, “abrupt” onset</p>";

            var cfHtml = TiroClipboard.ToCfHtml(fragment);
            var offsets = Offsets(cfHtml);

            Assert.AreEqual(fragment, Slice(cfHtml, offsets.StartFragment, offsets.EndFragment));
            Assert.IsTrue(Encoding.UTF8.GetByteCount(fragment) > fragment.Length,
                "precondition: this fixture must actually contain multi-byte characters");
        }

        [TestMethod]
        public void OffsetsSurviveAFragmentLongEnoughToWidenThem()
        {
            // The header uses fixed-width (D10) offsets so its own length can be measured with
            // zeros. Should anyone switch to a bare {0}, the header would grow as the numbers
            // grew and every offset would shift by the difference — this catches that.
            var fragment = "<p>" + new string('a', 20000) + "</p>";

            var cfHtml = TiroClipboard.ToCfHtml(fragment);
            var offsets = Offsets(cfHtml);

            Assert.AreEqual(fragment, Slice(cfHtml, offsets.StartFragment, offsets.EndFragment));
        }

        [TestMethod]
        public void AnAlreadyFramedFragmentIsReturnedUnchanged()
        {
            // Makes the call idempotent, so a caller that pre-framed its HTML (or that passes
            // this method's own output back in) doesn't end up with a header inside the body,
            // which pastes as visible "Version:0.9" text.
            var once = TiroClipboard.ToCfHtml("<p>x</p>");

            Assert.AreEqual(once, TiroClipboard.ToCfHtml(once));
        }

        [TestMethod]
        public void NothingToFrameYieldsNull()
        {
            Assert.IsNull(TiroClipboard.ToCfHtml(null));
            Assert.IsNull(TiroClipboard.ToCfHtml(""));
        }

        [TestMethod]
        public void NothingToCopyIsNotPutOnTheClipboard()
        {
            // Guarded before touching the clipboard at all, so this reaches no real clipboard:
            // an empty copy must leave whatever the clinician already had alone.
            Assert.IsFalse(TiroClipboard.SetHtml(null, null));
            Assert.IsFalse(TiroClipboard.SetHtml("", ""));
        }
    }
}
