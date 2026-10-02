// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 354: a set's aligner vocal mode (<see cref="BeatmapSetInfo.AlignerVocalMode"/>) is realm
    /// user data. A set stored before realm schema 61 reads as <see cref="AlignerVocalMode.Aligned"/>
    /// with no migration body, a stored choice survives reopening, and the realm write mapper never
    /// copies it, so an editor save of a set detached before the choice cannot revert it.
    /// </summary>
    [TestFixture]
    public class AlignerVocalModeStorageTest
    {
        private const string filename = "client.realm";

        private string directory = null!;

        [SetUp]
        public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "typebeat-vocal-mode-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // a leftover temp directory is not worth failing a test over.
            }
        }

        [Test]
        public void StoredValuesAreTheColumnsValues()
        {
            // The enum's numbers ARE the stored column: renumbering would silently flip every choice.
            Assert.That((int)AlignerVocalMode.Aligned, Is.EqualTo(0));
            Assert.That((int)AlignerVocalMode.Estimated, Is.EqualTo(1));

            var set = new BeatmapSetInfo();
            Assert.That(set.AlignerVocalMode, Is.EqualTo(AlignerVocalMode.Aligned), "a new set follows the vocals");

            set.AlignerVocalMode = AlignerVocalMode.Estimated;
            Assert.That(set.AlignerVocalModeInt, Is.EqualTo(1));
        }

        [Test]
        public void ChoiceSurvivesReopeningAndOlderSetsReadAligned()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(directory);

            Guid older = Guid.Empty, chosen = Guid.Empty;

            // Written at schema 60, the version before the column: the opened realm must need no
            // migration body for its sets to read Aligned.
            using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(filename, true)) { SchemaVersion = 60 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                    older = add(realm, ruleset);
                });
            }

            using (var access = new RealmAccess(storage, filename))
            {
                Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(older)!.AlignerVocalMode), Is.EqualTo(AlignerVocalMode.Aligned));

                access.Write(r =>
                {
                    chosen = add(r, r.Find<RulesetInfo>("typebeat")!);
                    r.Find<BeatmapSetInfo>(chosen)!.AlignerVocalMode = AlignerVocalMode.Estimated;
                });
            }

            using (var access = new RealmAccess(storage, filename))
            {
                Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(chosen)!.AlignerVocalMode), Is.EqualTo(AlignerVocalMode.Estimated));
                Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(older)!.AlignerVocalMode), Is.EqualTo(AlignerVocalMode.Aligned));
            }
        }

        [Test]
        public void EditorSaveOfAStaleCopyKeepsTheChoice()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(directory);

            using var access = new RealmAccess(storage, filename);

            Guid id = Guid.Empty;
            access.Write(r =>
            {
                var ruleset = r.Find<RulesetInfo>("typebeat") ?? r.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                id = add(r, ruleset);
            });

            // The editor detaches the set before the mapper ticks the toggle...
            var detached = access.Run(r => r.Find<BeatmapSetInfo>(id)!.Detach());
            Assert.That(detached.AlignerVocalMode, Is.EqualTo(AlignerVocalMode.Aligned));

            // ...the toggle writes realm straight away (BeatmapManager.SetAlignerVocalMode)...
            access.Write(r => r.Find<BeatmapSetInfo>(id)!.AlignerVocalMode = AlignerVocalMode.Estimated);

            // ...and the save writes the stale copy back through the realm write mapper.
            access.Write(r => detached.CopyChangesToRealm(r.Find<BeatmapSetInfo>(id)!));

            Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(id)!.AlignerVocalMode), Is.EqualTo(AlignerVocalMode.Estimated));
        }

        private static Guid add(Realm realm, RulesetInfo ruleset)
        {
            var set = new BeatmapSetInfo();
            set.Beatmaps.Add(new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set });
            realm.Add(set);
            return set.ID;
        }
    }
}
