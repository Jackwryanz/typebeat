// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The editor's per-frame line order (backlog 377): the lyric strip and the line list asked for
    /// a freshly sorted list every update frame, which allocated on every frame of an idle editor.
    /// The cache sorts only when the hit objects changed, and it must see every change that moves
    /// the order, including a line replaced without the beatmap's change event.
    /// </summary>
    [TestFixture]
    public class OrderedLinesCacheTest
    {
        [Test]
        public void AnUnchangedBeatmapReturnsTheSameOrderWithoutAllocating()
        {
            var editorBeatmap = createBeatmap();
            var cache = new OrderedLinesCache();

            var first = cache.Get(editorBeatmap);
            int version = cache.Version;

            // Warm the path (JIT, tuple comparers) before counting.
            for (int i = 0; i < 10; i++)
                cache.Get(editorBeatmap);

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
                cache.Get(editorBeatmap);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Multiple(() =>
            {
                Assert.That(cache.Get(editorBeatmap), Is.SameAs(first));
                Assert.That(cache.Version, Is.EqualTo(version));
                Assert.That(allocated, Is.Zero, "an unchanged frame allocates nothing");
                Assert.That(first, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(editorBeatmap)));
            });
        }

        [Test]
        public void ALineReplacedWithoutTheChangeEventIsReordered()
        {
            var editorBeatmap = createBeatmap();
            var cache = new OrderedLinesCache();
            var firstLine = cache.Get(editorBeatmap)[0];

            // Assigned directly, no editorBeatmap.Update: the order must still follow it.
            firstLine.Line = lineAt("alpha", 9000, 10000);

            var ordered = cache.Get(editorBeatmap);

            Assert.Multiple(() =>
            {
                Assert.That(ordered[^1], Is.SameAs(firstLine));
                Assert.That(ordered, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(editorBeatmap)));
            });
        }

        [Test]
        public void ALineIndexChangeReordersATie()
        {
            var editorBeatmap = createBeatmap();
            var cache = new OrderedLinesCache();
            var lines = cache.Get(editorBeatmap).ToArray();

            // Two lines at the same start are ordered by index, so swapping the indices swaps them.
            lines[1].Line = lineAt("beta", lines[0].Line.StartTime, lines[0].Line.EndTime);
            Assert.That(cache.Get(editorBeatmap).Take(2), Is.EqualTo(new[] { lines[0], lines[1] }));

            (lines[0].LineIndex, lines[1].LineIndex) = (lines[1].LineIndex, lines[0].LineIndex);

            Assert.That(cache.Get(editorBeatmap).Take(2), Is.EqualTo(new[] { lines[1], lines[0] }));
        }

        [Test]
        public void AddedAndRemovedLinesAreSeen()
        {
            var editorBeatmap = createBeatmap();
            var cache = new OrderedLinesCache();
            var initial = cache.Get(editorBeatmap).ToArray();

            var added = new TypeBeatHitObject { StartTime = 500, LineIndex = 3, Line = lineAt("early", 500, 900), Granularity = TimingGranularity.Word };
            editorBeatmap.Add(added);

            Assert.That(cache.Get(editorBeatmap)[0], Is.SameAs(added));

            editorBeatmap.Remove(initial[1]);

            Assert.That(cache.Get(editorBeatmap), Is.EqualTo(new[] { added, initial[0], initial[2] }));
        }

        private static EditorBeatmap createBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;

            // Stored out of order, so the cached list is a real sort and not the storage order.
            add(beatmap, 2, "gamma", 5000, 7000);
            add(beatmap, 0, "alpha", 1000, 3000);
            add(beatmap, 1, "beta", 3000, 5000);

            return new EditorBeatmap(beatmap);
        }

        private static void add(Beatmap beatmap, int index, string text, double start, double end)
            => beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = start, LineIndex = index, Line = lineAt(text, start, end), Granularity = TimingGranularity.Word });

        private static LyricLine lineAt(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };
    }
}
