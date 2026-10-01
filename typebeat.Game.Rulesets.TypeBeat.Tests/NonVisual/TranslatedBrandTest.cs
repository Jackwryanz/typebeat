// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using NUnit.Framework;
using typebeat.Game.Extensions;
using typebeat.Game.Localisation;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 359: every non-English string is read from the resources package's satellite
    /// assemblies, which are upstream's translations and say "osu!". The brand is rewritten once,
    /// in <see cref="ResourceManagerLocalisationStore.Rebrand"/>, and these tests pin that no
    /// translated string of our own localisation namespaces reaches the UI still carrying it.
    /// No compound is kept: the English fallbacks rename every one of them.
    /// </summary>
    [TestFixture]
    public class TranslatedBrandTest
    {
        private const string resources_assembly = @"typebeat.Game.Resources";

        [OneTimeSetUp]
        public void LoadResourcesAssembly()
        {
            // The store finds its assembly among the ALREADY LOADED ones (the game has loaded it by
            // the time a culture is read); a test process has not, so load it by name.
            Assembly.Load(resources_assembly);
        }

        [Test]
        public void PolishFirstRunDescriptionCarriesOurBrand()
        {
            using var store = new ResourceManagerLocalisationStore(@"pl");

            // FirstRunSetupOverlayStrings.FirstRunSetupDescription's key (prefix + key).
            const string lookup = @"typebeat.Game.Resources.Localisation.FirstRunSetupOverlay:first_run_setup_description";

            string? translated = store.Get(lookup);

            if (translated == null)
                Assert.Ignore($"No Polish satellite for {resources_assembly} next to the test assembly, so {lookup} cannot be read.");

            Assert.That(translated, Does.Not.Contain(@"osu!").IgnoreCase);
            Assert.That(translated, Does.Contain(@"type!beat"), "the Polish subtitle names the brand, so the rewrite must have produced it");
        }

        [TestCase(@"Dostosuj osu! do swoich potrzeb", @"Dostosuj type!beat do swoich potrzeb")]
        [TestCase(@"Osu! on käynnissä", @"type!beat on käynnissä")]
        [TestCase(@"osu!n asetukset", @"type!beatn asetukset")]
        [TestCase(@"osu!-Installation", @"type!beat-Installation")]
        [TestCase(@"osu!supporter", @"type!beatsupporter")]
        [TestCase(@"osu!stable", @"type!beatstable")]
        [TestCase(@"osu!の設定", @"type!beatの設定")]
        [TestCase(@"no brand here", @"no brand here")]
        public void RebrandRewritesEveryOccurrence(string translated, string expected)
            => Assert.That(ResourceManagerLocalisationStore.Rebrand(translated), Is.EqualTo(expected));

        [Test]
        public void NoTranslatedStringOfOurNamespacesSaysOsu()
        {
            var resources = Assembly.Load(resources_assembly);
            var namespaces = ourNamespaces();

            Assert.That(namespaces, Is.Not.Empty, "no *Strings class with a prefix const was found, so the scan would be vacuous");

            var offenders = new List<string>();
            var missingCultures = new List<string>();
            int scanned = 0;
            int rewritten = 0;
            int satelliteCultures = 0;

            foreach (var language in Enum.GetValues<Language>())
            {
                if (language == Language.en || language.ToString() == @"debug")
                    continue;

                string code = language.ToCultureCode();
                using var store = new ResourceManagerLocalisationStore(code);

                bool anySatellite = false;

                foreach (string ns in namespaces)
                {
                    var manager = new ResourceManager(ns, resources);

                    // The keys come from the neutral (English) set, so a key a culture has not
                    // translated is still read, through the store's own parent fallback.
                    ResourceSet? neutral;

                    try
                    {
                        neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true);
                    }
                    catch (MissingManifestResourceException)
                    {
                        continue;
                    }

                    if (neutral == null)
                        continue;

                    bool satellite = hasSatellite(manager, store.EffectiveCulture);
                    anySatellite |= satellite;

                    foreach (DictionaryEntry entry in neutral)
                    {
                        if (entry.Value is not string)
                            continue;

                        string key = (string)entry.Key;
                        string? raw = manager.GetString(key, store.EffectiveCulture);
                        string? shown = store.Get($@"{ns}:{key}");

                        scanned++;

                        if (satellite && raw?.Contains(@"osu!", StringComparison.OrdinalIgnoreCase) == true)
                            rewritten++;

                        if (shown?.Contains(@"osu!", StringComparison.OrdinalIgnoreCase) == true)
                            offenders.Add($@"{code} {ns}:{key} = {shown}");
                    }
                }

                if (anySatellite)
                    satelliteCultures++;
                else
                    missingCultures.Add(code);
            }

            if (missingCultures.Count > 0)
                TestContext.Out.WriteLine($"No satellite assembly found for {missingCultures.Count} culture(s), scanned on the neutral fallback only: {string.Join(", ", missingCultures)}");

            TestContext.Out.WriteLine($"Scanned {scanned} translated lookups across {namespaces.Count} namespaces; {rewritten} carried the upstream brand before the rewrite.");

            if (scanned == 0)
                Assert.Ignore($"No {resources_assembly} resources could be read, so there was nothing to scan.");

            if (satelliteCultures > 0)
                Assert.That(rewritten, Is.GreaterThan(0), "no raw translation carried the upstream brand, so the scan proves nothing about the rewrite");
            Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders.Take(50)));
        }

        /// <summary>
        /// The resource namespaces our own <c>*Strings</c> classes read from, taken from their
        /// <c>prefix</c> constants so a newly added class is scanned without touching this test.
        /// </summary>
        private static List<string> ourNamespaces()
        {
            return typeof(FirstRunSetupOverlayStrings).Assembly.GetTypes()
                                                      .Where(t => t.Namespace?.StartsWith(@"typebeat.Game.Localisation", StringComparison.Ordinal) == true)
                                                      .Select(t => t.GetField(@"prefix", BindingFlags.NonPublic | BindingFlags.Static))
                                                      .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
                                                      .Select(f => (string)f!.GetRawConstantValue()!)
                                                      .Distinct()
                                                      .OrderBy(n => n, StringComparer.Ordinal)
                                                      .ToList();
        }

        /// <summary>
        /// Whether a satellite (not the neutral set) answers for <paramref name="culture"/> or one of
        /// its parents, e.g. zh-TW resolves to the zh-Hant satellite.
        /// </summary>
        private static bool hasSatellite(ResourceManager manager, CultureInfo culture)
        {
            for (var c = culture; c.Name.Length > 0; c = c.Parent)
            {
                try
                {
                    if (manager.GetResourceSet(c, true, false) != null)
                        return true;
                }
                catch (MissingManifestResourceException)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
