// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// PROFILING HARNESS for the zoom-button lag report (2026-10-02), not a pin. Boots the real editor on
// the real Spectator map (183 s mp3, decoded waveform), then drives the zoom the way players do and
// records, per UPDATE frame, the wall time and the update thread's allocation, plus whole-gesture
// allocation, gen 2 collections and waveform resamples started. Headless hosts never draw, so the
// DRAW thread's CPU work is measured separately: the harness generates the real draw-node tree of the
// whole game and runs it through the host's dummy renderer, timing it, at held zoom levels and with
// individual parts hidden. A dummy renderer writes no vertices and uploads nothing, so those numbers
// are a LOWER bound on the draw thread and say nothing about the GPU; the waveform's quad count is
// reported beside them so the vertex volume can be bounded by arithmetic instead.
//
// Run: copy into typebeat.Game.Rulesets.TypeBeat.Tests/Visual/, build the test project, then
// dotnet test ... --filter "FullyQualifiedName~TestSceneZoomButtonProfile" with TYPEBEAT_MAPS_DIR set;
// ZOOM_PROFILE_OUT names the file the report is written to. Raw outputs beside this file:
// zoom-button-profile-before.txt (main at 3ed0982e) and zoom-button-profile-after.txt (this branch).
// Headless root is 1366x768, so the timeline is 1,031 px wide; a 1920 px player sees more visible
// quads after the fix (about one per pixel) and the same 183k before it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osuTK;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using typebeat.Game.Screens.Edit.Timing;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    [Explicit("profiling harness; run by name")]
    public partial class TestSceneZoomButtonProfile : EditorTestScene
    {
        private const string map_dir = "Friday Pilots Club - Spectator";
        private const string mp3 = "Friday Pilots Club - Spectator Official Audio.mp3";

        private static readonly StringBuilder report = new StringBuilder();

        [Resolved]
        private GameHost host { get; set; } = null!;

        private readonly FrameRecorder recorder = new FrameRecorder();

        private float defaultZoom;

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            Assert.That(TimingJsonLoader.TryLoad(StandaloneMaps.Require(map_dir, "timing.json"), out var lines), Is.True);

            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Friday Pilots Club";
            beatmap.BeatmapInfo.Metadata.Title = "Spectator";
            beatmap.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 60000 / 128.0 });

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return beatmap;
        }

        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
            => new RealWaveformWorkingBeatmap(beatmap, storyboard, Clock, Audio, StandaloneMaps.Require(map_dir, mp3));

        private Timeline timeline => Editor.ChildrenOfType<Timeline>().Single();
        private TimelineWaveformGraph graph => Editor.ChildrenOfType<TimelineWaveformGraph>().Single();
        private TimelineButton zoomIn => Editor.ChildrenOfType<TimelineButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.SearchPlus));

        [Test]
        public void TestProfile([Values(false, true)] bool pinned)
        {
            IntPtr originalAffinity = IntPtr.Zero;

            AddStep("attach recorder", () =>
            {
                if (recorder.Parent == null)
                    Add(recorder);
                recorder.Graph = graph;
            });
            AddUntilStep("waveform decoded and resampled", () => recorder.Graph?.Waveform?.GetPointsAsync().IsCompleted == true && pointsApplied(recorder.Graph) > 0);
            AddWaitStep("settle boot", 30);
            AddStep("note default zoom", () =>
            {
                defaultZoom = timeline.Zoom;
                log($"\n==== {(pinned ? "PINNED TO 2 CORES" : $"ALL {Environment.ProcessorCount} CORES")} ====");
                log($"root {host.Window?.ClientSize.ToString() ?? "headless"}, timeline width {timeline.DrawWidth:N0} px, track {EditorClock.TrackLength:N0} ms, "
                    + $"decoded points {recorder.Graph!.Waveform!.GetPoints().Length:N0}, default zoom {defaultZoom:N1}, "
                    + $"min/max zoom {minZoom():N1}/{maxZoom():N1}, step per notch {(maxZoom() - minZoom()) * 0.02f:N2}");
            });

            if (pinned)
            {
                AddStep("pin to 2 cores", () =>
                {
                    var process = Process.GetCurrentProcess();
                    originalAffinity = process.ProcessorAffinity;
                    process.ProcessorAffinity = (IntPtr)0b11;
                });
            }

            idle("idle at default zoom", 240);

            // The player's report: click the magnifier repeatedly from the default zoom to the deepest.
            gesture("BUTTON clicks, default -> max (one click per 250 ms)", 52, clickZoomIn);
            resetZoom();

            // The same distance on the wheel path (Alt+wheel over the strip).
            gesture("WHEEL notches, default -> max (one notch per 250 ms)", 52, wheelNotch);
            resetZoom();

            // Holding the button: RepeatingButtonBehaviour re-fires from 300 ms down to every 80 ms.
            AddStep("begin: BUTTON held for 4 s", () => beginGesture("BUTTON held 4 s from default"));
            AddStep("press and hold", () =>
            {
                InputManager.MoveMouseTo(zoomIn);
                InputManager.PressButton(MouseButton.Left);
            });
            waitGameTime(4000);
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));
            waitGameTime(400);
            AddStep("end", endGesture);
            resetZoom();

            // Held zoom levels: the per-frame cost once nothing is animating.
            foreach (float fraction in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                AddStep($"zoom to {fraction:P0} of default..max", () => timeline.Zoom = defaultZoom + (maxZoom() - defaultZoom) * fraction);
                waitGameTime(600);
                idle($"HELD at {fraction:P0} (zoom {{zoom}})", 240);
                AddStep("draw-thread breakdown", () => drawBreakdown($"{fraction:P0}"));
            }

            // The update thread's own share of the lyric strip and line list (372's side finding):
            // hidden drawables do not update, so the difference to the held run above is theirs.
            AddStep("hide lyric strip and line list", () =>
            {
                foreach (var d in Editor.ChildrenOfType<LyricTimeline>().Cast<Drawable>().Concat(Editor.ChildrenOfType<LineListPanel>()))
                    d.Alpha = 0;
            });
            idle("HELD at 100% WITHOUT lyric strip and line list (zoom {zoom})", 240);
            AddStep("show them again", () =>
            {
                foreach (var d in Editor.ChildrenOfType<LyricTimeline>().Cast<Drawable>().Concat(Editor.ChildrenOfType<LineListPanel>()))
                    d.Alpha = 1;
            });

            if (pinned)
                AddStep("unpin", () => Process.GetCurrentProcess().ProcessorAffinity = originalAffinity);

            AddStep("write report", () =>
            {
                string? path = Environment.GetEnvironmentVariable("ZOOM_PROFILE_OUT");
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, report.ToString());
                TestContext.Progress.WriteLine(report.ToString());
            });
        }

        #region Gestures

        private void clickZoomIn()
        {
            InputManager.MoveMouseTo(zoomIn);
            InputManager.Click(MouseButton.Left);
        }

        private void wheelNotch()
        {
            InputManager.MoveMouseTo(timeline);
            InputManager.PressKey(Key.AltLeft);
            InputManager.ScrollVerticalBy(1);
            InputManager.ReleaseKey(Key.AltLeft);
        }

        private void gesture(string name, int count, Action act)
        {
            AddStep($"begin: {name}", () => beginGesture(name));

            for (int i = 0; i < count; i++)
            {
                AddStep($"act {i}", () =>
                {
                    // The zoom TARGET before each action; input lands on the next frame, so the step
                    // an action took is the difference to the next reading.
                    recorder.ZoomSteps.Add(timeline.Zoom);
                    act();
                });
                waitGameTime(250);
            }

            waitGameTime(400);
            AddStep($"end: {name}", endGesture);
        }

        private void resetZoom()
        {
            AddStep("move mouse away", () => InputManager.MoveMouseTo(Editor.ScreenSpaceDrawQuad.Centre));
            AddStep("reset zoom", () => timeline.Zoom = defaultZoom);
            waitGameTime(800);
        }

        private void idle(string name, int frames)
        {
            AddStep($"begin: {name}", () => beginGesture(name.Replace("{zoom}", timeline.Zoom.ToString("N1"))));
            AddUntilStep("record frames", () => recorder.Frames.Count >= frames);
            AddStep($"end: {name}", endGesture);
        }

        private void waitGameTime(double ms)
        {
            double until = 0;
            AddStep($"wait {ms} ms", () => until = Time.Current + ms);
            AddUntilStep("waited", () => Time.Current >= until);
        }

        #endregion

        #region Recording

        private string gestureName = string.Empty;
        private long gestureAllocStart;
        private int gestureGen2Start;
        private int gestureGen0Start;

        private void beginGesture(string name)
        {
            gestureName = name;
            recorder.Begin();
            gestureAllocStart = GC.GetTotalAllocatedBytes(true);
            gestureGen2Start = GC.CollectionCount(2);
            gestureGen0Start = GC.CollectionCount(0);
        }

        private void endGesture()
        {
            recorder.Recording = false;

            long allocated = GC.GetTotalAllocatedBytes(true) - gestureAllocStart;
            var frames = recorder.Frames;
            var times = frames.Select(f => f.Ms).OrderBy(t => t).ToList();

            log($"-- {gestureName}");

            if (times.Count == 0)
            {
                log("   no frames");
                return;
            }

            double pct(double p) => times[Math.Min(times.Count - 1, (int)(p * times.Count))];

            log($"   frames {times.Count}, update ms: mean {times.Average():N2} p50 {pct(0.5):N2} p95 {pct(0.95):N2} p99 {pct(0.99):N2} max {times[^1]:N2}; "
                + $"over 16.7 ms {times.Count(t => t > 1000 / 60.0)}, over 4.17 ms {times.Count(t => t > 1000 / 240.0)}");
            log($"   alloc total {allocated / 1048576.0:N1} MiB (update thread {frames.Sum(f => f.Bytes) / 1048576.0:N1} MiB, largest frame {frames.Max(f => f.Bytes) / 1048576.0:N2} MiB); "
                + $"gen0 {GC.CollectionCount(0) - gestureGen0Start}, gen2 {GC.CollectionCount(2) - gestureGen2Start}; "
                + $"resamples started {recorder.ResamplesStarted}, applied {recorder.ResamplesApplied}, points now {pointsApplied(recorder.Graph!):N0}");

            if (recorder.ZoomSteps.Count > 0)
            {
                var readings = recorder.ZoomSteps.Append(timeline.Zoom).ToList();
                var steps = readings.Zip(readings.Skip(1), (a, b) => b - a).Where(s => s > 0.001f).ToList();
                log($"   actions {recorder.ZoomSteps.Count}, that moved the zoom {steps.Count}, zoom step per action min {(steps.Count > 0 ? steps.Min() : 0):N2} max {(steps.Count > 0 ? steps.Max() : 0):N2}, final zoom {timeline.Zoom:N1}");
            }
        }

        private void log(string line) => report.AppendLine(line);

        private float minZoom() => (float)typeof(ZoomableScrollContainer).GetField("minZoom", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(timeline)!;
        private float maxZoom() => (float)typeof(ZoomableScrollContainer).GetField("maxZoom", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(timeline)!;

        private static readonly FieldInfo resampled_points = typeof(WaveformGraph).GetField("resampledPoints", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo cancel_source = typeof(WaveformGraph).GetField("cancelSource", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static int pointsApplied(WaveformGraph graph) => (resampled_points.GetValue(graph) as Waveform.Point[])?.Length ?? 0;

        private partial class FrameRecorder : Component
        {
            public bool Recording;
            public WaveformGraph? Graph;
            public readonly List<(double Ms, long Bytes)> Frames = new List<(double, long)>();
            public readonly List<float> ZoomSteps = new List<float>();
            public int ResamplesStarted;
            public int ResamplesApplied;

            private long lastTimestamp;
            private long lastAllocated;
            private object? lastCancelSource;
            private object? lastPoints;

            public FrameRecorder()
            {
                AlwaysPresent = true;
            }

            public void Begin()
            {
                Frames.Clear();
                ZoomSteps.Clear();
                ResamplesStarted = ResamplesApplied = 0;
                lastTimestamp = 0;
                Recording = true;
            }

            protected override void Update()
            {
                base.Update();

                long now = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();

                if (Recording && lastTimestamp != 0)
                    Frames.Add(((now - lastTimestamp) * 1000.0 / Stopwatch.Frequency, allocated - lastAllocated));

                lastTimestamp = now;
                lastAllocated = allocated;

                if (Graph == null)
                    return;

                object? cancel = cancel_source.GetValue(Graph);
                object? points = resampled_points.GetValue(Graph);

                if (Recording && lastCancelSource != null && !ReferenceEquals(cancel, lastCancelSource))
                    ResamplesStarted++;
                if (Recording && lastPoints != null && !ReferenceEquals(points, lastPoints))
                    ResamplesApplied++;

                lastCancelSource = cancel;
                lastPoints = points;
            }
        }

        #endregion

        #region Draw thread (dummy renderer)

        private static readonly MethodInfo generate_subtree = typeof(Drawable).GetMethod("GenerateDrawNodeSubtree", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo draw_other = typeof(DrawNode).GetMethod("DrawOther", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo begin_frame = typeof(Renderer).GetMethod("BeginFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo finish_frame = typeof(Renderer).GetMethod("FinishFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private ulong drawFrame = 1UL << 40;

        /// <summary>
        /// Median CPU time of drawing the whole game's draw-node tree through the dummy renderer, after
        /// regenerating it (so ApplyState costs are not in the number).
        /// </summary>
        private double timeDraw(int reps = 31)
        {
            Drawable root = this;
            while (root.Parent != null)
                root = root.Parent;

            var renderer = (Renderer)host.Renderer;
            var samples = new List<double>();

            for (int i = 0; i < reps; i++)
            {
                var node = (DrawNode)generate_subtree.Invoke(root, new object[] { drawFrame++, 0, false })!;

                long start = Stopwatch.GetTimestamp();
                begin_frame.Invoke(renderer, new object[] { root.DrawSize });
                draw_other.Invoke(null, new object[] { node, renderer });
                finish_frame.Invoke(renderer, null);
                samples.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            }

            samples.Sort();
            return samples[samples.Count / 2];
        }

        /// <summary>
        /// How many quads the framework's waveform draw node walks per frame: it culls to the innermost
        /// MASKING container's screen-space bounds, not to what is visible.
        /// </summary>
        private (int quads, int points, float maskWidth, float visibleWidth) waveformQuads()
        {
            var g = graph;
            int points = pointsApplied(g);

            CompositeDrawable? masking = g.Parent;
            while (masking != null && !masking.Masking)
                masking = masking.Parent;

            var mask = masking!.ScreenSpaceDrawQuad.AABBFloat;
            var graphQuad = g.ScreenSpaceDrawQuad.AABBFloat;

            float spacing = graphQuad.Width / Math.Max(1, points - 1);
            int left = (int)Math.Clamp((mask.Left - graphQuad.Left) / spacing, 0, points - 1);
            int right = (int)Math.Clamp((mask.Right - graphQuad.Left) / spacing + 1, 0, points - 1);

            return (right - left, points, mask.Width, timeline.ScreenSpaceDrawQuad.Width);
        }

        private void drawBreakdown(string label)
        {
            var (quads, points, maskWidth, visibleWidth) = waveformQuads();
            log($"   draw @ {label}: waveform walks {quads:N0} quads of {points:N0} points (masking bounds {maskWidth:N0} px wide, visible {visibleWidth:N0} px); "
                + $"~{quads * 4 * 60 / 1048576.0:N1} MiB of vertices per frame on a real renderer");

            double all = timeDraw();
            log($"   draw CPU (dummy renderer, median of 31): whole game {all:N2} ms");

            void without(string name, IEnumerable<Drawable> parts)
            {
                var list = parts.ToList();
                var saved = list.Select(p => p.Alpha).ToList();

                foreach (var p in list)
                    p.Alpha = 0;

                // Presence only changes the next generated tree, which timeDraw regenerates itself.
                double t = timeDraw();

                for (int i = 0; i < list.Count; i++)
                    list[i].Alpha = saved[i];

                log($"     without {name} ({list.Count}): {t:N2} ms (share {all - t:N2} ms)");
            }

            without("waveform", new[] { graph });
            without("timing grids", Editor.ChildrenOfType<TimingGrid>());
            without("lyric strip", Editor.ChildrenOfType<LyricTimeline>());
            without("line list", Editor.ChildrenOfType<LineListPanel>());
            without("zoom buttons", Editor.ChildrenOfType<TimelineButton>());
        }

        #endregion

        private class RealWaveformWorkingBeatmap : ClockBackedTestWorkingBeatmap
        {
            private readonly string path;

            public RealWaveformWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard, IFrameBasedClock clock, AudioManager audio, string path)
                : base(beatmap, storyboard, clock, audio)
            {
                this.path = path;
            }

            protected override Waveform GetWaveform() => new Waveform(File.OpenRead(path));
        }
    }
}
