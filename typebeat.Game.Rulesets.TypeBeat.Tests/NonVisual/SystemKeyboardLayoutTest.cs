// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osuTK.Input;
using SDL;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SystemKeyboardLayoutTest
    {
        [TestCase(SDL_Keycode.SDLK_KP_0, '0')]
        [TestCase(SDL_Keycode.SDLK_KP_1, '1')]
        [TestCase(SDL_Keycode.SDLK_KP_2, '2')]
        [TestCase(SDL_Keycode.SDLK_KP_3, '3')]
        [TestCase(SDL_Keycode.SDLK_KP_4, '4')]
        [TestCase(SDL_Keycode.SDLK_KP_5, '5')]
        [TestCase(SDL_Keycode.SDLK_KP_6, '6')]
        [TestCase(SDL_Keycode.SDLK_KP_7, '7')]
        [TestCase(SDL_Keycode.SDLK_KP_8, '8')]
        [TestCase(SDL_Keycode.SDLK_KP_9, '9')]
        public void SDLKeypadDigitsTypeInNormalAndLiteratePlay(SDL_Keycode keycode, char expected)
        {
            char? character = SystemKeyboardLayout.ToCharacter(keycode);
            Assert.That(character, Is.EqualTo(expected));
            foreach (bool punctuation in new[] { false, true })
            {
                Assert.That(SystemKeyCharMap.TryMap(character, character, punctuation, out char result), Is.True);
                Assert.That(result, Is.EqualTo(expected));
            }
        }

        [TestCase(SDL_Keycode.SDLK_UNKNOWN)]
        [TestCase(SDL_Keycode.SDLK_LEFT)]
        [TestCase(SDL_Keycode.SDLK_KP_ENTER)]
        [TestCase(SDL_Keycode.SDLK_KP_MULTIPLY)]
        [TestCase(SDL_Keycode.SDLK_KP_PERIOD)]
        public void OtherSDLKeycodesRemainInert(SDL_Keycode keycode)
            => Assert.That(SystemKeyboardLayout.ToCharacter(keycode), Is.Null);

        [TestCase('t')]
        [TestCase('T')]
        [TestCase('!')]
        public void SDLUnicodeKeycodesKeepTheirCharacters(char character)
            => Assert.That(SystemKeyboardLayout.ToCharacter((SDL_Keycode)character), Is.EqualTo(character));

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
