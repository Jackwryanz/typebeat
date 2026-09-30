// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// type!beat's additions to a map's metadata (<see cref="BeatmapMetadata.Language"/>, <see cref="BeatmapMetadata.AudioGain"/>,
    /// <see cref="BeatmapMetadata.LyricFont"/>, <see cref="BeatmapMetadata.LyricFontFile"/>) live in the .osu, and the importer
    /// copies the decoded metadata onto the realm row field by field. It used to skip all four, so every imported row read
    /// "no language" whatever its file said, and song select's language grouping put the whole library under "No language set".
    /// Pins the copy through a real import of a real .osz into a real realm, and the heal for libraries imported before it
    /// (<see cref="StoredBeatmapFacts.StampFileMetadata"/>, driven over the whole library once by realm schema 60).
    /// </summary>
    [TestFixture]
    public class FileMetadataImportTest
    {
        private const string realm_filename = "client.realm";

        private string directory = null!;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "typebeat-metadata-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            LyricBeatmapDecoder.Register();
        }

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

        // ---- import ----

        [Test]
        public async Task ImportCopiesEveryStatedValueOntoTheRealmRow()
        {
            string osu = encode(map(BeatmapLanguage.Japanese, audioGain: 2.5, lyricFont: "Noto Sans JP", lyricFontFile: "fonts/noto.ttf"));

            Assert.Multiple(() =>
            {
                // The fixture must actually state all four, or the test proves nothing.
                Assert.That(osu, Does.Contain("Language:japanese"));
                Assert.That(osu, Does.Contain("AudioGain:2.5"));
                Assert.That(osu, Does.Contain("LyricFont: Noto Sans JP"));
                Assert.That(osu, Does.Contain("LyricFontFile: fonts/noto.ttf"));
            });

            var stored = await importAndRead(osu).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.Japanese));
                Assert.That(stored.AudioGain, Is.EqualTo(2.5));
                Assert.That(stored.LyricFont, Is.EqualTo("Noto Sans JP"));
                Assert.That(stored.LyricFontFile, Is.EqualTo("fonts/noto.ttf"));

                // and the fields the copy always carried still arrive.
                Assert.That(stored.Title, Is.EqualTo("Neon Nights"));
                Assert.That(stored.Artist, Is.EqualTo("Synth Rider"));
            });
        }

        [Test]
        public async Task AFileThatStatesNothingImportsTheDefaults()
        {
            string osu = encode(map(BeatmapLanguage.Unspecified, BeatmapMetadata.DEFAULT_AUDIO_GAIN, string.Empty, string.Empty));

            Assert.Multiple(() =>
            {
                // Byte for byte the encoding every map had before these fields existed: none of the four lines.
                Assert.That(osu, Does.Not.Contain("Language:"));
                Assert.That(osu, Does.Not.Contain("AudioGain:"));
                Assert.That(osu, Does.Not.Contain("LyricFont"));
            });

            var stored = await importAndRead(osu).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.Unspecified));
                Assert.That(stored.AudioGain, Is.EqualTo(BeatmapMetadata.DEFAULT_AUDIO_GAIN));
                Assert.That(stored.LyricFont, Is.Empty);
                Assert.That(stored.LyricFontFile, Is.Empty);
            });
        }

        // ---- healing a library imported before the copy existed ----

        [Test]
        public void TheStampFillsADefaultRowFromTheFile()
        {
            var stored = new BeatmapMetadata();
            var file = decode(encode(map(BeatmapLanguage.Korean, 0.5, "Pretendard", "pretendard.otf"))).Metadata;

            bool changed = StoredBeatmapFacts.StampFileMetadata(stored, file);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.True);
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.Korean));
                Assert.That(stored.AudioGain, Is.EqualTo(0.5));
                Assert.That(stored.LyricFont, Is.EqualTo("Pretendard"));
                Assert.That(stored.LyricFontFile, Is.EqualTo("pretendard.otf"));
                Assert.That(StoredBeatmapFacts.StampFileMetadata(stored, file), Is.False, "a second pass has nothing left to change");
            });
        }

        [Test]
        public void AFileThatStatesNothingNeverWipesAStoredValue()
        {
            // The oldest server sets carry no Language line; the online lookup fills the row from the server, and the
            // next decode of that line-less file must not put it back to unspecified. Same for the other three.
            var stored = new BeatmapMetadata { Language = BeatmapLanguage.English, AudioGain = 2, LyricFont = "Inter", LyricFontFile = "inter.ttf" };
            var file = decode(encode(map(BeatmapLanguage.Unspecified, BeatmapMetadata.DEFAULT_AUDIO_GAIN, string.Empty, string.Empty))).Metadata;

            bool changed = StoredBeatmapFacts.StampFileMetadata(stored, file);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.False);
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.English));
                Assert.That(stored.AudioGain, Is.EqualTo(2));
                Assert.That(stored.LyricFont, Is.EqualTo("Inter"));
                Assert.That(stored.LyricFontFile, Is.EqualTo("inter.ttf"));
            });
        }

        [Test]
        public void AValueTheFileStatesWinsOverTheStoredOne()
        {
            // The mapper's declaration is the authority, including over a language the lookup took from the server.
            var stored = new BeatmapMetadata { Language = BeatmapLanguage.English, AudioGain = 2, LyricFont = "Inter", LyricFontFile = "inter.ttf" };
            var file = decode(encode(map(BeatmapLanguage.Japanese, 3, "Noto Sans JP", "noto.ttf"))).Metadata;

            StoredBeatmapFacts.StampFileMetadata(stored, file);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.Japanese));
                Assert.That(stored.AudioGain, Is.EqualTo(3));
                Assert.That(stored.LyricFont, Is.EqualTo("Noto Sans JP"));
                Assert.That(stored.LyricFontFile, Is.EqualTo("noto.ttf"));
            });
        }

        [Test]
        public void ApplyStampsTheMetadataBesideTheFacts()
        {
            // BeatmapUpdater.Process (import, editor save, online refresh) goes through Apply; the background pass
            // calls the same StampFileMetadata inside its own write. A row as an old import left it:
            var row = new BeatmapInfo(new TypeBeatRuleset().RulesetInfo, new BeatmapDifficulty(), new BeatmapMetadata());
            var working = new TestWorkingBeatmap(decode(encode(map(BeatmapLanguage.Spanish, 1.5, "Lora", string.Empty))));

            StoredBeatmapFacts.Apply(row, working);

            Assert.Multiple(() =>
            {
                Assert.That(row.Metadata.Language, Is.EqualTo(BeatmapLanguage.Spanish));
                Assert.That(row.Metadata.AudioGain, Is.EqualTo(1.5));
                Assert.That(row.Metadata.LyricFont, Is.EqualTo("Lora"));
                Assert.That(row.Metadata.LyricFontFile, Is.Empty);
                Assert.That(StoredBeatmapFacts.IsProcessed(row), Is.True, "and the facts are still written");
            });
        }

        [Test]
        public void Schema60SendsEveryRowBackThroughTheBackgroundPass()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "migration"));
            Guid id = Guid.Empty;

            using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(realm_filename, true)) { SchemaVersion = 59 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                    var set = new BeatmapSetInfo();

                    // Fully processed under schema 59, with the metadata an old import left behind.
                    var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set, TargetWpm = 120, HasIntroBeatdrop = true };
                    set.Beatmaps.Add(beatmap);

                    realm.Add(set);
                    id = beatmap.ID;
                });
            }

            using (var access = new RealmAccess(storage, realm_filename))
            {
                var (wpm, language) = access.Run(r =>
                {
                    var b = r.Find<BeatmapInfo>(id)!;
                    return (b.TargetWpm, b.Metadata.Language);
                });

                Assert.Multiple(() =>
                {
                    Assert.That(wpm, Is.EqualTo(StoredBeatmapFacts.UNPROCESSED), "the migration marks the row, the background pass heals it");
                    Assert.That(language, Is.EqualTo(BeatmapLanguage.Unspecified), "the migration itself cannot decode, so it writes no metadata");
                });
            }
        }

        // ---- helpers ----

        /// <summary>
        /// Packs <paramref name="osu"/> into an .osz, imports it through <see cref="BeatmapImporter"/> into a fresh realm,
        /// and returns a detached copy of the single imported row's metadata.
        /// </summary>
        private async Task<BeatmapMetadata> importAndRead(string osu)
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "data"));

            string oszPath = Path.Combine(directory, "set.osz");

            using (var zip = ZipFile.Open(oszPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Synth Rider - Neon Nights (mapper) [Normal].osu").Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(osu).ConfigureAwait(false);

                using (var writer = new StreamWriter(zip.CreateEntry("audio.mp3").Open()))
                    await writer.WriteAsync("fake audio").ConfigureAwait(false);
            }

            using var realm = new RealmAccess(storage, realm_filename);

            // What RulesetStore would register at startup; the importer skips a map whose ruleset is not available.
            realm.Write(r => r.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0)
            {
                Available = true,
            }));

            var importer = new BeatmapImporter(storage, realm);
            var imported = await importer.Import(new ImportTask(oszPath)).ConfigureAwait(false);

            Assert.That(imported, Is.Not.Null, "the import itself failed");

            return realm.Run(r => r.All<BeatmapInfo>().Single().Metadata.DeepClone());
        }

        private static Beatmap map(BeatmapLanguage language, double audioGain, string lyricFont, string lyricFontFile)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.DifficultyName = "Normal";
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.Language = language;
            beatmap.Metadata.AudioGain = audioGain;
            beatmap.Metadata.LyricFont = lyricFont;
            beatmap.Metadata.LyricFontFile = lyricFontFile;

            var line = new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[]
                {
                    new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Word,
            });

            return beatmap;
        }

        private static string encode(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(source, null, sw);

            return sb.ToString();
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
