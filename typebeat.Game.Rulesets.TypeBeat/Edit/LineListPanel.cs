// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The scrollable list of all lyric lines: index, time, and an editable text box per line,
    /// the fastest surface for sweeping text edits ("yeah" → "yeaaaaaaaah") across a whole song.
    /// Clicking a row selects the line and seeks to it. Poll-synced: rows rebuild only when the
    /// line set changes identity; labels refresh in place; a focused text box is never stomped.
    ///
    /// This is also where a SECTION is picked: Ctrl+click toggles a line in or out of the
    /// selection and Shift+click takes the contiguous run from the anchor (the last plain or
    /// Ctrl-clicked row) to the clicked row. Every selected row is tinted, the last-clicked row
    /// stays the ACTIVE line the detail panel edits, and section-level operations (timing
    /// copy/paste, tap timing) consume the whole set. Escape drops it.
    /// </summary>
    public partial class LineListPanel : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        private readonly FillFlowContainer<LineRow> rows;
        private readonly OsuScrollContainer scroll;
        private readonly Container listArea;
        private readonly RoundedButton lyricViewButton;
        private readonly List<TypeBeatHitObject> displayed = new List<TypeBeatHitObject>();
        private bool? lastToggleAvailable;

        // Nothing on the idle per-frame path allocates: the sort is cached, and whether any line
        // has an original (a walk of every word) is re-asked only when the order's version or the
        // map's language moved.
        private readonly OrderedLinesCache orderedLines = new OrderedLinesCache();
        private int originalsCheckedVersion = -1;
        private BeatmapLanguage? originalsCheckedLanguage;
        private bool canShowOriginal;

        public LineListPanel()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.PanelBackground,
                    Alpha = 0.6f,
                },
                listArea = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = scroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ScrollbarOverlapsContent = false,
                        Child = rows = new FillFlowContainer<LineRow>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 2),
                            Padding = new MarginPadding(4),
                        },
                    },
                },
                lyricViewButton = new RoundedButton
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new Vector2(-4, 4),
                    Width = 174,
                    Height = 28,
                    TooltipText = "Switches the editor's lyric display. Original edits update the script editor's line original.",
                    Action = () =>
                    {
                        foreach (LineRow row in rows)
                            row.CommitPendingText();

                        state.ShowOriginalLyrics.Toggle();
                    },
                },
            };

            lyricViewButton.Alpha = 0;
            lyricViewButton.Enabled.Value = false;
        }

        protected override void Update()
        {
            base.Update();

            var current = orderedLines.Get(editorBeatmap);
            var language = editorBeatmap.BeatmapInfo.Metadata.Language;

            if (orderedLines.Version != originalsCheckedVersion || language != originalsCheckedLanguage)
            {
                canShowOriginal = language != BeatmapLanguage.English
                                  && language != BeatmapLanguage.Instrumental
                                  && current.Any(h => h.Line.Original != null || h.Line.UnromanisedWords.Count > 0
                                                      || h.Line.Units.Any(u => u.Original != null));
                originalsCheckedVersion = orderedLines.Version;
                originalsCheckedLanguage = language;
            }

            if (!canShowOriginal && state.ShowOriginalLyrics.Value)
                state.ShowOriginalLyrics.Value = false;

            if (lastToggleAvailable != canShowOriginal)
            {
                listArea.Padding = new MarginPadding { Top = canShowOriginal ? 36 : 0 };
                lyricViewButton.Alpha = canShowOriginal ? 1 : 0;
                lyricViewButton.Enabled.Value = canShowOriginal;
                lastToggleAvailable = canShowOriginal;
            }
            string buttonText = state.ShowOriginalLyrics.Value ? "Lyrics: Original" : "Lyrics: Romanized";

            if (lyricViewButton.Text != buttonText)
                lyricViewButton.Text = buttonText;

            if (!sameAsDisplayed(current))
            {
                displayed.Clear();
                displayed.AddRange(current);

                rows.Clear();

                foreach (var hitObject in current)
                    rows.Add(new LineRow(hitObject));
            }

            // A tap-timing pass shows only the section it is recording. Alpha 0 makes a row
            // non-present, so the FillFlowContainer drops it out of the flow entirely and the list
            // COLLAPSES to the scope rather than leaving holes.
            //
            // Driven from HERE rather than from the row's own Update: a non-present drawable stops
            // being updated, so a row that hid itself could never bring itself back when the pass
            // ended. The panel is always present, so this restores every row the frame the scope
            // clears, whichever way the pass exited.
            var children = rows.Children;

            for (int i = 0; i < children.Count; i++)
                children[i].Alpha = state.HiddenByTapScope(children[i].HitObject) ? 0 : 1;
        }

        private bool sameAsDisplayed(IReadOnlyList<TypeBeatHitObject> current)
        {
            if (current.Count != displayed.Count)
                return false;

            for (int i = 0; i < current.Count; i++)
            {
                if (current[i] != displayed[i])
                    return false;
            }

            return true;
        }

        /// <summary>Brings the active line's row into view (called by the screen on line change).</summary>
        public void ScrollToActive()
        {
            var row = rows.FirstOrDefault(r => r.HitObject == state.ActiveLine.Value);

            if (row != null)
                scroll.ScrollIntoView(row);
        }

        /// <summary>One list row. Public so scene tests can address a specific line's row.</summary>
        public partial class LineRow : CompositeDrawable
        {
            public readonly TypeBeatHitObject HitObject;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private readonly Box background;
            private readonly FillFlowContainer body;
            private OsuSpriteText indexText = null!;
            private OsuSpriteText timeText = null!;
            private OsuTextBox textBox = null!;
            private OsuSpriteText originalCaption = null!;
            private bool? renderedOriginalView;

            // The row's strings are rebuilt only when what they are built from changes: the line
            // (immutable, so a new reference is the only way its text, words or start move), its
            // index and the view. An idle row allocates nothing.
            private LyricLine? builtLine;
            private int builtIndex = -1;
            private bool builtShowOriginal;
            private string display = string.Empty;
            private string caption = string.Empty;

            /// <summary>The caption over the text box: the line's ORIGINAL text (backlog 330), or empty.</summary>
            public string OriginalCaptionText => originalCaption.Text.ToString();

            public LineRow(TypeBeatHitObject hitObject)
            {
                HitObject = hitObject;

                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
                Masking = true;
                CornerRadius = 4;

                InternalChild = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Children = new Drawable[]
                    {
                        background = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.Background,
                            Alpha = 0.9f,
                        },
                        body = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                        },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                // THE ORIGINAL TEXT (backlog 330) as a caption over the romanised line, aligned with
                // the text box, so the mapper can check the romanisation against the source word by
                // word. Absent (and taking no room) on a line without one.
                body.Add(originalCaption = new OsuSpriteText
                {
                    Margin = new MarginPadding { Left = 34 + 76 + 4, Top = 3 },
                    Font = TypeBeatStyle.Lyric(13),
                    Colour = TypeBeatStyle.SungAccent,
                    Alpha = 0,
                });

                body.Add(new GridContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, 34),
                        new Dimension(GridSizeMode.Absolute, 76),
                        new Dimension(),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            indexText = new OsuSpriteText
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            timeText = new OsuSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            textBox = new LineTextBox
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                RelativeSizeAxes = Axes.X,
                                Height = 28,
                                FontSize = 15,
                                CommitOnFocusLost = true,
                                CommittedText = committedText,
                            },
                        },
                    },
                });

                textBox.OnCommit += (_, _) => commitText();
            }

            private void commitText()
            {
                if (!editorBeatmap.HitObjects.Contains(HitObject))
                    return;

                if (textBox.Text == committedText())
                    return;

                if (state.ShowOriginalLyrics.Value)
                {
                    // Use the same operation as the script editor's line-original field.
                    // This leaves the romanized words and their timing intact.
                    TypeBeatEditorOperations.SetLineOriginal(editorBeatmap, HitObject, textBox.Text);
                    textBox.Text = committedText();
                    return;
                }

                if (!TypeBeatEditorOperations.SetLineText(editorBeatmap, HitObject, textBox.Text))
                {
                    // Normalized to empty; refuse and flash (delete the line instead).
                    textBox.Text = TypeBeatEditorOperations.PipeDisplayText(HitObject.Line);
                    background.FlashColour(TypeBeatStyle.ErrorChar, 400, Easing.OutQuint);
                }
            }

            private string committedText() => state.ShowOriginalLyrics.Value
                ? TypeBeatEditorOperations.OriginalCaption(HitObject.Line) ?? TypeBeatEditorOperations.PipeDisplayText(HitObject.Line)
                : TypeBeatEditorOperations.PipeDisplayText(HitObject.Line);

            /// <summary>Commits a focused edit before the view button changes the row's text.</summary>
            public void CommitPendingText()
            {
                if (textBox.HasFocus && textBox.Text != committedText())
                    commitText();
            }

            protected override void Update()
            {
                base.Update();

                if (builtIndex != HitObject.LineIndex)
                {
                    builtIndex = HitObject.LineIndex;
                    indexText.Text = (builtIndex + 1).ToString();
                }

                bool showOriginal = state.ShowOriginalLyrics.Value;

                if (!ReferenceEquals(builtLine, HitObject.Line) || builtShowOriginal != showOriginal)
                {
                    var line = HitObject.Line;

                    if (!ReferenceEquals(builtLine, line))
                        timeText.Text = formatTime(line.StartTime);

                    // The box shows the line in its PIPE form: a subdivided word carries a '|' at each
                    // of its syllable splits ("ap|ple"), which is both how the split is displayed and
                    // how it is edited (see TypeBeatEditorOperations.SetLineText). The pipe is a
                    // reserved character of this surface only; it is stripped on commit and never
                    // reaches the stored lyric or a gameplay cell.
                    string romanized = TypeBeatEditorOperations.PipeDisplayText(line);
                    string? original = TypeBeatEditorOperations.OriginalCaption(line);
                    bool originalView = showOriginal && original != null;
                    display = originalView ? original! : romanized;
                    caption = originalView ? $"Romanized: {romanized}" : captionFor(line);

                    builtLine = line;
                    builtShowOriginal = showOriginal;
                }

                bool modeChanged = renderedOriginalView != showOriginal;
                renderedOriginalView = showOriginal;

                textBox.ReadOnly = false;

                if ((!textBox.HasFocus || modeChanged) && textBox.Text != display)
                    textBox.Text = display;

                if (originalCaption.Text != caption)
                {
                    originalCaption.Text = caption;
                    originalCaption.Alpha = caption.Length > 0 ? 1 : 0;
                }

                bool active = state.ActiveLine.Value == HitObject;
                bool multiSelected = state.MultiSelectedLines.Contains(HitObject);
                background.Colour = active
                    ? TypeBeatStyle.PanelBackground.Lighten(0.5f)
                    : multiSelected
                        ? TypeBeatStyle.PanelBackground.Lighten(0.25f)
                        : TypeBeatStyle.Background;
            }

            /// <summary>
            /// The caption for a line (backlog 330): its original, with a line of nothing but
            /// unromanised words saying so, since its text box is then empty. Empty for a line with
            /// no original at all.
            /// </summary>
            private static string captionFor(LyricLine line)
            {
                string? original = TypeBeatEditorOperations.OriginalCaption(line);

                if (original == null)
                    return string.Empty;

                int unromanised = line.UnromanisedWords.Count;

                return unromanised == 0
                    ? original
                    : $"{original}   ({unromanised} word{(unromanised == 1 ? string.Empty : "s")} to romanise)";
            }

            private static string formatTime(double ms)
            {
                int total = (int)(ms / 1000);
                return $"{total / 60}:{total % 60:00}.{(int)(ms % 1000):000}";
            }

            protected override bool OnClick(ClickEvent e)
            {
                // Ctrl/Shift build a multi-selection (a section, for timing copy/paste and tap
                // timing) without seeking; yanking the playhead mid-selection would fight the user.
                if (e.ControlPressed)
                {
                    state.ToggleLine(HitObject);
                    return true;
                }

                if (e.ShiftPressed)
                {
                    state.SelectLineRange(TypeBeatEditorOperations.OrderedLines(editorBeatmap), HitObject);
                    return true;
                }

                state.SelectLine(HitObject);
                editorClock.SeekSmoothlyTo(HitObject.Line.StartTime);

                // Picking a line from the list ALWAYS brings the fine-timing strip to it. Seeking
                // alone is not enough: the strip only tracks the caret while its own follow is
                // armed, and the first manual pan or strip click disarms that for good, after which
                // a list click used to leave the mapper looking at a completely different part of
                // the song. This is a one-shot pan, so follow itself stays disarmed.
                state.RequestViewSnap(HitObject.Line.StartTime);
                return true;
            }

            /// <summary>
            /// The row's text box, adding one layer to Ctrl+Z. The framework's text box has no
            /// text-level undo and lets the Undo/Redo platform actions bubble, so while a box was
            /// focused mid-edit a Ctrl+Z fired an EDITOR undo out from under the typing: the undo
            /// replaces every hit object instance, the panel rebuilds its rows, and the focused box
            /// is destroyed together with the uncommitted edit. So: while focused with an
            /// in-progress (uncommitted) edit, Undo reverts the box to the committed text and stops
            /// there, and Redo is swallowed so it cannot vaporise the edit either. A pristine
            /// focused box passes both through, so the next Ctrl+Z steps into the editor history
            /// as usual (the layered-undo convention).
            /// </summary>
            private partial class LineTextBox : OsuTextBox
            {
                /// <summary>The line's committed pipe-form text, the value an in-progress edit reverts to.</summary>
                public Func<string> CommittedText { get; init; } = () => string.Empty;

                public override bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
                {
                    if (HasFocus && (e.Action == PlatformAction.Undo || e.Action == PlatformAction.Redo))
                    {
                        string committed = CommittedText();

                        if (Text != committed)
                        {
                            if (e.Action == PlatformAction.Undo)
                                Text = committed;

                            return true;
                        }

                        return false;
                    }

                    return base.OnPressed(e);
                }
            }
        }
    }
}
