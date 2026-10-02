// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Input.StateChanges;
using osu.Framework.Testing.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Input;

namespace typebeat.Game.Tests.Visual
{
    /// <summary>
    /// The test scenes' manual input manager, given the same keycap rewrite the game's root input
    /// manager applies (<see cref="OsuUserInputManager"/>), so a test pressing a PHYSICAL key sees what
    /// a player pressing it would. Test input enters here rather than at the root, which is why the
    /// rewrite has to be repeated here; it reads the very same <see cref="KeycapKeyRewriter"/>. Under
    /// the default QWERTY layout the rewrite is the identity, so a scene that never sets a layout is
    /// untouched.
    /// </summary>
    public partial class KeycapManualInputManager : ManualInputManager
    {
        private readonly KeycapKeyRewriter keycaps = new KeycapKeyRewriter();

        [BackgroundDependencyLoader(true)]
        private void load(OsuConfigManager? config)
        {
            if (config != null)
                config.BindWith(OsuSetting.KeyboardLayout, keycaps.Layout);
        }

        protected override List<IInput> GetPendingInputs()
        {
            var inputs = base.GetPendingInputs();

            // Parent input already passed through the root's rewrite.
            if (!UseParentInput)
                keycaps.Rewrite(inputs);

            return inputs;
        }
    }
}
