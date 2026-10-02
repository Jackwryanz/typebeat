// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Apply the existing typing surface to OS-resolved direct characters.</summary>
    public static class SystemKeyCharMap
    {
        public static bool TryMap(char? resolved, char? unshifted, bool punctuation, out char c)
        {
            c = default;
            // Normal play keeps the digit on a shifted number key, just like the presets.
            if (!punctuation && unshifted is >= '0' and <= '9')
                resolved = unshifted;

            if (resolved is not char value)
                return false;

            if (value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' '
                || (punctuation && Typeability.PUNCTUATION.Contains(value)))
            {
                c = value;
                return true;
            }

            return false;
        }
    }
}
