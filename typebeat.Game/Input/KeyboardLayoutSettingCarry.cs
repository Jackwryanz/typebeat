// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Configuration;
using typebeat.Game.Database;

namespace typebeat.Game.Input
{
    /// <summary>
    /// Moves the keyboard layout a player chose while it was a type!beat RULESET setting (a
    /// <see cref="RealmRulesetSetting"/> row keyed by the member name <c>KeyboardLayout</c>) across to
    /// <see cref="OsuSetting.KeyboardLayout"/>, where the root input manager can read it.
    ///
    /// <para>One-time by construction: the rows are deleted once read, and the ruleset no longer
    /// declares the setting, so nothing writes them again. Only a non-QWERTY value is carried. Every
    /// install that ever booted owns a row (the ruleset config databases every default on first boot),
    /// so a QWERTY row says nothing about what the player chose, and an older build run again after
    /// this one would recreate exactly such a row; carrying it would reset a layout picked since.</para>
    /// </summary>
    public static class KeyboardLayoutSettingCarry
    {
        /// <summary>The member name the ruleset stored the setting under.</summary>
        public const string RULESET_SETTING_KEY = "KeyboardLayout";

        public static void Run(RealmAccess realm, OsuConfigManager config)
        {
            // Read first, so the boots after the carry do not open a write transaction for nothing.
            if (!realm.Run(r => r.All<RealmRulesetSetting>().Any(s => s.Key == RULESET_SETTING_KEY)))
                return;

            KeyboardLayout? carried = null;

            realm.Write(r =>
            {
                var rows = r.All<RealmRulesetSetting>().Where(s => s.Key == RULESET_SETTING_KEY).ToList();

                foreach (var row in rows)
                {
                    if (carried == null && Enum.TryParse(row.Value, out KeyboardLayout layout) && Enum.IsDefined(layout) && layout != KeyboardLayout.Qwerty)
                        carried = layout;

                    r.Remove(row);
                }
            });

            if (carried != null)
                config.SetValue(OsuSetting.KeyboardLayout, carried.Value);
        }
    }
}
