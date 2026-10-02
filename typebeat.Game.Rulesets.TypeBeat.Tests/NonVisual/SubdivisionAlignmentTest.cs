// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SubdivisionAlignmentTest
    {
        // The saved Overdone line at 3:03.508 has boundary times but no split_chars.
        // The editor derives o|ver|doooooooooone from this exact spelling.
        internal static LyricLine OverdoneLine() => new LyricLine
        {
            RawText = "It's overdoooooooooone",
            StartTime = 183508.333333, EndTime = 191050, SingEndTime = 190633.333333,
            Units = new[]
            {
                new TimedUnit { Text = "It's", StartTime = 183508.333333, EndTime = 183883.333333 },
                new TimedUnit
                {
                    Text = "overdoooooooooone", StartTime = 184008.333333, EndTime = 190633.333333,
                    SyllableBoundaries = new[] { 185008.333333, 186008.333333 },
                },
            },
        };

        private static TypingEngine engine(LyricLine line, bool aligned, bool literate = false)
            => new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Overdone", FolderPath = "", AudioFileName = "" },
                Granularity = TimingGranularity.Syllable, Lines = new[] { line },
            }, literate) { AlignSubdivisionTargets = aligned };

        [TestCase(false)]
        [TestCase(true)]
        public void OverdoneCaretAndCharacterTargetsFollowEditorCuts(bool literate)
        {
            var source = OverdoneLine();
            var line = engine(source, true, literate).Lines[0];
            var word = source.Units[1];
            int first = line.Cells.Count - word.Text.Length;
            Assert.That(SyllableSegments.SegmentTexts(word.Text, SyllableSegments.SplitsFor(word)),
                Is.EqualTo(new[] { "o", "ver", "doooooooooone" }));
            Assert.That(line.Cells[first + 1].TargetTime, Is.EqualTo(185008.333333).Within(0.000001));
            Assert.That(line.Cells[first + 4].TargetTime, Is.EqualTo(186008.333333).Within(0.000001));
            Assert.That(line.SungPositionAt(185008.333333), Is.EqualTo(first + 1).Within(0.000001));
            Assert.That(line.SungPositionAt(186008.333333), Is.EqualTo(first + 4).Within(0.000001));
            Assert.That(line.Cells[first + 5].TargetTime,
                Is.EqualTo(186008.333333 + 4625.0 / 13).Within(0.000001), "held o's begin in the final subdivision");
            Assert.That(line.IsCharTimedStretch(first + 5), Is.True);
            Assert.That(line.Cells.Skip(first).Select(c => c.TargetTime),
                Is.EqualTo(TypingLine.CellTargetsFor(word, word.Text.Length)).Within(0.000001));
            assertTargetsInsideGroups(line);
        }

        [Test]
        public void DerivedSubdivisionsInsidePausedWordsAlsoAlign()
        {
            var source = new LyricLine
            {
                RawText = "probably", StartTime = 0, EndTime = 10000, SingEndTime = 9000,
                Units = new[]
                {
                    new TimedUnit
                    {
                        Text = "probably", StartTime = 1000, EndTime = 9000,
                        SyllableBoundaries = new[] { 2000.0, 6000.0 },
                        Pauses = new[] { new WordPause { StartTime = 3500, EndTime = 4500, SplitChar = 3 } },
                    },
                },
            };
            var line = engine(source, true).Lines[0];
            assertTargetsInsideGroups(line);
            Assert.That(line.Cells.Select(c => c.TargetTime),
                Is.EqualTo(TypingLine.CellTargetsFor(source.Units[0], 8)).Within(0.000001));
            Assert.That(line.Cells.Select(c => c.TargetTime), Has.None.InRange(3500.000001, 4499.999999));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AuthoredSplitsKeepTheirOriginalTargets(bool literate)
        {
            var source = new LyricLine
            {
                RawText = "probably", StartTime = 0, EndTime = 10000, SingEndTime = 9000,
                Units = new[]
                {
                    new TimedUnit
                    {
                        Text = "probably", StartTime = 1000, EndTime = 9000,
                        SyllableBoundaries = new[] { 2000.0, 6000.0 }, SyllableSplits = new[] { 3, 5 },
                    },
                },
            };
            var aligned = engine(source, true, literate).Lines[0];
            Assert.That(aligned.Cells.Select(c => c.TargetTime),
                Is.EqualTo(engine(source, false, literate).Lines[0].Cells.Select(c => c.TargetTime)));
            assertTargetsInsideGroups(aligned);
        }

        [Test]
        public void ShortWordsUseTheFinalGroupForRemainingTimeBoundaries()
        {
            var source = new LyricLine
            {
                RawText = "a", StartTime = 0, EndTime = 10000, SingEndTime = 9000,
                Units = new[]
                {
                    new TimedUnit { Text = "a", StartTime = 1000, EndTime = 9000, SyllableBoundaries = new[] { 2000.0, 6000.0 } },
                },
            };
            var line = engine(source, true).Lines[0];
            Assert.That(line.Syllables.Count, Is.EqualTo(1));
            Assert.That(line.Syllables[0].EndTime, Is.EqualTo(9000));
            assertTargetsInsideGroups(line);
        }

        [Test]
        public void LegacyAndExtendedHeadersSelectTheirOwnTargetsWithoutReplacingCells()
        {
            var e = engine(OverdoneLine(), false);
            var line = e.Lines[0];
            var cell = line.Cells[5];
            double oldTarget = cell.TargetTime;
            var marker = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, alignSubdivisionTargets: true);
            var decoded = new TypeBeatReplayFrame();
            var legacy = marker.ToLegacy(null!);
            Assert.That(legacy.MouseY, Is.EqualTo(8), "alignment is bit 3 of the extended word");
            decoded.FromLegacy(legacy, null!);
            Assert.That(decoded.IsConfigExtended && decoded.AlignSubdivisionTargets, Is.True, "timing era survives the .osr carrier");
            ReplayEngineFeed.Apply(e, decoded);
            Assert.That(cell.TargetTime, Is.EqualTo(185008.333333).Within(0.000001));
            Assert.That(line.Cells[5], Is.SameAs(cell));
            ReplayEngineFeed.Apply(e, TypeBeatReplayFrame.CreateConfigFrame(0, true));
            Assert.That(cell.TargetTime, Is.EqualTo(oldTarget));
            Assert.That(e.AlignSubdivisionTargets, Is.False);
            ReplayEngineFeed.Apply(e, decoded);
            Assert.That(cell.TargetTime, Is.EqualTo(185008.333333).Within(0.000001));
            Assert.That(line.SungPositionAt(185008.333333), Is.EqualTo(5).Within(0.000001));
            ReplayEngineFeed.Apply(e, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true));
            Assert.That(e.AlignSubdivisionTargets, Is.False, "an older extended header preserves its own target era");
            Assert.That(cell.TargetTime, Is.EqualTo(oldTarget));
        }

        [TestCase(185100.0, 5)]
        [TestCase(186100.0, 8)]
        [TestCase(189000.0, 16)]
        public void RushCapPlayheadFollowsTheTargetEra(double time, int alignedCount)
        {
            // Built legacy and flipped, the path a replay header takes: the playhead the rush cap
            // measures against must track the targets the cells actually carry after the swap.
            var e = engine(OverdoneLine(), false);
            int legacyCount = countableAtOrBefore(e, time);
            Assert.That(e.PlayheadCountablePosition(time), Is.EqualTo(legacyCount), "legacy era");

            e.AlignSubdivisionTargets = true;
            Assert.That(countableAtOrBefore(e, time), Is.EqualTo(alignedCount), "aligned targets cross-check");
            Assert.That(e.PlayheadCountablePosition(time), Is.EqualTo(alignedCount), "aligned era");
            Assert.That(legacyCount, Is.Not.EqualTo(alignedCount), "the fixture separates the two eras");

            e.AlignSubdivisionTargets = false;
            Assert.That(e.PlayheadCountablePosition(time), Is.EqualTo(legacyCount), "back to legacy");

            Assert.That(engine(OverdoneLine(), true).PlayheadCountablePosition(time), Is.EqualTo(alignedCount), "built aligned");
        }

        [Test]
        public void TheTargetEraRaisesItsChangeEventOnARealChangeOnly()
        {
            // The lyric stage re-lays its pace bands on this event, the same shape as the grouping
            // switch (AuthoredSyllablesEraTest), because the bands are laid from the cells' targets.
            var e = engine(OverdoneLine(), true);
            int flips = 0;
            e.AlignSubdivisionTargetsChanged += () => flips++;
            var alignedBands = UnderlinePace.BuildRelativeBands(e.Lines);

            ReplayEngineFeed.Apply(e, TypeBeatReplayFrame.CreateConfigFrame(0, true));
            Assert.That(e.AlignSubdivisionTargets, Is.False, "a CONFIG frame alone is a replay recorded before the era");
            Assert.That(UnderlinePace.BuildRelativeBands(e.Lines), Is.Not.EqualTo(alignedBands), "the bands follow the target era");

            ReplayEngineFeed.Apply(e, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, alignSubdivisionTargets: true));
            Assert.That(e.AlignSubdivisionTargets, Is.True);
            Assert.That(UnderlinePace.BuildRelativeBands(e.Lines), Is.EqualTo(alignedBands));

            ReplayEngineFeed.Apply(e, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, alignSubdivisionTargets: true));
            Assert.That(flips, Is.EqualTo(2), "the change event fires on a real change only");

            ReplayEngineFeed.ClearExtendedEras(e);
            Assert.That(e.AlignSubdivisionTargets, Is.False);
            Assert.That(flips, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ThePublicFactoryBuildsTheLineTheLiveEngineHolds(bool literate)
        {
            var source = OverdoneLine();
            var live = engine(source, true, literate).Lines[0];
            var legacy = engine(source, false, literate).Lines[0];

            foreach (var built in new[]
                     {
                         TypingLine.FromLyricLine(source, literate, alignSubdivisionTargets: true),
                         TypingLine.ForMods(source, literate, false, null, alignSubdivisionTargets: true),
                     })
            {
                Assert.That(built.Cells.Select(c => c.TargetTime), Is.EqualTo(live.Cells.Select(c => c.TargetTime)));
                Assert.That(built.SealGraceMs, Is.EqualTo(live.SealGraceMs));
            }

            var plain = TypingLine.FromLyricLine(source, literate);
            Assert.That(plain.Cells.Select(c => c.TargetTime), Is.EqualTo(legacy.Cells.Select(c => c.TargetTime)), "the default is still the legacy era");
            Assert.That(plain.SealGraceMs, Is.EqualTo(legacy.SealGraceMs));
            Assert.That(plain.Cells.Select(c => c.TargetTime), Is.Not.EqualTo(live.Cells.Select(c => c.TargetTime)), "the fixture separates the two eras");
        }

        private static int countableAtOrBefore(TypingEngine e, double time)
            => e.Lines.SelectMany(l => l.Cells).Count(c => c.IsCountable && c.TargetTime <= time);

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void OverdoneLiveJudgementsSurviveReplayScoringAndBackwardsSeek(bool hardRock, bool aligned)
        {
            var source = OverdoneLine();
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = source.StartTime, LineIndex = 0, Granularity = TimingGranularity.Syllable, Line = source,
            });
            beatmap.HitObjects[0].ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);
            Mod[] mods = hardRock ? new Mod[] { new TypeBeatModHardRock() } : Array.Empty<Mod>();
            using var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), beatmap, mods);
            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);

            var e = drawable.Engine;
            Assert.That(e.AlignSubdivisionTargets, Is.True, "live play defaults to the editor cuts");
            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, syllableTiming: !hardRock,
                charTimedStretch: true, firstCharTiming: true, unhalvedHardRockWindows: true, firstLineLeadIn: true));
            if (aligned)
                replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, alignSubdivisionTargets: true));
            foreach (TypeBeatReplayFrame header in replay.Frames)
                ReplayEngineFeed.Apply(e, header);

            var seen = new List<JudgementType>();
            e.CharJudged += j => seen.Add(j.Type);
            foreach (var cell in e.Lines[0].Cells.Where(c => c.IsTypeable))
            {
                var frame = new TypeBeatReplayFrame(Math.Round(cell.TargetTime), char.ToLowerInvariant(cell.Expected));
                var decoded = new TypeBeatReplayFrame { Time = frame.Time };
                decoded.FromLegacy(frame.ToLegacy(beatmap), beatmap);
                replay.Frames.Add(decoded);
                e.Update(decoded.Time);
                Assert.That(e.ProcessKey(decoded.Character, decoded.Time), Is.True, $"{decoded.Character} at {decoded.Time}");
            }

            var account = TypeBeatReplayScorer.Score(beatmap, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            int count = e.Lines[0].Cells.Count(c => c.IsTypeable);
            Assert.That(seen.Count, Is.EqualTo(count));
            if (aligned || hardRock)
                Assert.That(seen, Is.EqualTo(Enumerable.Repeat(JudgementType.Great, count)));
            else
                Assert.That(seen, Is.Not.EqualTo(Enumerable.Repeat(JudgementType.Great, count)), "legacy syllable onsets retain their original mismatch");
            foreach (var type in new[] { JudgementType.Great, JudgementType.Ok, JudgementType.Meh })
            {
                var result = TypeBeatResultMapping.CellResult(type, TypoRule.Deferred)!.Value;
                Assert.That(account.Statistics.GetValueOrDefault(result), Is.EqualTo(seen.Count(t => TypeBeatResultMapping.CellResult(t, TypoRule.Deferred) == result)));
            }
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
            Assert.That(account.UnconsumedFrames, Is.Zero);

            double target = e.Lines[0].Cells[5].TargetTime;
            ReplayEngineFeed.RebuildTo(e, replay.Frames, 186008.333333);
            Assert.That(e.AlignSubdivisionTargets, Is.EqualTo(aligned));
            Assert.That(e.Lines[0].Cells[5].TargetTime, Is.EqualTo(target));
            if (aligned)
                Assert.That(e.Lines[0].SungPositionAt(186008.333333), Is.EqualTo(8).Within(0.000001));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OverdoneAutoplayCarriesAlignedTargetsAcrossReplayAttach(bool hardRock)
        {
            var source = OverdoneLine();
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = source.StartTime, LineIndex = 0, Granularity = TimingGranularity.Syllable, Line = source,
            });
            beatmap.HitObjects[0].ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);
            Mod[] mods = hardRock ? new Mod[] { new TypeBeatModHardRock() } : Array.Empty<Mod>();
            using var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), beatmap, mods);
            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);
            var e = drawable.Engine;
            var frames = new TypeBeatModAutoplay().CreateReplayData(beatmap, mods).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(frames[0].IsConfigExtended && frames[0].AlignSubdivisionTargets, Is.True);
            Assert.That(frames[^2].IsConfigExtended && frames[^2].AlignSubdivisionTargets, Is.True,
                "the editor's trimmed autoplay still carries the timing era");
            ReplayEngineFeed.ClearExtendedEras(e);
            Assert.That(e.AlignSubdivisionTargets, Is.False);
            var seen = new List<JudgementType>();
            e.CharJudged += j => seen.Add(j.Type);
            foreach (var frame in frames)
                ReplayEngineFeed.Apply(e, frame);
            Assert.That(e.AlignSubdivisionTargets, Is.True);
            Assert.That(e.AuthoredSyllablesOnly, Is.True);
            Assert.That(seen, Is.EqualTo(Enumerable.Repeat(JudgementType.Great, e.Lines[0].Cells.Count)));
            Assert.That(e.Lines[0].Cells[5].TargetTime, Is.EqualTo(185008.333333).Within(0.000001));
        }

        [Test]
        public void PuppeteerTransformCarriesSubdivisionAlignment()
        {
            var source = OverdoneLine();
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = source.StartTime, LineIndex = 0, Granularity = TimingGranularity.Syllable, Line = source,
            });
            var replay = new Replay();
            double anchor = Math.Ceiling(source.StartTime);
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(anchor, true, wallClockFrames: true));
            replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(anchor, authoredSyllablesOnly: true, alignSubdivisionTargets: true));
            replay.Frames.Add(new TypeBeatReplayFrame(anchor + 10, 'i'));
            var derived = PuppeteerReplayTransform.Derive(beatmap, new Mod[] { new TypeBeatModPuppeteer() }, replay);
            var header = derived.Single(f => f.IsConfigExtended);
            Assert.That(header.AlignSubdivisionTargets, Is.True);
            Assert.That(header.AuthoredSyllablesOnly, Is.True);
            var e = engine(source, false);
            foreach (var frame in derived.Take(2))
                ReplayEngineFeed.Apply(e, frame);
            Assert.That(e.Lines[0].Cells[5].TargetTime, Is.EqualTo(185008.333333).Within(0.000001));
        }

        private static void assertTargetsInsideGroups(TypingLine line)
        {
            foreach (var group in line.Syllables)
            {
                Assert.That(line.Cells[group.StartCell].TargetTime, Is.EqualTo(group.StartTime).Within(0.000001));
                for (int i = group.StartCell; i < group.EndCellExclusive; i++)
                    Assert.That(line.Cells[i].TargetTime, Is.InRange(group.StartTime, group.EndTime), $"cell {i}");
                Assert.That(line.SungPositionAt(group.StartTime), Is.EqualTo(group.StartCell).Within(0.000001));
            }
        }
    }
}
