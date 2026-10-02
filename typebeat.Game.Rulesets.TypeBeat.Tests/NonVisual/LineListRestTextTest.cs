// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The line list's rest view text rules (backlog 378): the plain text a row shows at rest, the
    /// gaps its marks sit in, and the caret re-map from that text onto the pipe form a focus swaps in.
    /// </summary>
    [TestFixture]
    public class LineListRestTextTest
    {
        [TestCase("Did I di|sap|point you?", "Did I disappoint you?")]
        [TestCase("blan|ket o|ver", "blanket over")]
        [TestCase("no splits here", "no splits here")]
        [TestCase("", "")]
        public void TestRestTextStripsPipes(string pipe, string rest) => Assert.That(LineListPanel.RestTextOf(pipe), Is.EqualTo(rest));

        [Test]
        public void TestRestTextWithoutPipesIsTheSameInstance()
        {
            const string text = "nothing to strip";
            Assert.That(LineListPanel.RestTextOf(text), Is.SameAs(text));
        }

        [TestCase("Did I di|sap|point you?", new[] { 8, 11 })]
        [TestCase("blan|ket o|ver", new[] { 4, 9 })]
        [TestCase("li|ne", new[] { 2 })]
        [TestCase("plain", new int[0])]
        public void TestRestGaps(string pipe, int[] gaps) => Assert.That(LineListPanel.RestGapsOf(pipe), Is.EqualTo(gaps));

        [Test]
        public void TestRestGapsNeverAtTheEdgesAndNeverTwice()
        {
            // A pipe at the text's own edge has no gap to mark, and a doubled pipe is one gap.
            Assert.That(LineListPanel.RestGapsOf("|ab||c|"), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void TestNoPipesAllocatesNoGaps() => Assert.That(LineListPanel.RestGapsOf("plain"), Is.SameAs(LineListPanel.RestGapsOf("other")));

        // "Did I di|sap|point you?": pipes at 8 and 12.
        [TestCase(0, 0)]
        [TestCase(7, 7)] // inside "di", before the first pipe
        [TestCase(8, 8)] // the marked gap: in FRONT of its pipe
        [TestCase(9, 10)] // in front of 'a': one pipe behind
        [TestCase(11, 12)] // the second marked gap: in front of its pipe
        [TestCase(12, 14)] // in front of 'o': both pipes behind
        [TestCase(21, 23)] // the end of the text
        [TestCase(99, 23)] // clamped
        [TestCase(-3, 0)] // clamped
        public void TestCaretRemap(int restCaret, int pipeCaret) =>
            Assert.That(LineListPanel.PipeCaretFor("Did I di|sap|point you?", restCaret), Is.EqualTo(pipeCaret));

        [Test]
        public void TestCaretRemapKeepsTheSameCharacterAhead()
        {
            const string pipe = "Did I di|sap|point you?";
            string rest = LineListPanel.RestTextOf(pipe);

            for (int p = 0; p < rest.Length; p++)
            {
                // Past any pipes the caret stopped in front of, the next character is the one the
                // rest caret was in front of.
                int i = LineListPanel.PipeCaretFor(pipe, p);

                while (pipe[i] == '|')
                    i++;

                Assert.That(pipe[i], Is.EqualTo(rest[p]), $"rest caret {p}");
            }
        }
    }
}
