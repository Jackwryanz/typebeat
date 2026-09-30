// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the contents of Settings &gt; Experimental. The section itself holds nothing: it loops the
    /// available rulesets asking each for <c>CreateExperimentalSettings()</c>, so a ruleset that stops
    /// answering leaves an empty section on screen rather than failing anywhere. These assertions are
    /// what makes that silent, and dropping one of the controls, loud.
    ///
    /// The controls are built through <c>BuildControls</c> rather than by loading the subsection: the
    /// dependency loader needs a game host, and all this needs to know is which controls exist.
    /// </summary>
    [TestFixture]
    public class ExperimentalSettingsTest
    {
        [Test]
        public void TypeBeatAnswersTheExperimentalSettingsHook()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.That(ruleset.CreateExperimentalSettings(), Is.InstanceOf<TypeBeatExperimentalSettingsSubsection>(),
                "the Experimental section is empty unless the ruleset hands it a subsection");
        }

        /// <summary>
        /// Everything still on trial in the section, in source order: the sync metric (backlog 251 put
        /// it behind a switch), the local auto-aligner and its high-accuracy tier. Pinned by their
        /// labels because that is the only thing a player sees: the bindables behind them deliberately
        /// did not move (Realm keys stored rows by enum member name), so nothing else here would
        /// notice a control quietly going missing.
        ///
        /// <para>The four typing behaviours that used to be listed here - space to skip a word, manual
        /// newlines, the space error dot and the syllable markers - have settled and their controls
        /// now live in the type!beat section, pinned by
        /// <c>TypeBeatSettingsTest.TheSettledSettingsAreAllPresent</c>. What this test is left guarding
        /// is that they do not quietly come back here or go missing altogether.</para>
        ///
        /// <para>The sync one matters more than the others do: it is the ONLY way back to a display
        /// the game used to ship on, so losing the checkbox would not degrade a feature, it would
        /// delete one with no way to notice.</para>
        /// </summary>
        [Test]
        public void TheOnTrialSettingsAreAllPresent()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);

                Assert.That(controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Select(c => c.Caption.ToString()), Is.EqualTo(new[]
                {
                    "Show sync metric",
                    "Use local auto-aligner",
                    "High-accuracy alignment (about 4x slower per import)",
                }));

                Assert.That(controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>(), Is.Empty, "pop-in controls live in type!beat");
                Assert.That(controls.OfType<SettingsCheckbox>(), Is.Empty, "all experimental toggles use the shared form UI");

                // The one that has to be OFF here: the whole point of the toggle is that the metric
                // is gone unless a player goes looking for it, so a checkbox that came up ticked
                // would ship the thing backlog 251 removed.
                var syncCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Show sync metric");

                Assert.That(syncCheckbox.Current.Value, Is.False);

                // Also OFF: the full tier makes every import about four times slower, a cost the
                // player opts into. The hint is pinned whole because it is the only place the trade
                // (accuracy gained, minutes spent) is spelled out.
                var qualityCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>()
                                              .Single(c => c.Caption.ToString() == TypeBeatExperimentalSettingsSubsection.HIGH_QUALITY_CAPTION);

                Assert.That(qualityCheckbox.Current.Value, Is.False);
                Assert.That(qualityCheckbox.HintText.ToString(), Is.EqualTo(
                    "The aligner listens to the song eight times instead of twice. That times about one more word in a hundred "
                    + "correctly on stamped lyrics, and about two more when the lyrics have no timestamps at all. On a 6-core CPU "
                    + "a 4 minute song takes roughly 2 minutes to import instead of 30 seconds; on a 2-core machine, about twice that."));

                // Bound to the setting the importer reads, not to a copy: ticking it reaches config.
                qualityCheckbox.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.LocalAlignerHighQuality), Is.True);

                // The aligner checkbox is meaningless without the installer, so the pair moved together.
                var installButton = controls.OfType<Container>().Select(c => c.Child).OfType<FormButton>().Single();

                Assert.That(installButton.Caption.ToString(), Is.EqualTo("Install local auto-aligner (~2 GB)"));

                // ILocalAlignerManager is resolved CanBeNull and is absent here, exactly as it is in a
                // headless scene: the button must go dead rather than throw on a click nothing services.
                Assert.That(installButton.Enabled.Value, Is.False);
            }
        }

        /// <summary>
        /// Backlog 353: a venv whose setup never completed offers Repair, not Install, Update or
        /// Reinstall, whatever the version comparison says.
        /// </summary>
        [Test]
        public void TheButtonOffersRepairForAnIncompleteInstall()
        {
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager { NeedsRepair = true }),
                Is.EqualTo("Repair local auto-aligner"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager()),
                Is.EqualTo("Install local auto-aligner (~2 GB)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager { IsInstalled = true }),
                Is.EqualTo("Reinstall local auto-aligner"));
        }

        private class FakeAlignerManager : ILocalAlignerManager
        {
            public bool IsInstalled { get; init; }
            public bool NeedsRepair { get; init; }
            public string? InstalledDevice => null;
            public bool GpuDetected => false;
            public string? InstalledVersion => null;
            public string? ShippedVersion => null;
            public bool UpdateAvailable => false;

            public Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
