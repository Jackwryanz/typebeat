// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// How the local auto-aligner times a map's words (backlog 354). A per-SET choice the mapper makes
    /// when the aligned result is wrong, stored as <see cref="BeatmapSetInfo.AlignerVocalMode"/> and
    /// read by every later import and re-align of that set. Never a default and never picked
    /// automatically: on ordinary songs <see cref="Estimated"/> times far worse than <see cref="Aligned"/>.
    /// </summary>
    /// <remarks>The numeric values are the stored realm column's, so they must never be renumbered.</remarks>
    public enum AlignerVocalMode
    {
        /// <summary>The aligner follows the vocals (its ordinary decoders). Every set's default.</summary>
        Aligned = 0,

        /// <summary>
        /// The aligner keeps no acoustic path: every line is paced evenly from its line stamp plus the
        /// song's measured stamp lead (<c>align_lyrics.py --vocal-mode estimated</c>, aligner version 7).
        /// For vocals the model cannot follow (screamed, effect-heavy); it needs line stamps.
        /// </summary>
        Estimated = 1,
    }
}
