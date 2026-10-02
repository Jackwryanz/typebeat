// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 371: shortcuts follow the KEYCAP. The root input manager rewrites a physical letter key
    /// to the QWERTY key carrying the same keycap letter (<see cref="KeycapLayout"/>,
    /// <see cref="KeycapKeyRewriter"/>), and gameplay typing reads the physical position back
    /// (<see cref="KeyCharMap.TryMapKeycap"/>). The pins here are the translation itself, the promise
    /// that what a physical key TYPES is byte-identical to before on every layout, the stored setting's
    /// carry out of the ruleset config, and the key NAMES the settings screen prints.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class KeycapLayoutTest
    {
        private static readonly KeyboardLayout[] all_layouts = Enum.GetValues<KeyboardLayout>();

        private static readonly Key[] all_keys = Enum.GetValues<Key>().Distinct().ToArray();

        [Test]
        public void TheKeycapIsTheLetterPrintedOnThePhysicalKey()
        {
            // AZERTY: A and Q swap, Z and W swap, M sits on the QWERTY semicolon position.
            Assert.That(KeycapLayout.ToKeycap(Key.Q, KeyboardLayout.Azerty), Is.EqualTo(Key.A));
            Assert.That(KeycapLayout.ToKeycap(Key.A, KeyboardLayout.Azerty), Is.EqualTo(Key.Q));
            Assert.That(KeycapLayout.ToKeycap(Key.W, KeyboardLayout.Azerty), Is.EqualTo(Key.Z));
            Assert.That(KeycapLayout.ToKeycap(Key.Z, KeyboardLayout.Azerty), Is.EqualTo(Key.W));
            Assert.That(KeycapLayout.ToKeycap(Key.Semicolon, KeyboardLayout.Azerty), Is.EqualTo(Key.M));
            Assert.That(KeycapLayout.ToKeycap(Key.M, KeyboardLayout.Azerty), Is.EqualTo(Key.Semicolon));

            // QWERTZ: Y and Z swap.
            Assert.That(KeycapLayout.ToKeycap(Key.Y, KeyboardLayout.Qwertz), Is.EqualTo(Key.Z));
            Assert.That(KeycapLayout.ToKeycap(Key.Z, KeyboardLayout.Qwertz), Is.EqualTo(Key.Y));

            // Keys that do not move stay put (R is R on every layout here).
            foreach (var layout in all_layouts)
            {
                Assert.That(KeycapLayout.ToKeycap(Key.R, layout), Is.EqualTo(Key.R));
                Assert.That(KeycapLayout.ToKeycap(Key.ControlLeft, layout), Is.EqualTo(Key.ControlLeft));
            }
        }

        /// <summary>
        /// A bijection that is its own inverse, so every physical key has exactly one keycap key and a
        /// press can always be released as the key it was pressed as. QWERTY is the identity.
        /// </summary>
        [Test]
        public void TheTranslationIsASwapAndQwertyIsTheIdentity()
        {
            foreach (var layout in all_layouts)
            {
                foreach (var key in all_keys)
                {
                    Key keycap = KeycapLayout.ToKeycap(key, layout);

                    Assert.That(KeycapLayout.ToPhysical(keycap, layout), Is.EqualTo(key), $"{layout} {key}");

                    if (layout == KeyboardLayout.Qwerty)
                        Assert.That(keycap, Is.EqualTo(key));

                    // The InputKey form (stored bindings) agrees with the Key form wherever both exist.
                    var input = KeyCombination.FromKey(key);
                    var inputKeycap = KeyCombination.FromKey(keycap);
                    if (input != InputKey.None && inputKeycap != InputKey.None)
                        Assert.That(KeycapLayout.ToPhysical(inputKeycap, layout), Is.EqualTo(input), $"{layout} {key} as InputKey");
                }

                Assert.That(all_keys.Select(k => KeycapLayout.ToKeycap(k, layout)).Distinct().Count(), Is.EqualTo(all_keys.Length), $"{layout} is one to one");
            }
        }

        /// <summary>
        /// THE identity pin. Every physical key, on every layout, under every shift / caps lock /
        /// punctuation state, driven through the input-layer rewrite and then through the typing map
        /// exactly as live input is, produces the very character (or the very nothing) that
        /// <see cref="KeyCharMap.TryMap(Key, KeyboardLayout, bool, bool, bool, out char)"/> gave the
        /// physical key before the rewrite existed. Replays record those characters, and
        /// <see cref="KeyCharMapTest"/>, <see cref="LiteratePunctuationTest"/> and the Literate mod
        /// tests pin them against the physical key, so they stand unchanged.
        /// </summary>
        [Test]
        public void EveryPhysicalKeyTypesExactlyWhatItTypedBefore()
        {
            foreach (var layout in all_layouts)
            {
                var rewriter = new KeycapKeyRewriter { Layout = { Value = layout } };

                foreach (var physical in all_keys)
                {
                    Key delivered = deliver(rewriter, physical);

                    foreach (bool shift in new[] { false, true })
                    {
                        foreach (bool punctuation in new[] { false, true })
                        {
                            foreach (bool caps in new[] { false, true })
                            {
                                bool before = KeyCharMap.TryMap(physical, layout, shift, punctuation, caps, out char expected);
                                bool after = KeyCharMap.TryMapKeycap(delivered, layout, shift, punctuation, caps, out char actual);

                                Assert.That(after, Is.EqualTo(before), $"{layout} {physical} shift={shift} punct={punctuation} caps={caps}");
                                Assert.That(actual, Is.EqualTo(expected), $"{layout} {physical} shift={shift} punct={punctuation} caps={caps}");
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void TheFrenchHomeRowStillTypesWhatItsKeycapsSay()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };

            // The keycap A (physical Q) is delivered as Key.A and still types 'a'.
            Key a = deliver(rewriter, Key.Q);
            Assert.That(a, Is.EqualTo(Key.A));
            Assert.That(KeyCharMap.TryMapKeycap(a, KeyboardLayout.Azerty, false, false, false, out char typedA) && typedA == 'a');

            // The keycap M (physical semicolon) is delivered as Key.M and types 'm'.
            Key m = deliver(rewriter, Key.Semicolon);
            Assert.That(m, Is.EqualTo(Key.M));
            Assert.That(KeyCharMap.TryMapKeycap(m, KeyboardLayout.Azerty, false, false, false, out char typedM) && typedM == 'm');

            // The ',' keycap (physical M) is inert outside Literate and ',' under it, as before.
            Key comma = deliver(rewriter, Key.M);
            Assert.That(KeyCharMap.TryMapKeycap(comma, KeyboardLayout.Azerty, false, false, false, out _), Is.False);
            Assert.That(KeyCharMap.TryMapKeycap(comma, KeyboardLayout.Azerty, false, true, false, out char typedComma) && typedComma == ',');
        }

        /// <summary>
        /// A release is rewritten to whatever its PRESS was rewritten to, so flipping the layout while
        /// a key is held cannot strand a key in the pressed state.
        /// </summary>
        [Test]
        public void AReleaseAlwaysMatchesItsPress()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };

            var press = rewrite(rewriter, new KeyboardKeyInput(Key.W, true));
            Assert.That(press.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.Z, true)));

            rewriter.Layout.Value = KeyboardLayout.Qwerty;

            var release = rewrite(rewriter, new KeyboardKeyInput(Key.W, false));
            Assert.That(release.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.Z, false)), "released as the key it was pressed as");

            // And the next press of the same key follows the new layout.
            var again = rewrite(rewriter, new KeyboardKeyInput(Key.W, true));
            Assert.That(again.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.W, true)));
        }

        [Test]
        public void OnlyKeyboardKeysAreRewritten()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };
            var mouse = new MouseButtonInput(MouseButton.Left, true);
            var inputs = new List<IInput> { mouse, new KeyboardKeyInput(Key.Q, true) };

            rewriter.Rewrite(inputs);

            Assert.That(inputs[0], Is.SameAs(mouse));
            Assert.That(((KeyboardKeyInput)inputs[1]).Entries.Single().Button, Is.EqualTo(Key.A));
        }

        // ---------------------------------------------------------------------------------------
        // The stored setting's move out of the ruleset config
        // ---------------------------------------------------------------------------------------

        [TestCase("Azerty", KeyboardLayout.Qwerty, KeyboardLayout.Azerty)]
        [TestCase("Qwertz", KeyboardLayout.Qwerty, KeyboardLayout.Qwertz)]
        // A QWERTY row is what every install that ever booted holds (and what an older build run
        // again would write back), so it says nothing and must not reset a layout chosen since.
        [TestCase("Qwerty", KeyboardLayout.Azerty, KeyboardLayout.Azerty)]
        [TestCase("not-a-layout", KeyboardLayout.Qwertz, KeyboardLayout.Qwertz)]
        [TestCase(null, KeyboardLayout.Qwertz, KeyboardLayout.Qwertz)]
        public void TheRulesetRowIsCarriedOnceAndDeleted(string? storedRow, KeyboardLayout gameValue, KeyboardLayout expected)
        {
            // Realm must not schedule its notifications onto NUnit's context (see PaceColourSettingsMigrationTest).
            using var context = new SynchronousRealmTestContext();
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-layout-carry-" + Guid.NewGuid().ToString("N"));

            try
            {
                var storage = new NativeStorage(directory);

                using var realm = new RealmAccess(storage, "client.realm");
                using var config = new OsuConfigManager(storage);

                config.SetValue(OsuSetting.KeyboardLayout, gameValue);

                realm.Write(r =>
                {
                    if (storedRow != null)
                        r.Add(new RealmRulesetSetting { RulesetName = "typebeat", Variant = 0, Key = KeyboardLayoutSettingCarry.RULESET_SETTING_KEY, Value = storedRow });

                    // An unrelated ruleset setting must survive.
                    r.Add(new RealmRulesetSetting { RulesetName = "typebeat", Variant = 0, Key = "SpaceSkipsWord", Value = "False" });
                });

                KeyboardLayoutSettingCarry.Run(realm, config);

                Assert.That(config.Get<KeyboardLayout>(OsuSetting.KeyboardLayout), Is.EqualTo(expected));
                Assert.That(realm.Run(r => r.All<RealmRulesetSetting>().Count(s => s.Key == KeyboardLayoutSettingCarry.RULESET_SETTING_KEY)), Is.Zero, "the orphaned row is gone");
                Assert.That(realm.Run(r => r.All<RealmRulesetSetting>().Count(s => s.Key == "SpaceSkipsWord")), Is.EqualTo(1));

                // One-time: a later choice is not overwritten by a second boot.
                config.SetValue(OsuSetting.KeyboardLayout, KeyboardLayout.Qwerty);
                KeyboardLayoutSettingCarry.Run(realm, config);
                Assert.That(config.Get<KeyboardLayout>(OsuSetting.KeyboardLayout), Is.EqualTo(KeyboardLayout.Qwerty));
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // A realm file can stay locked briefly on Windows; a stray temp folder is harmless.
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Key names on the settings screen and in hotkey hints
        // ---------------------------------------------------------------------------------------

        [Test]
        public void KeyNamesFollowTheKeycap()
        {
            var layout = new Bindable<KeyboardLayout>(KeyboardLayout.Azerty);
            var host = new PhysicalNamingProvider();
            var provider = new KeycapKeyCombinationProvider(host, layout);

            // A letter is named by the letter itself: the binding now answers to that keycap.
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.A)), Is.EqualTo("A"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Z)), Is.EqualTo("Z"));

            // Any other key is named by the host for the PHYSICAL position it now means: the
            // semicolon key is the physical M position (the ',' keycap) under AZERTY.
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Semicolon)), Is.EqualTo("phys:M"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Enter)), Is.EqualTo("phys:Enter"));

            // QWERTY hands everything to the host untouched.
            layout.Value = KeyboardLayout.Qwerty;
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.A)), Is.EqualTo("phys:A"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Semicolon)), Is.EqualTo("phys:Semicolon"));
        }

        [Test]
        public void ALayoutChangeRefreshesKeyNames()
        {
            var layout = new Bindable<KeyboardLayout>(KeyboardLayout.Qwerty);
            var provider = new KeycapKeyCombinationProvider(new PhysicalNamingProvider(), layout);
            int refreshes = 0;
            provider.KeymapChanged += () => refreshes++;

            layout.Value = KeyboardLayout.Azerty;

            Assert.That(refreshes, Is.EqualTo(1));
        }

        private static Key deliver(KeycapKeyRewriter rewriter, Key physical)
        {
            var pressed = rewrite(rewriter, new KeyboardKeyInput(physical, true)).Entries.Single();
            rewrite(rewriter, new KeyboardKeyInput(physical, false));
            return pressed.Button;
        }

        private static KeyboardKeyInput rewrite(KeycapKeyRewriter rewriter, KeyboardKeyInput input)
        {
            var inputs = new List<IInput> { input };
            rewriter.Rewrite(inputs);
            return (KeyboardKeyInput)inputs.Single();
        }

        /// <summary>Stands in for the host's OS-layout-aware provider, naming the physical key it is asked about.</summary>
        private class PhysicalNamingProvider : ReadableKeyCombinationProvider
        {
            protected override string GetReadableKey(InputKey key) => $"phys:{key}";
        }
    }
}
