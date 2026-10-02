// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;

namespace typebeat.Game.Input
{
    /// <summary>
    /// The keyboard layout the player's keycaps follow (<see cref="Configuration.OsuSetting.KeyboardLayout"/>).
    /// osu!framework reports keys by physical position (scancode), so the letter printed on a key can
    /// differ from the QWERTY letter its position implies. <see cref="KeycapLayout"/> turns a physical
    /// letter key into the key carrying the same KEYCAP letter on QWERTY, once, at the root input
    /// manager, which is what makes every shortcut follow the keycap; the typing map
    /// (<c>KeyCharMap</c>, in the ruleset) reads the physical position back so typed characters are
    /// unchanged.
    /// <see cref="System"/> leaves keys at their physical positions and resolves direct typing
    /// characters from the OS instead; shortcut names are supplied by the platform provider.
    ///
    /// <para>The member NAMES are stored (in game.ini, and formerly in the ruleset's own settings row,
    /// which <see cref="KeyboardLayoutSettingCarry"/> moves across once), so they must never be renamed.</para>
    /// </summary>
    public enum KeyboardLayout
    {
        Qwerty,

        /// <summary>
        /// German/Central-European: the Y and Z keys are swapped relative to QWERTY, and four US
        /// punctuation positions carry LETTERS instead (o-umlaut, a-umlaut, u-umlaut and eszett),
        /// with the marks they displaced sitting elsewhere (see the QWERTZ punctuation table in
        /// <c>KeyCharMap</c>).
        /// </summary>
        Qwertz,

        /// <summary>
        /// French: A↔Q and Z↔W are swapped relative to QWERTY, M sits on the QWERTY semicolon
        /// position, and the QWERTY M position carries ',' (outside the typeable surface).
        /// </summary>
        Azerty,

        /// <summary>
        /// Resolve direct typing characters using the active operating-system layout.
        /// Shortcuts retain physical positions; their displayed names follow the OS.
        /// </summary>
        [Description("System keyboard layout")]
        System
    }
}
