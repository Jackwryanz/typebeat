// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using osu.Framework;
using osu.Framework.Input.Bindings;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK.Input;
using SDL;
using static SDL.SDL3;

namespace typebeat.Game.Input
{
    public interface ISystemKeyboardLayout
    {
        char? Resolve(Key key, bool shift, bool capsLock, bool altGr);
    }

    /// <summary>
    /// An immutable snapshot of SDL's OS keymap, queried on the input thread because
    /// SDL_GetKeyFromScancode is not thread safe. Gameplay reads it on the update thread.
    /// This resolves direct key legends, not dead-key sequences or IME composition.
    /// </summary>
    public sealed class SystemKeyboardLayout : ISystemKeyboardLayout, IDisposable
    {
        private readonly GameHost? host;
        private Dictionary<(Key, int), char> characters = new Dictionary<(Key, int), char>();
        private volatile bool disposed;

        public SystemKeyboardLayout(GameHost? host)
        {
            if (host?.Window == null || !FrameworkEnvironment.UseSDL3)
            {
                Logger.Log("System keyboard layout requires the SDL3 desktop backend; no preset fallback is applied.", level: LogLevel.Important);
                return;
            }

            this.host = host;
            host.Window.KeymapChanged += refresh;
            refresh();
        }

        private void refresh() => host?.InputThread.Scheduler.Add(() =>
        {
            if (disposed)
                return;

            var next = new Dictionary<(Key, int), char>();
            foreach (Key key in Enum.GetValues<Key>())
            {
                SDL_Scancode scancode = KeyCombination.FromKey(key).ToScancode();
                if (scancode == SDL_Scancode.SDL_SCANCODE_UNKNOWN)
                    continue;

                for (int state = 0; state < 8; state++)
                {
                    SDL_Keymod modifiers = SDL_Keymod.SDL_KMOD_NUM;
                    if ((state & 1) != 0) modifiers |= SDL_Keymod.SDL_KMOD_SHIFT;
                    if ((state & 2) != 0) modifiers |= SDL_Keymod.SDL_KMOD_CAPS;
                    if ((state & 4) != 0) modifiers |= SDL_Keymod.SDL_KMOD_MODE | SDL_Keymod.SDL_KMOD_RALT;

                    uint code = (uint)SDL_GetKeyFromScancode(scancode, modifiers, false);
                    if (code > 0 && code <= char.MaxValue)
                        next[(key, state)] = (char)code;
                }
            }

            Volatile.Write(ref characters, next);
        });

        public char? Resolve(Key key, bool shift, bool capsLock, bool altGr)
        {
            int state = (shift ? 1 : 0) | (capsLock ? 2 : 0) | (altGr ? 4 : 0);
            return Volatile.Read(ref characters).TryGetValue((key, state), out char c) ? c : null;
        }

        public void Dispose()
        {
            disposed = true;
            if (host != null)
                host.Window.KeymapChanged -= refresh;
        }
    }
}
