// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;

namespace typebeat.Game.Input
{
    /// <summary>
    /// Names keys the way the player's keycaps read them, for every place a key combination is shown
    /// (Settings &gt; Input, hotkey hints, tooltips, conflict popovers). Cached by <see cref="OsuGameBase"/>
    /// in place of the host's provider.
    ///
    /// <para>A stored binding's <see cref="InputKey"/> now means a KEYCAP key (the root input manager
    /// rewrites keys before they are matched, see <see cref="KeycapLayout"/>), while the host's provider
    /// names a key by asking the OS layout what its PHYSICAL position carries. So a LETTER under a
    /// non-QWERTY setting is named by the letter itself, which is exactly the keycap the setting
    /// describes; any other key is translated back to its physical position and named by the host as
    /// before (on AZERTY the swapped semicolon key is the ',' keycap, and the OS layout says so).
    /// Under QWERTY nothing is translated and the host's names are untouched.</para>
    /// </summary>
    public class KeycapKeyCombinationProvider : ReadableKeyCombinationProvider
    {
        private static readonly MethodInfo? on_keymap_changed =
            typeof(ReadableKeyCombinationProvider).GetMethod("OnKeymapChanged", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ReadableKeyCombinationProvider host;
        private readonly IBindable<KeyboardLayout> layout;

        public KeycapKeyCombinationProvider(ReadableKeyCombinationProvider host, IBindable<KeyboardLayout> layout)
        {
            this.host = host;
            this.layout = layout.GetBoundCopy();

            // Displays refresh on KeymapChanged, which only the framework can raise on this instance
            // (its raiser is internal), so it is reached by reflection. Absent in some future framework,
            // the names simply refresh the next time a display rebuilds.
            host.KeymapChanged += raiseKeymapChanged;
            this.layout.BindValueChanged(_ => raiseKeymapChanged());
        }

        protected override string GetReadableKey(InputKey key)
        {
            if (layout.Value is KeyboardLayout.Qwertz or KeyboardLayout.Azerty && key >= InputKey.A && key <= InputKey.Z)
                return key.ToString();

            return host.GetReadableString(new KeyCombination(KeycapLayout.ToPhysical(key, layout.Value)));
        }

        private void raiseKeymapChanged() => on_keymap_changed?.Invoke(this, null);
    }
}
