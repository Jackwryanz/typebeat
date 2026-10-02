// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Audio;
using osuTK;

namespace typebeat.Game.Screens.Edit.Compose.Components.Timeline
{
    /// <summary>
    /// The timeline's waveform: the framework's graph, resampled only once its size has SETTLED, and
    /// never at more points than the decoded song has.
    /// </summary>
    /// <remarks>
    /// <para>The graph spans the whole ZOOMED content, so its width is the timeline's width times the
    /// zoom, and the framework resamples the entire song for that width on every frame the width moves.
    /// A zoom is a 200 ms eased tween, so one wheel notch queued around fourteen full-width resamples on
    /// the thread pool (each one runs to completion even when the next frame has already superseded it),
    /// and every one that landed was copied whole into the draw node on the update thread. Measured on a
    /// 3 minute song, one tween to maximum zoom allocated 125 to 225 MiB above idle and a twelve-notch
    /// wheel gesture 270 to 630 MiB, with up to eleven gen 2 collections, depending on the timeline's width.</para>
    ///
    /// <para>Holding the regeneration until the size stops moving costs nothing visible: the draw node
    /// spaces whatever points it has across the CURRENT width, so the previous resample simply stretches
    /// with the tween and is replaced once, at the size the mapper settled on.</para>
    ///
    /// <para>The point cap matters at deep zoom: the framework analyses one point per millisecond of audio,
    /// and at the narrowest window (500 ms across the timeline) the width asks for several times that, so
    /// the resample was manufacturing duplicate points that carry nothing and still had to be allocated,
    /// copied and walked. Once capped, a further zoom that stays past the cap resamples nothing at all.</para>
    /// </remarks>
    public partial class TimelineWaveformGraph : WaveformGraph
    {
        /// <summary>How long the graph's size has to hold still before it is resampled for that size.</summary>
        public const double SETTLE_TIME = 50;

        private Vector2 lastSeenSize;
        private double sizeChangedAt;

        private int requestedCount = -1;
        private Waveform? requestedSource;

        private Waveform? countedSource;
        private Task<Waveform.Point[]>? sourcePoints;

        protected override void Update()
        {
            if (DrawSize != lastSeenSize)
            {
                lastSeenSize = DrawSize;
                sizeChangedAt = Time.Current;
                return;
            }

            if (Time.Current - sizeChangedAt < SETTLE_TIME)
                return;

            int available = sourcePointCount();
            double width = Math.Ceiling(DrawWidth * Scale.X);

            // Past the cap the graph already holds every point the song has, so a zoom that stays past it
            // has nothing to add: the draw node spreads those same points over the new width. Resampling
            // here would only rebuild an identical point set (and the resolution it would take to keep the
            // count at the cap is itself a forced regeneration), so the stale resolution is left alone
            // until a zoom comes back under the cap or the waveform changes.
            if (available > 0 && width > available && requestedCount == available && ReferenceEquals(requestedSource, Waveform))
                return;

            capResolution(available, width);

            // The framework regenerates for ANY new size, even one that asks for the point count it already
            // has (its equal-count early-out only applies after a cancelled generation). The draw node
            // spreads the points over the current width either way, so an unchanged count is skipped here.
            int count = (int)Math.Max(0, width * Resolution);

            if (count == requestedCount && ReferenceEquals(requestedSource, Waveform))
                return;

            requestedCount = count;
            requestedSource = Waveform;

            // The framework's own check: regenerate if the size differs from the one last generated for.
            base.Update();
        }

        /// <summary>
        /// The decoded song's point count, or 0 while it is still decoding (or there is no waveform). Read
        /// without waiting: until the song is decoded the cap is simply not applied yet.
        /// </summary>
        private int sourcePointCount()
        {
            if (!ReferenceEquals(countedSource, Waveform))
            {
                countedSource = Waveform;
                sourcePoints = Waveform?.GetPointsAsync();
            }

            return sourcePoints?.IsCompletedSuccessfully == true ? sourcePoints.GetResultSafely().Length : 0;
        }

        /// <summary>
        /// Lowers <see cref="WaveformGraph.Resolution"/> below one point per unit of width when the width
        /// exceeds the decoded song's point count, so the resample is never an upsample.
        /// </summary>
        /// <remarks>
        /// Only written when the point count it yields would change, because the framework treats any new
        /// resolution as a forced regeneration. Under the cap the resolution is 1, the framework's own
        /// one point per unit of width, so every zoom shallower than the decode's own density is drawn
        /// exactly as the stock graph draws it.
        /// </remarks>
        private void capResolution(int available, double width)
        {
            if (available <= 0 || DrawWidth <= 0)
                return;

            if (width <= available)
            {
                Resolution = 1;
                return;
            }

            // The framework's own arithmetic (truncated), so the count it asks for is exactly the source's.
            if ((int)(width * Resolution) != available)
                Resolution = (float)((available + 0.5) / width);
        }
    }
}
