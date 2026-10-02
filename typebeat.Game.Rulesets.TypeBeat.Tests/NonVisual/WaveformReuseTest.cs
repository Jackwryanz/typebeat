// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Audio.Track;
using typebeat.Game.Beatmaps;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// A track reload that plays the same audio data keeps the waveform it already decoded. The map's
    /// audio gain rebuilds the track on every committed value, and before this each one threw the
    /// decoded song away and decoded the whole file again for an identical waveform.
    /// </summary>
    [TestFixture]
    public class WaveformReuseTest
    {
        [Test]
        public void ReloadOfTheSameDataKeepsTheWaveform()
        {
            var working = new CountingWorkingBeatmap { Source = "files/ab/abcdef" };

            working.LoadTrack();
            var first = working.Waveform;

            working.LoadTrack();

            Assert.Multiple(() =>
            {
                Assert.That(working.Waveform, Is.SameAs(first));
                Assert.That(working.Built, Is.EqualTo(1), "the song is decoded once");
            });
        }

        [Test]
        public void ReloadOfDifferentDataDecodesAgain()
        {
            var working = new CountingWorkingBeatmap { Source = "files/ab/abcdef" };

            working.LoadTrack();
            var first = working.Waveform;

            // A swap under the same filename lands at a different hash-named store path.
            working.Source = "files/12/123456";
            working.LoadTrack();

            Assert.Multiple(() =>
            {
                Assert.That(working.Waveform, Is.Not.SameAs(first));
                Assert.That(working.Built, Is.EqualTo(2));
            });
        }

        [Test]
        public void UnknownSourceKeepsTheStockRecycle()
        {
            var working = new CountingWorkingBeatmap { Source = null };

            working.LoadTrack();
            var first = working.Waveform;

            working.LoadTrack();

            Assert.Multiple(() =>
            {
                Assert.That(working.Waveform, Is.Not.SameAs(first));
                Assert.That(working.Built, Is.EqualTo(2));
            });
        }

        [Test]
        public void ANeverReadWaveformIsNotBuiltByAReload()
        {
            var working = new CountingWorkingBeatmap { Source = "files/ab/abcdef" };

            working.LoadTrack();
            working.LoadTrack();

            Assert.That(working.Built, Is.Zero);
        }

        private class CountingWorkingBeatmap : TestWorkingBeatmap
        {
            public string? Source;
            public int Built;

            public CountingWorkingBeatmap()
                : base(new Beatmap())
            {
            }

            protected override string? WaveformSource => Source;

            protected override Waveform GetWaveform()
            {
                Built++;
                return new Waveform(null);
            }

            protected override Track GetBeatmapTrack() => new TrackVirtual(1000);
        }
    }
}
