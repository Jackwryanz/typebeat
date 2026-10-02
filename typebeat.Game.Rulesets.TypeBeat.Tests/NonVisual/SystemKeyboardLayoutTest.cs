// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osuTK.Input;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SystemKeyboardLayoutTest
    {
        [TestCase('t')]
        [TestCase('T')]
        [TestCase('0')]
        [TestCase(' ')]
        public void DirectCharactersKeepTheirOSCase(char character)
        {
            Assert.That(SystemKeyCharMap.TryMap(character, character, false, out char result), Is.True);
            Assert.That(result, Is.EqualTo(character));
        }

        [TestCase('é')]
        [TestCase('ß')]
        [TestCase('&')]
        [TestCase('|')]
        [TestCase('\n')]
        public void CharactersOutsideTheTypingSurfaceStayInert(char character)
        {
            Assert.That(SystemKeyCharMap.TryMap(character, character, true, out _), Is.False);
        }

        [Test]
        public void PunctuationIsOptInAndUsesTheSharedSupportedSet()
        {
            foreach (char mark in Typeability.PUNCTUATION)
            {
                Assert.That(SystemKeyCharMap.TryMap(mark, mark, false, out _), Is.False);
                Assert.That(SystemKeyCharMap.TryMap(mark, mark, true, out char result), Is.True);
                Assert.That(result, Is.EqualTo(mark));
            }
        }

        [Test]
        public void NormalPlayKeepsShiftedDigitsButLiterateUsesTheOSMark()
        {
            Assert.That(SystemKeyCharMap.TryMap('!', '1', false, out char normal), Is.True);
            Assert.That(normal, Is.EqualTo('1'));
            Assert.That(SystemKeyCharMap.TryMap('!', '1', true, out char literate), Is.True);
            Assert.That(literate, Is.EqualTo('!'));
        }

        [Test]
        public void AnUnavailableResolverNeverFallsBackToQwerty()
        {
            Assert.That(SystemKeyCharMap.TryMap(null, null, true, out _), Is.False);
            Assert.That(KeyCharMap.TryMap(Key.F, KeyboardLayout.System, out _), Is.False);
        }

        [Test]
        public void SystemBindingsKeepPhysicalPositionsAndDisplayOSNames()
        {
            Assert.That(KeycapLayout.ToKeycap(Key.F, KeyboardLayout.System), Is.EqualTo(Key.F));
            Assert.That(KeycapLayout.ToPhysical(InputKey.F, KeyboardLayout.System), Is.EqualTo(InputKey.F));
            var provider = new KeycapKeyCombinationProvider(new ColemakNames(), new Bindable<KeyboardLayout>(KeyboardLayout.System));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.F)), Is.EqualTo("T"));
        }

        private class ColemakNames : ReadableKeyCombinationProvider
        {
            protected override string GetReadableKey(InputKey key) => key == InputKey.F ? "T" : key.ToString();
        }
    }
}
