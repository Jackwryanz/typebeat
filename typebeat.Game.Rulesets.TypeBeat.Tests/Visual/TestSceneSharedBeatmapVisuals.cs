// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Database;
using typebeat.Game.IO;
using typebeat.Game.Models;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;
using Decoder = typebeat.Game.Beatmaps.Formats.Decoder;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneSharedBeatmapVisuals : EditorTestScene
    {
        [Resolved]
        private BeatmapManager manager { get; set; } = null!;

        [Resolved]
        private RealmAccess database { get; set; } = null!;

        private Guid firstId;
        private Guid missingId;
        private Guid copiedId;
        private string? imagePath;

        protected override bool IsolateSavingFromDatabase => false;
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
        {
            var ruleset = database.Run(r => r.Find<RulesetInfo>("typebeat")!.Detach());
            var first = manager.CreateNew(ruleset, API.LocalUser.Value);
            firstId = first.BeatmapInfo.ID;
            // Registered resources with no on-disk data keep playback out of these persistence tests.
            // The real manager still writes, reloads and resolves every difficulty's native file.
            database.Write(r =>
            {
                var set = r.Find<BeatmapInfo>(firstId)!.BeatmapSet!;
                set.Files.Add(new RealmNamedFileUsage(r.Add(new RealmFile { Hash = new string('a', 64) }, update: true), "original.png"));
                set.Files.Add(new RealmNamedFileUsage(r.Add(new RealmFile { Hash = new string('b', 64) }, update: true), "song.mp4"));
            });
            first = reload(firstId);
            first.Metadata.BackgroundFile = "original.png";
            ResourcesSection.ApplyVideoChange(first.Storyboard, "song.mp4");
            ResourcesSection.ApplyVideoOffsetChange(first.Storyboard, -1250);
            manager.Save(first.BeatmapInfo, new TypeBeatBeatmap { BeatmapInfo = first.BeatmapInfo }, storyboard: first.Storyboard);

            var created = manager.CreateNewDifficulty(first.BeatmapSetInfo, first, ruleset);
            missingId = created.BeatmapInfo.ID;
            var copied = manager.CopyExistingDifficulty(first.BeatmapSetInfo, first);
            copiedId = copied.BeatmapInfo.ID;
            return reload(firstId);
        }

        [TearDownSteps]
        public void CleanupImage() => AddStep("remove temporary image", () =>
        {
            if (imagePath != null)
                File.Delete(imagePath);
        });

        [Test]
        public void NewAndCopiedDifficultiesRetainImageVideoAndOffset()
        {
            AddAssert("three saved difficulties", () => savedSet().Beatmaps.Count == 3);
            AddAssert("each saved image matches", () => savedSet().Beatmaps.All(b => b.Metadata.BackgroundFile == "original.png"));
            AddAssert("each saved video and sync matches", () => new[] { firstId, missingId, copiedId }.All(id =>
                savedStoryboard(id).PrimaryVideo is { Path: "song.mp4", StartTime: -1250 }));
            AddAssert("storyboards are independent", () => !ReferenceEquals(reload(firstId).Storyboard, reload(missingId).Storyboard));
        }

        [Test]
        public void OldDifficultyMissingVisualsInheritsFromSibling()
        {
            AddStep("simulate a difficulty saved by the older editor", () =>
            {
                var missing = reload(missingId);
                missing.Metadata.BackgroundFile = string.Empty;
                manager.Save(missing.BeatmapInfo, missing.Beatmap, storyboard: new Storyboard());
            });
            AddAssert("file really has no video", () => savedStoryboard(missingId).PrimaryVideo == null);
            AddAssert("stored image really is absent", () => savedSet().Beatmaps.Single(b => b.ID == missingId).Metadata.BackgroundFile == string.Empty);
            AddAssert("image resolves from sibling", () => reload(missingId).BackgroundFile == "original.png");
            AddAssert("video resolves from sibling with its sync", () => reload(missingId).Storyboard.PrimaryVideo is { Path: "song.mp4", StartTime: -1250 });
        }

        [Test]
        public void OldConflictingDifficultyVisualsShareTheVideoButKeepTheirOwnImage()
        {
            AddStep("give one difficulty its own visual settings", () =>
            {
                var other = reload(missingId);
                database.Write(r => r.Find<BeatmapInfo>(firstId)!.BeatmapSet!.Files.Add(
                    new RealmNamedFileUsage(r.Find<RealmFile>(new string('a', 64))!, "alternative.png")));
                other = reload(missingId);
                other.Metadata.BackgroundFile = "alternative.png";
                var storyboard = new Storyboard();
                ResourcesSection.ApplyVideoChange(storyboard, "song.mp4");
                ResourcesSection.ApplyVideoOffsetChange(storyboard, 990);
                manager.Save(other.BeatmapInfo, other.Beatmap, storyboard: storyboard);
            });
            AddAssert("stored difficulty has a conflicting offset", () => savedStoryboard(missingId).PrimaryVideo!.StartTime == 990);
            // A difficulty naming an image of its own keeps it (the setup screen's per-difficulty
            // background tick writes exactly this); its siblings stay on the shared one.
            AddAssert("the difficulty with its own image resolves it", () => reload(missingId).BackgroundFile == "alternative.png");
            AddAssert("the others resolve the shared image", () => new[] { firstId, copiedId }.All(id => reload(id).BackgroundFile == "original.png"));
            AddAssert("every difficulty resolves the same video timing", () => new[] { firstId, missingId, copiedId }.All(id => reload(id).Storyboard.PrimaryVideo!.StartTime == -1250));
        }

        [Test]
        public void EditorImageOffsetAndVideoRemovalPersistAcrossAllDifficulties()
        {
            AddStep("open setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("resources ready", () => Editor.ChildrenOfType<ResourcesSection>().Any());
            AddStep("replace background image", () =>
            {
                imagePath = Path.Combine(Path.GetTempPath(), $"typebeat_shared_image_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII="));
                Assert.That(resources().ChangeBackgroundImage(new System.IO.FileInfo(imagePath)), Is.True);
            });
            AddAssert("every image is persisted", () => savedSet().Beatmaps.All(b => b.Metadata.BackgroundFile == "bg.png"));
            AddAssert("every reloaded difficulty uses the image", () => new[] { firstId, missingId, copiedId }.All(id => reload(id).BackgroundFile == "bg.png"));
            AddStep("retime video", () => Assert.That(resources().ChangeVideoOffset(375), Is.True));
            AddAssert("every saved video has the same offset", () => new[] { firstId, missingId, copiedId }.All(id =>
                savedStoryboard(id).PrimaryVideo is { Path: "song.mp4", StartTime: 375 }));
            AddStep("reuse video as soundtrack and add shared storyboard video", () =>
            {
                EditorBeatmap.Metadata.AudioFile = "song.mp4";
                var metadata = Beatmap.Value.BeatmapSetInfo.Metadata;
                string name = (metadata.Artist.Length > 0 ? $"{metadata.Artist} - {metadata.Title}" : Path.GetFileNameWithoutExtension(metadata.AudioFile))
                              + $" ({metadata.Author.Username}).osb";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[Events]\nVideo,375,\"song.mp4\"\nSprite,Foreground,Centre,\"bg.png\",320,240\n F,0,0,1000,0,1\n"));
                manager.AddFile(Beatmap.Value.BeatmapSetInfo, stream, name);
            });
            AddStep("clear video", () => Assert.That(resources().ChangeVideo(null), Is.True));
            AddAssert("video removed from every saved difficulty", () => new[] { firstId, missingId, copiedId }.All(id => savedStoryboard(id).PrimaryVideo == null));
            AddAssert("no sibling video reappears after reload", () => new[] { firstId, missingId, copiedId }.All(id => reload(id).Storyboard.PrimaryVideo == null));
            AddAssert("the soundtrack file is retained", () => reload(firstId).BeatmapSetInfo.GetFile("song.mp4") != null);
            AddAssert("other shared storyboard content survives", () => reload(firstId).Storyboard.GetLayer("Foreground").Elements.Any(e => e.Path == "bg.png"));
        }

        private ResourcesSection resources() => Editor.ChildrenOfType<ResourcesSection>().Single();

        private BeatmapSetInfo savedSet() => database.Run(r => r.Find<BeatmapInfo>(firstId)!.BeatmapSet!.Detach());

        private WorkingBeatmap reload(Guid id)
        {
            var info = database.Run(r => r.Find<BeatmapInfo>(id)!.Detach());
            ((IWorkingBeatmapCache)manager).Invalidate(info.BeatmapSet!);
            return manager.GetWorkingBeatmap(info);
        }

        private Storyboard savedStoryboard(Guid id)
        {
            var working = reload(id);
            using var stream = working.GetStream(working.BeatmapSetInfo.GetPathForFile(working.BeatmapInfo.Path!));
            using var reader = new LineBufferedReader(stream);
            return Decoder.GetDecoder<Storyboard>(reader).Decode(reader);
        }
    }
}
