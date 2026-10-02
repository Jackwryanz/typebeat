// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor's waveform only DRAWS what is on screen, at every zoom, while the map's gain is
    /// still clipped to the strip (zoom-button lag, 2026-10-02).
    /// </summary>
    /// <remarks>
    /// The framework's waveform draw node emits one quad for every point inside the innermost masking
    /// container's bounds. A masking container around the graph spans the whole zoomed content, so the
    /// graph drew the entire song every frame: the whole 183k points of a 3 minute song from half
    /// zoom inwards, around 45 ms of draw thread CPU a frame before any vertex reached the GPU. These
    /// pins read the same arithmetic the draw node uses, so a masking container put back around the
    /// graph turns them red without needing a renderer.
    /// </remarks>
    public partial class TestSceneTimelineWaveformCulling : EditorTestScene
    {
        private const int song_seconds = 60;

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;

            var line = new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 3000,
                Units = new[] { new TimedUnit { Text = "hello world", StartTime = 1000, EndTime = 3000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });
            return beatmap;
        }

        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
            => new ToneWorkingBeatmap(beatmap, storyboard, Clock, Audio);

        private Timeline timeline => Editor.ChildrenOfType<Timeline>().Single();
        private TimelineWaveformGraph graph => Editor.ChildrenOfType<TimelineWaveformGraph>().Single();

        [Test]
        public void TestOnlyTheVisiblePointsAreDrawn()
        {
            AddUntilStep("waveform resampled", () => pointsApplied() > 0);

            AddAssert("default zoom draws the visible width only", visibleOnly);

            AddStep("zoom to maximum", () => timeline.Zoom = float.MaxValue);
            AddUntilStep("resampled for the maximum zoom", () => pointsApplied() == graph.Waveform!.GetPoints().Length);
            AddAssert("maximum zoom draws the visible width only", visibleOnly);
        }

        [Test]
        public void TestTheGainIsStillClippedToTheStrip()
        {
            AddUntilStep("waveform resampled", () => pointsApplied() > 0);
            AddStep("boost the gain", () => EditorBeatmap.BeatmapInfo.Metadata.AudioGain = 3);
            AddUntilStep("graph scaled past the strip", () => graph.Scale.Y == 3);
            AddAssert("its clip is exactly the strip's height", () =>
            {
                var clip = maskingAncestor().ScreenSpaceDrawQuad.AABBFloat;
                var strip = graph.Parent!.ScreenSpaceDrawQuad.AABBFloat;

                return Math.Abs(clip.Top - strip.Top) < 0.5f && Math.Abs(clip.Bottom - strip.Bottom) < 0.5f;
            });
        }

        /// <summary>
        /// The framework's own culling arithmetic (WaveformGraph.WaveformDrawNode.Draw): the points are
        /// spaced evenly over the graph's width and walked from the left to the right edge of the
        /// innermost masking bounds. True when that walk is no longer than the timeline is wide.
        /// </summary>
        private bool visibleOnly()
        {
            int points = pointsApplied();
            var mask = maskingAncestor().ScreenSpaceDrawQuad.AABBFloat;
            var graphQuad = graph.ScreenSpaceDrawQuad.AABBFloat;

            float spacing = graphQuad.Width / Math.Max(1, points - 1);
            int left = (int)Math.Clamp((mask.Left - graphQuad.Left) / spacing, 0, points - 1);
            int right = (int)Math.Clamp((mask.Right - graphQuad.Left) / spacing + 1, 0, points - 1);

            int visible = (int)Math.Ceiling(timeline.ScreenSpaceDrawQuad.Width / spacing) + 2;

            return right - left <= visible && right - left < points - 1;
        }

        private CompositeDrawable maskingAncestor()
        {
            CompositeDrawable? parent = graph.Parent;

            while (parent != null && !parent.Masking)
                parent = parent.Parent;

            return parent ?? throw new InvalidOperationException("the waveform has no masking ancestor at all");
        }

        private static readonly FieldInfo resampled_points = typeof(WaveformGraph).GetField("resampledPoints", BindingFlags.NonPublic | BindingFlags.Instance)
                                                             ?? throw new InvalidOperationException("the framework's waveform graph no longer has the field this test reads its points from");

        private int pointsApplied() => (resampled_points.GetValue(graph) as Waveform.Point[])?.Length ?? 0;

        private class ToneWorkingBeatmap : ClockBackedTestWorkingBeatmap
        {
            public ToneWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard, IFrameBasedClock clock, AudioManager audio)
                : base(beatmap, storyboard, clock, audio)
            {
            }

            protected override Waveform GetWaveform() => new Waveform(new MemoryStream(tone(song_seconds)));
        }

        /// <summary>A mono 440 Hz tone as an in-memory 16 bit wav, at a low rate to keep it small.</summary>
        private static byte[] tone(int seconds)
        {
            const int rate = 8000;
            int frames = rate * seconds;
            var pcm = new byte[frames * 2];

            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5 * short.MaxValue);
                pcm[i * 2] = (byte)(value & 0xff);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }

            var wav = new MemoryStream();
            var writer = new BinaryWriter(wav);

            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data".ToCharArray());
            writer.Write(pcm.Length);
            writer.Write(pcm);
            writer.Flush();

            return wav.ToArray();
        }
    }
}
