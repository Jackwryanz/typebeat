// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// <see cref="TypeBeatEditorOperations.OrderedLines"/> for a surface that asks every frame: the
    /// sort runs only when the hit objects changed, and an unchanged frame allocates nothing.
    ///
    /// <para>The check is a walk of the beatmap's hit objects against a snapshot of what the sort
    /// last saw, not a subscription to the beatmap's change events, because it has to see EVERY
    /// change and the events do not: an edit that replaces a line raises
    /// <c>HitObjectUpdated</c> only when the operation remembered to call
    /// <c>Update</c>. The sort key is a line's <see cref="LyricLine.StartTime"/> then its
    /// <see cref="TypeBeatHitObject.LineIndex"/>, and a <see cref="LyricLine"/> is immutable (every
    /// edit assigns a new one), so the hit object, its line REFERENCE and its line index are
    /// together exactly what the sort depends on.</para>
    /// </summary>
    public sealed class OrderedLinesCache
    {
        private readonly List<(HitObject hitObject, LyricLine? line, int lineIndex)> snapshot = new List<(HitObject, LyricLine?, int)>();
        private List<TypeBeatHitObject> ordered = new List<TypeBeatHitObject>();
        private bool valid;

        /// <summary>
        /// Bumped every time the order is recomputed, so a consumer can skip its own diff of the
        /// list when nothing moved.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>The lines of <paramref name="editorBeatmap"/> in typing order. Do not mutate the result.</summary>
        public IReadOnlyList<TypeBeatHitObject> Get(EditorBeatmap editorBeatmap)
        {
            if (valid && matches(editorBeatmap.HitObjects))
                return ordered;

            snapshot.Clear();

            var hitObjects = editorBeatmap.HitObjects;

            for (int i = 0; i < hitObjects.Count; i++)
            {
                var h = hitObjects[i];
                snapshot.Add(h is TypeBeatHitObject t ? (h, t.Line, t.LineIndex) : (h, null, 0));
            }

            ordered = TypeBeatEditorOperations.OrderedLines(editorBeatmap);
            valid = true;
            Version++;
            return ordered;
        }

        private bool matches(IReadOnlyList<HitObject> hitObjects)
        {
            if (hitObjects.Count != snapshot.Count)
                return false;

            for (int i = 0; i < hitObjects.Count; i++)
            {
                var (hitObject, line, lineIndex) = snapshot[i];
                var h = hitObjects[i];

                if (!ReferenceEquals(h, hitObject))
                    return false;

                if (h is TypeBeatHitObject t && (!ReferenceEquals(t.Line, line) || t.LineIndex != lineIndex))
                    return false;
            }

            return true;
        }
    }
}
