// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Input;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Input;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>Drive OS-resolved characters through the real ruleset input chain.</summary>
    public partial class TestSceneSystemKeyboardLayout : TestSceneTypeBeatInput
    {
        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private readonly LayoutSource layout = new LayoutSource();
        private readonly TextSource text = new TextSource();
        private DrawableTypeBeatRuleset ruleset => Children.OfType<DrawableTypeBeatRuleset>().Single();
        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)ruleset.Playfield;
        private TypingEngine engine => playfield.Engine;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs<ISystemKeyboardLayout>(layout);
            dependencies.CacheAs<TextInputSource>(text);
            return dependencies;
        }

        [SetUpSteps]
        public void UseSystemLayout() => AddStep("System layout", () =>
        {
            layout.FCharacter = 't';
            config.SetValue(OsuSetting.KeyboardLayout, KeyboardLayout.System);
        });

        [TearDownSteps]
        public void RestoreLayout() => AddStep("restore QWERTY", () => config.SetValue(OsuSetting.KeyboardLayout, KeyboardLayout.Qwerty));

        [Test]
        public void TestOSCharacterAndTextEventsDoNotTypeTwice()
        {
            AddStep("physical F produces Colemak t", () => InputManager.Key(Key.F));
            AddAssert("OS t judged once, rather than physical f", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].TypedChar == 't');
            AddStep("platform also sends text", () => text.Commit("t"));
            AddAssert("no duplicate judgement", () => engine.CaretIndex == 1);
            AddStep("erase", () => InputManager.Key(Key.BackSpace));
            AddAssert("editing still works", () => engine.CaretIndex == 0);
            AddStep("OS switches to a custom layout", () => layout.FCharacter = 'z');
            AddStep("same physical key", () => InputManager.Key(Key.F));
            AddAssert("new OS character is accepted", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].State == CellState.Correct);
        }

        [Test]
        public void TestRepeatAndSettingChanges()
        {
            AddStep("hold F", () => InputManager.PressKey(Key.F));
            AddWaitStep("wait beyond repeat delay", 30);
            AddAssert("one character per press", () => engine.CaretIndex == 1);
            AddStep("release F", () => InputManager.ReleaseKey(Key.F));
            AddStep("switch to QWERTY", () => config.SetValue(OsuSetting.KeyboardLayout, KeyboardLayout.Qwerty));
            AddStep("press F", () => InputManager.Key(Key.F));
            AddAssert("preset restored", () => engine.CaretIndex == 2 && engine.Lines[0].Cells[1].TypedChar == 'f');
        }

        [Test]
        public void TestFocusAndPauseDoNotLeakTyping()
        {
            OsuTextBox box = null!;
            AddStep("focus another text control", () =>
            {
                Add(box = new OsuTextBox { Size = new Vector2(300, 40) });
            });
            AddUntilStep("text control loaded", () => box.IsLoaded);
            // The scene deliberately targets its manual manager rather than the host's focus.
#pragma warning disable CS0618
            AddStep("give it focus", () => InputManager.ChangeFocus(box));
#pragma warning restore CS0618
            AddAssert("text control has focus", () => box.HasFocus);
            AddStep("type while gameplay is unfocused", () => InputManager.Key(Key.F));
            AddAssert("engine untouched", () => engine.CaretIndex == 0);
            AddStep("close text control", () => box.Expire());
            AddStep("pause", () => ruleset.IsPaused.Value = true);
            AddStep("type while paused", () => InputManager.Key(Key.F));
            AddAssert("engine still untouched", () => engine.CaretIndex == 0);
            AddStep("resume", () => ruleset.IsPaused.Value = false);
            AddStep("type again", () => InputManager.Key(Key.F));
            AddAssert("typing resumes", () => engine.CaretIndex == 1);
        }

        [Test]
        public void TestPhysicalEditingShortcutsAndAltGr()
        {
            AddStep("type a typo", () => InputManager.Key(Key.F));
            AddStep("physical Ctrl+A", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.A);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddAssert("selection gesture survives", () => playfield.CurrentRetypeSelection != null);
            AddStep("retype z", () => InputManager.Key(Key.Z));
            AddAssert("selection collapsed and retyped", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].State == CellState.Correct);
            AddStep("physical Ctrl+Backspace", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.BackSpace);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddAssert("word erased", () => engine.CaretIndex == 0);
            AddStep("AltGr F produces z", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.PressKey(Key.AltRight);
                InputManager.Key(Key.F);
                InputManager.ReleaseKey(Key.AltRight);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddAssert("AltGr character typed once", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].TypedChar == 'z');
        }

        [Test]
        public void TestRightAltWithoutAnAlternateCharacterRemainsAShortcut()
        {
            AddStep("Right Alt plus A has no third-level character", () =>
            {
                InputManager.PressKey(Key.AltRight);
                InputManager.Key(Key.A);
                InputManager.ReleaseKey(Key.AltRight);
            });
            AddAssert("no ordinary letter leaks through Alt", () => engine.CaretIndex == 0);
        }

        private class LayoutSource : ISystemKeyboardLayout
        {
            public char FCharacter = 't';

            public char? Resolve(Key key, bool shift, bool capsLock, bool altGr)
            {
                if (altGr && key == Key.F)
                    return 'z';
                if (key == Key.F)
                    return shift ^ capsLock ? char.ToUpperInvariant(FCharacter) : FCharacter;
                return KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, true, capsLock, out char c) ? c : null;
            }
        }

        private class TextSource : TextInputSource
        {
            public void Commit(string value) => TriggerTextInput(value);
        }
    }
}
