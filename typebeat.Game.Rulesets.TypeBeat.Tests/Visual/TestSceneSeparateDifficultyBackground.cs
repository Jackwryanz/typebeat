// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Database;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.IO;
using typebeat.Game.Models;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;
using Decoder = typebeat.Game.Beatmaps.Formats.Decoder;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The setup screen's "Separate background for this difficulty" tick: off (the default) the chosen
    /// image goes into every difficulty, on it goes into the open one only, and unticking puts the open
    /// difficulty back on the background its siblings share. The tick is derived from the files, never
    /// stored, so these read it back after the editor has reloaded a difficulty from disk.
    /// </summary>
    public partial class TestSceneSeparateDifficultyBackground : EditorTestScene
    {
        [Resolved]
        private BeatmapManager manager { get; set; } = null!;

        [Resolved]
        private RealmAccess database { get; set; } = null!;

        /// <summary>Every difficulty of the fixture, the opened one first.</summary>
        private Guid[] ids = Array.Empty<Guid>();

        private readonly List<string> imagePaths = new List<string>();

        protected override bool IsolateSavingFromDatabase => false;
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        private Guid openedId => ids[0];

        /// <summary>
        /// Two difficulties sharing "original.png", the editor opened on the second, except for the
        /// orphan-retention test, which needs four: the opened one and one other on "own.png", so the opened
        /// difficulty is separate and its image is still named by a sibling after it re-syncs.
        /// </summary>
        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
        {
            bool fourDifficulties = TestContext.CurrentContext.Test.MethodName == nameof(UntickingKeepsAnImageAnotherDifficultyStillNames);

            var ruleset = database.Run(r => r.Find<RulesetInfo>("typebeat")!.Detach());
            var first = manager.CreateNew(ruleset, API.LocalUser.Value);
            Guid firstId = first.BeatmapInfo.ID;

            // Registered resources with no on-disk data, as in TestSceneSharedBeatmapVisuals: the real
            // manager still writes, reloads and resolves every difficulty's native file.
            database.Write(r =>
            {
                var set = r.Find<BeatmapInfo>(firstId)!.BeatmapSet!;
                set.Files.Add(new RealmNamedFileUsage(r.Add(new RealmFile { Hash = new string('a', 64) }, update: true), "original.png"));
                set.Files.Add(new RealmNamedFileUsage(r.Add(new RealmFile { Hash = new string('b', 64) }, update: true), "song.mp4"));
                set.Files.Add(new RealmNamedFileUsage(r.Add(new RealmFile { Hash = new string('c', 64) }, update: true), "own.png"));
            });

            first = reload(firstId);
            first.Metadata.BackgroundFile = "original.png";
            ResourcesSection.ApplyVideoChange(first.Storyboard, "song.mp4");
            ResourcesSection.ApplyVideoOffsetChange(first.Storyboard, -1250);
            manager.Save(first.BeatmapInfo, new TypeBeatBeatmap { BeatmapInfo = first.BeatmapInfo }, storyboard: first.Storyboard);

            var setOrder = new List<Guid> { firstId };

            for (int i = 0; i < (fourDifficulties ? 3 : 1); i++)
                setOrder.Add(manager.CopyExistingDifficulty(reload(firstId).BeatmapSetInfo, reload(firstId)).BeatmapInfo.ID);

            // Never the set's FIRST difficulty: on a split with no majority the first difficulty's image
            // is the shared one (see ResourcesSection.SharedBackgroundFile), so the difficulty that goes
            // separate has to be a later one for the tick to read checked on it.
            Guid opened = setOrder[fourDifficulties ? 2 : 1];
            ids = setOrder.Where(id => id == opened).Concat(setOrder.Where(id => id != opened)).ToArray();

            if (fourDifficulties)
            {
                // Set order: original, original, own (opened), own. Two against two, so no majority, and
                // the first difficulty's "original.png" is the shared image.
                foreach (var id in new[] { setOrder[2], setOrder[3] })
                {
                    var difficulty = reload(id);
                    difficulty.Metadata.BackgroundFile = "own.png";
                    manager.Save(difficulty.BeatmapInfo, difficulty.Beatmap, storyboard: difficulty.Storyboard);
                }
            }

            return reload(opened);
        }

        [TearDownSteps]
        public void CleanupImages() => AddStep("remove temporary images", () =>
        {
            foreach (string path in imagePaths)
                File.Delete(path);
            imagePaths.Clear();
        });

        [Test]
        public void TickOffWritesEveryDifficulty()
        {
            showSetup();

            AddAssert("the shared image starts in the set", () => setHoldsFile("original.png"));
            AddAssert("tick shown on a multi-difficulty set", () => tick().IsPresent);
            AddAssert("tick derived off on a shared background", () => !tick().Current.Value);
            AddAssert("chooser says it applies to all", () => backgroundChooser().HintText.ToString() == ResourcesSection.BACKGROUND_HINT_ALL_DIFFICULTIES);

            chooseImage();

            AddAssert("both metadata rows name the new image", () => savedSet().Beatmaps.All(b => b.Metadata.BackgroundFile == "bg.png"));
            AddAssert("both saved files name the new image", () => ids.All(id => savedBackgroundLine(id) == "bg.png"));
            AddAssert("both difficulties resolve the new image", () => ids.All(id => reload(id).BackgroundFile == "bg.png"));
            AddAssert("the replaced image is gone from the set", () => !setHoldsFile("original.png"));
        }

        [Test]
        public void TickOnWritesOnlyTheOpenDifficulty()
        {
            showSetup();

            AddStep("tick separate background", () => tick().Current.Value = true);
            AddAssert("chooser says this difficulty only", () => backgroundChooser().HintText.ToString() == ResourcesSection.BACKGROUND_HINT_THIS_DIFFICULTY);
            AddAssert("ticking alone changed nothing", () => savedSet().Beatmaps.All(b => b.Metadata.BackgroundFile == "original.png"));

            chooseImage();

            AddAssert("open difficulty's row names the new image", () => savedRow(ids[0]).Metadata.BackgroundFile == "bg.png");
            AddAssert("other difficulty's row is untouched", () => savedRow(ids[1]).Metadata.BackgroundFile == "original.png");
            AddAssert("open difficulty's file names the new image", () => savedBackgroundLine(ids[0]) == "bg.png");
            AddAssert("other difficulty's file is untouched", () => savedBackgroundLine(ids[1]) == "original.png");
            AddAssert("each difficulty resolves its own image", () => reload(ids[0]).BackgroundFile == "bg.png" && reload(ids[1]).BackgroundFile == "original.png");
            AddAssert("the shared image is kept", () => setHoldsFile("original.png"));

            reopen(() => ids[0]);
            AddAssert("tick reads checked on the separate difficulty", () => tick().Current.Value);
            AddAssert("its chooser shows its own image", () => backgroundChooser().Current.Value?.Name == "bg.png");

            reopen(() => ids[1]);
            AddAssert("tick reads unchecked on the other difficulty", () => !tick().Current.Value);
            AddAssert("its chooser shows the shared image", () => backgroundChooser().Current.Value?.Name == "original.png");
        }

        [Test]
        public void UntickingResyncsAndRemovesTheOrphanedImage()
        {
            showSetup();

            AddStep("tick separate background", () => tick().Current.Value = true);
            chooseImage();
            AddAssert("open difficulty is separate", () => resources().HasSeparateBackground);
            AddAssert("its image is in the set", () => setHoldsFile("bg.png"));

            AddStep("untick", () => tick().Current.Value = false);

            AddAssert("open difficulty's row is back on the shared image", () => savedRow(ids[0]).Metadata.BackgroundFile == "original.png");
            AddAssert("open difficulty's file is back on the shared image", () => savedBackgroundLine(ids[0]) == "original.png");
            AddAssert("chooser shows the shared image", () => backgroundChooser().Current.Value?.Name == "original.png");
            AddAssert("no longer separate", () => !resources().HasSeparateBackground);
            AddAssert("the image nobody names is removed", () => !setHoldsFile("bg.png"));
            AddAssert("the shared image is kept", () => setHoldsFile("original.png"));
        }

        [Test]
        public void UntickingKeepsAnImageAnotherDifficultyStillNames()
        {
            showSetup();

            AddAssert("four difficulties", () => savedSet().Beatmaps.Count == 4);
            AddAssert("on a two-two split the first difficulty's image is shared", () => resources().SharedBackgroundFile == "original.png");
            AddAssert("tick derived on for the difficulty on its own image", () => tick().Current.Value);
            AddAssert("chooser says this difficulty only", () => backgroundChooser().HintText.ToString() == ResourcesSection.BACKGROUND_HINT_THIS_DIFFICULTY);

            AddStep("untick", () => tick().Current.Value = false);

            AddAssert("open difficulty is back on the shared image", () => savedRow(ids[0]).Metadata.BackgroundFile == "original.png");
            AddAssert("the difficulty that also names own.png is untouched", () => savedRow(ids[3]).Metadata.BackgroundFile == "own.png");
            AddAssert("own.png is kept while a difficulty names it", () => setHoldsFile("own.png"));
        }

        [Test]
        public void VideoStaysSetWide()
        {
            showSetup();

            AddStep("tick separate background", () => tick().Current.Value = true);
            AddStep("retime video", () => Assert.That(resources().ChangeVideoOffset(375), Is.True));
            AddAssert("every saved video has the new offset", () => ids.All(id => savedStoryboard(id).PrimaryVideo is { Path: "song.mp4", StartTime: 375 }));

            AddStep("clear video", () => Assert.That(resources().ChangeVideo(null), Is.True));
            AddAssert("video removed from every saved difficulty", () => ids.All(id => savedStoryboard(id).PrimaryVideo == null));
        }

        private void chooseImage() => AddStep("choose a new background image", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"typebeat_separate_bg_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII="));
            imagePaths.Add(path);
            backgroundChooser().Current.Value = new System.IO.FileInfo(path);
        });

        private void showSetup()
        {
            AddStep("open setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("resources ready", () => Editor.ChildrenOfType<ResourcesSection>().Any());
        }

        /// <summary>Reloads the editor on a difficulty, so its tick is derived afresh from what was saved.</summary>
        private void reopen(Func<Guid> id)
        {
            AddStep("switch difficulty", () => Editor.SwitchToDifficulty(database.Run(r => r.Find<BeatmapInfo>(id())!.Detach())));
            AddUntilStep("editor on that difficulty", () => Editor?.ReadyForUse == true && Editor.IsCurrentScreen() && Beatmap.Value.BeatmapInfo.ID == id());
            showSetup();
        }

        private ResourcesSection resources() => Editor.ChildrenOfType<ResourcesSection>().Single();

        private FormCheckBox tick() => resources().ChildrenOfType<FormCheckBox>().Single(c => c.Caption.ToString() == ResourcesSection.SEPARATE_BACKGROUND_CAPTION);

        private FormFileSelector backgroundChooser() => resources().ChildrenOfType<FormFileSelector>().First();

        private BeatmapSetInfo savedSet() => database.Run(r => r.Find<BeatmapInfo>(openedId)!.BeatmapSet!.Detach());

        // Read on the managed set: a detached set carries no files, so GetFile on one is always null.
        private bool setHoldsFile(string filename)
            => database.Run(r => r.Find<BeatmapInfo>(openedId)!.BeatmapSet!.Files.Any(f => f.Filename == filename));

        private BeatmapInfo savedRow(Guid id) => savedSet().Beatmaps.Single(b => b.ID == id);

        private WorkingBeatmap reload(Guid id)
        {
            var info = database.Run(r => r.Find<BeatmapInfo>(id)!.Detach());
            ((IWorkingBeatmapCache)manager).Invalidate(info.BeatmapSet!);
            return manager.GetWorkingBeatmap(info);
        }

        /// <summary>The background the difficulty's .osu file itself names, decoded from what was written.</summary>
        private string savedBackgroundLine(Guid id)
        {
            var working = reload(id);
            using var stream = working.GetStream(working.BeatmapSetInfo.GetPathForFile(working.BeatmapInfo.Path!));
            using var reader = new LineBufferedReader(stream);
            return Decoder.GetDecoder<Beatmap>(reader).Decode(reader).BeatmapInfo.Metadata.BackgroundFile;
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
