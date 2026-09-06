using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Komet.Measure;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Komet.Gui;

/// <summary>The window's views, in the order the tab strip lists them.</summary>
internal enum KometView
{
    Overview,
    Frametime,
    Cpu,
    Gpu,
    Rendering,
    Culling,
    Entities,
    Chunks,
    Memory,
    Threads,
    Cache,
    Mods,
    Hitches,
    Profiler,
    Toggles,
    Config,
    Stress,
    Conflicts,
    Report,
}

/// <summary>
/// The '.komet' window: one place to read everything this mod already measures, and to reach
/// everything it can already do.
///
/// It measures NOTHING of its own. Every figure in it comes from the same statics the F7
/// overlay, the '.komet' replies and the report read - <see cref="FrameStats"/>,
/// <see cref="HitchLog"/>, <see cref="GpuFrameTimer"/>, <see cref="ModProfiler"/>, the patch
/// classes' own counters - and every button calls the very method the chat command calls. A
/// window with its own sampling would be a second instrument to keep in agreement with the
/// first, and the first is the one people paste into bug reports.
///
/// What it costs is the one thing a performance window must not be vague about:
///
///   * it composes ONLY the view on screen, and only on a cadence (4 Hz, stretched by
///     <see cref="DebugHud.NextIntervalSeconds"/> if a rebuild turns out expensive on this
///     machine, exactly as the overlay does);
///   * the text is rastered by <see cref="TextPanel"/>, which draws the visible lines of
///     pre-formatted monospace instead of running the engine's word-by-word autobreak layout
///     over the whole block;
///   * the composed GUI itself is rebuilt on a tab click and on nothing else;
///   * the per-refresh cost is added to <see cref="FrameStats.AddHudMs"/>, so a frame this
///     window spikes is booked to the overlay column of the hitch log rather than vanishing
///     into "outside", and it is printed in the overview next to the overlay's own figure.
///
/// Closed, it is not in the frame at all: no renderer, no listener, no sampling.
/// </summary>
internal sealed class KometDialog : GuiDialog
{
    /// <summary>The hotkey code the engine's dialog toggling goes through. Ctrl+F7 by default,
    /// alongside F7 (overlay) and Shift+F7 (mod overlay): the engine matches modifiers exactly
    /// and runs that pass before the modifier-ignoring one, so the three cannot trigger each
    /// other, and a key this mod already owns is one that provably is not somebody's macro.</summary>
    public const string HotkeyCode = "kometgui";

    private const double TabWidth = 152;
    private const double MaxContentWidth = 860;
    private const double MaxContentHeight = 528;
    private const double MinContentWidth = 420;
    private const double GraphHeight = 132;

    /// <summary>
    /// What the dialog needs AROUND its content, in the same unscaled units: the inset's 6, the
    /// 5/40/36/56 the background is forked with, its 10 of padding on every side - and, across,
    /// the tab column that hangs beside it and is what the alignment offset makes room for.
    /// </summary>
    private const double ChromeWidth = 6 + 5 + 36 + 20 + TabWidth;
    private const double ChromeHeight = 6 + 40 + 56 + 20;

    /// <summary>The tab strip's height: GuiElementVerticalTabs draws each tab 25 units tall with
    /// 5 between them, and the last one needs no spacing after it. Nineteen tabs are the reason
    /// this window has a minimum height at all.</summary>
    private static double TabStripHeight => Enum.GetValues<KometView>().Length * 30.0 - 5.0;

    /// <summary>
    /// What the text panel is worth in monospace cells at the size it was designed at: the
    /// content width less the panel's own margins, over the advance of the 14 px monospace face.
    /// The panel measures its own font and its own width at runtime and uses THAT - a small
    /// window gets fewer cells - this is the widest case, for a layout review outside the game
    /// where there is neither a font map nor a screen to ask.
    /// </summary>
    internal const int NominalColumns = 100;

    private readonly KometModSystem mod;
    private readonly StringBuilder sb = new(4096);

    private TextPanel panel;
    private FrameGraph graph;
    private KometView view = KometView.Overview;

    /// <summary>Scroll offset per view, so coming back to a view lands where it was left.</summary>
    private readonly double[] scrollByView = new double[Enum.GetValues<KometView>().Length];

    private float accum;
    private float interval = 0.25f;

    /// <summary>
    /// A recompose asked for from inside the composer's own input handling - a tab click, a
    /// switch, an action button. It is carried out at the top of the next frame instead of
    /// on the spot, because composing disposes the composer whose element is mid-click, and
    /// the engine is still walking that element list when the handler returns.
    /// </summary>
    private bool recomposeWanted;
    private double avgRefreshMs;
    private int lastLineCount = -1;

    /// <summary>The content size the current composition was built for - a resized game window
    /// (or a changed GUI scale) has to rebuild it, or the dialog keeps a size the screen no
    /// longer has.</summary>
    private (double w, double h) composedFor;

    /// <summary>Which group of toggles the Toggles view is showing.</summary>
    private ToggleGroup toggleGroup = ToggleGroup.Culling;

    /// <summary>The sentence the last flip produced, shown under the switches. The chat command
    /// prints exactly this; the window shows it in place rather than sending the player to the
    /// chat log for the answer to a click they just made.</summary>
    private string lastFlip;

    public override string ToggleKeyCombinationCode => HotkeyCode;

    /// <summary>Above the HUD, below the engine's own modal boxes.</summary>
    public override double DrawOrder => 0.2;

    public KometDialog(ICoreClientAPI capi, KometModSystem mod) : base(capi)
    {
        this.mod = mod;
    }

    public override void OnGuiOpened()
    {
        Compose();
        base.OnGuiOpened();
    }

    public override void OnGuiClosed()
    {
        // Nothing of this window may survive its own closing: the panel and the graph each own
        // a cairo surface and a GL texture, and a window that is opened and closed twenty times
        // in a session must not leave twenty of them behind.
        base.OnGuiClosed();
        // ClearComposers, not "SingleComposer = null": the setter behind that property assigns
        // OnFocusChanged on whatever it is given, so handing it a null is an exception, not an
        // empty window.
        ClearComposers();
        panel = null;
        graph = null;
    }

    /// <summary>Opens the window on a given view - what '.komet report' and friends use to land
    /// the player on the page that answers the command they just typed.</summary>
    public bool OpenAt(KometView which)
    {
        var recompose = IsOpened() && which != view;
        view = which;
        if (!TryOpen()) return false;
        if (recompose) Compose();
        return true;
    }

    // ---- composition ----------------------------------------------------------------
    // Runs on a tab click, on open, and after a toggle flip changes what the switches read.
    // Never on a refresh: composing builds a static surface for the whole dialog, and doing
    // that four times a second is the mistake this window exists to help people find.

    private void Compose()
    {
        // The old composer owns the previous panel's surface and texture; the fields have to
        // let go of them in the same breath, or a view without a graph would keep the disposed
        // one reachable until the next one replaced it.
        SingleComposer?.Dispose();
        panel = null;
        graph = null;

        var isToggles = view == KometView.Toggles;
        var hasGraph = view == KometView.Frametime;

        (var contentWidth, var contentHeight) = ContentSize();
        composedFor = (contentWidth, contentHeight);

        var l = BuildLayout(contentWidth, contentHeight, hasGraph);
        var insetBounds = l.Inset;
        var contentBounds = l.Content;
        var scrollBounds = l.Scroll;
        var textBounds = l.Text;
        var graphBounds = l.Graph;
        var closeButton = l.Close;
        var leftButton = l.LeftButton;
        var bgBounds = l.Bg;
        var dialogBounds = l.Dialog;
        var tabBounds = l.Tabs;

        var compo = capi.Gui.CreateCompo("kometwindow", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Title(), () => TryClose())
            .AddVerticalTabs(BuildTabs(), tabBounds, OnTabClicked, "tabs")
            .BeginChildElements(bgBounds)
            .AddInset(insetBounds, 3, 0.85f);

        if (isToggles)
        {
            ComposeToggleRows(compo, contentBounds);
        }
        else
        {
            if (hasGraph)
            {
                graph = new FrameGraph(capi, graphBounds, MonoFont());
                compo.AddInteractiveElement(graph, "graph");
            }

            panel = new TextPanel(capi, textBounds, MonoFont());
            compo.AddInteractiveElement(panel, "panel");
            compo.AddVerticalScrollbar(OnScroll, scrollBounds, "scrollbar");
        }

        AddActions(compo, leftButton, closeButton);

        SingleComposer = compo.EndChildElements().Compose();
        compo.GetVerticalTab("tabs")?.SetValue((int)view, false);

        lastLineCount = -1;
        accum = 0;
        Refresh();                     // the new view must have content on its first frame
        if (panel != null)
        {
            panel.ScrollToUnscaled(scrollByView[(int)view]);
            var bar = compo.GetScrollbar("scrollbar");
            bar?.SetScrollbarPosition((int)Math.Max(0, scrollByView[(int)view]));
        }
    }

    /// <summary>Every bounds the window is built from. Handed out as one thing so the layout
    /// can be built - and checked - without a composer, a GL context or a screen.</summary>
    internal readonly struct Layout
    {
        public ElementBounds Dialog { get; init; }
        public ElementBounds Bg { get; init; }
        public ElementBounds Inset { get; init; }
        public ElementBounds Content { get; init; }
        public ElementBounds Scroll { get; init; }
        public ElementBounds Text { get; init; }
        public ElementBounds Graph { get; init; }
        public ElementBounds Close { get; init; }
        public ElementBounds LeftButton { get; init; }
        public ElementBounds Tabs { get; init; }
    }

    /// <summary>
    /// The window's bounds tree.
    ///
    /// Pure and static on purpose: this is the part that was wrong for two builds and looked
    /// like a rendering bug, so verify builds the very same tree against a stand-in screen and
    /// checks what a screenshot would have to show - that the panel is inside its frame, that
    /// the scrollbar and the buttons are beside and under it rather than 43 units above, and
    /// that the whole thing fits the window it was sized for.
    ///
    /// The order matters and is the whole lesson. ForkBoundingParent does not only return a
    /// parent: it MOVES the bounds it was called on into that parent, to (leftSpacing,
    /// topSpacing). Anything derived from the inset BEFORE the fork - the scrollbar beside it,
    /// the buttons under it, the panel inside it - keeps the position the inset had beforehand,
    /// and the fork then pulls the frame out from under all of them. So the inset is forked
    /// first, and everything else is placed against where it ended up.
    /// </summary>
    internal static Layout BuildLayout(double contentWidth, double contentHeight, bool hasGraph)
    {
        var inset = ElementBounds.Fixed(0, 0, contentWidth + 6, contentHeight + 6);
        var bg = inset.ForkBoundingParent(5, 40, 36, 56)
            .WithFixedPadding(GuiStyle.ElementToDialogPadding / 2.0);

        // Where the inset ended up, plus its own 3-unit border: the origin everything that
        // draws INSIDE the frame is placed against.
        var inX = inset.fixedX + 3;
        var inY = inset.fixedY + 3;

        var content = ElementBounds.Fixed(inX, inY, contentWidth, contentHeight);
        var scroll = inset.CopyOffsetedSibling(contentWidth + 10, 0, 0, 0).WithFixedWidth(20);

        var text = hasGraph
            ? ElementBounds.Fixed(inX + 4, inY + GraphHeight + 8, contentWidth - 8, contentHeight - GraphHeight - 12)
            : ElementBounds.Fixed(inX + 4, inY + 4, contentWidth - 8, contentHeight - 8);
        var graph = ElementBounds.Fixed(inX + 4, inY + 4, contentWidth - 8, GraphHeight);

        var close = ElementBounds.FixedSize(0, 0).FixedUnder(inset, 14)
            .WithAlignment(EnumDialogArea.RightFixed).WithFixedPadding(14, 4);
        var left = ElementBounds.FixedSize(0, 0).FixedUnder(inset, 14)
            .WithAlignment(EnumDialogArea.LeftFixed).WithFixedPadding(14, 4);

        bg.WithChildren(inset, content, scroll, close, left);
        bg.WithChild(text);
        if (hasGraph) bg.WithChild(graph);

        var dialog = bg.ForkBoundingParent().WithAlignment(EnumDialogArea.CenterMiddle)
            .WithFixedAlignmentOffset(TabWidth / 2.0, 0);

        // The tab column hangs to the left of the dialog, which is what the alignment offset
        // above makes room for. It is exactly as tall as the nineteen tabs are - never the
        // content's height, which would cut the last of them off a short page - and it slides
        // up towards the dialog's top edge as the dialog gets shorter, so it stays inside it
        // down to the smallest window the height floor allows.
        var tabTop = Math.Min(32, contentHeight + ChromeHeight - TabStripHeight);
        var tabs = ElementBounds.Fixed(-TabWidth, tabTop, TabWidth, TabStripHeight);

        // What the composer does when the elements are added: the background and the tab strip
        // are children of the dialog, everything else of the background.
        dialog.WithChild(bg);
        dialog.WithChild(tabs);

        return new Layout
        {
            Dialog = dialog, Bg = bg, Inset = inset, Content = content, Scroll = scroll,
            Text = text, Graph = graph, Close = close, LeftButton = left, Tabs = tabs,
        };
    }

    /// <summary>
    /// How big the content may be on this screen at this GUI scale.
    ///
    /// It used to be a constant 860x528, which needs a window of 1079x650 unscaled units once
    /// the chrome and the tab column are counted. Below that the dialog does not shrink, it
    /// simply hangs over the edges - the scrollbar, the close button and the title bar's own
    /// buttons are the first things off-screen, and nothing about that reads as "too small",
    /// it reads as broken. So the content is what the window has room for, up to the size it
    /// was designed at.
    ///
    /// The floor across is a page still worth reading; down it is the tab strip, which cannot
    /// be made shorter and cannot lose its last entries. A window smaller than that overhangs
    /// as it did before - there is nothing else to give.
    /// </summary>
    private (double w, double h) ContentSize()
        => ContentSizeFor(capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale);

    /// <summary>The same answer for a stated screen, so verify can ask it for the sizes no
    /// build machine has.</summary>
    internal static (double w, double h) ContentSizeFor(double frameWidth, double frameHeight, float guiScale)
    {
        var scale = Math.Max(0.01f, guiScale);
        var roomW = frameWidth / scale - 2 * GuiStyle.DialogToScreenPadding;
        var roomH = frameHeight / scale - 2 * GuiStyle.DialogToScreenPadding;
        return (Math.Clamp(roomW - ChromeWidth, MinContentWidth, MaxContentWidth),
                Math.Clamp(roomH - ChromeHeight, TabStripHeight - ChromeHeight, MaxContentHeight));
    }

    private string Title()
        => Loc.T("komet:gui-title", "komet {0} · performance", KometVersion.Display(mod.Mod.Info.Version));

    /// <summary>The overlay's font: the tables in here are the overlay's tables, and they only
    /// line up in a monospace face.</summary>
    private static CairoFont MonoFont()
        => CairoFont.WhiteSmallText().WithFont("monospace").WithFontSize(14f);

    private GuiTab[] BuildTabs()
    {
        var names = Enum.GetValues<KometView>();
        var tabs = new GuiTab[names.Length];
        for (var i = 0; i < names.Length; i++)
            tabs[i] = new GuiTab { Name = TabName(names[i]), DataInt = i };
        return tabs;
    }

    internal static string TabName(KometView v) => v switch
    {
        KometView.Overview => Loc.T("komet:gui-tab-overview", "Overview"),
        KometView.Frametime => Loc.T("komet:gui-tab-frametime", "Frametime"),
        KometView.Cpu => Loc.T("komet:gui-tab-cpu", "CPU"),
        KometView.Gpu => Loc.T("komet:gui-tab-gpu", "GPU"),
        KometView.Rendering => Loc.T("komet:gui-tab-rendering", "Rendering"),
        KometView.Culling => Loc.T("komet:gui-tab-culling", "Culling"),
        KometView.Entities => Loc.T("komet:gui-tab-entities", "Entities"),
        KometView.Chunks => Loc.T("komet:gui-tab-chunks", "Chunks"),
        KometView.Memory => Loc.T("komet:gui-tab-memory", "Memory / GC"),
        KometView.Threads => Loc.T("komet:gui-tab-threads", "Threads / Jobs"),
        KometView.Cache => Loc.T("komet:gui-tab-cache", "Cache"),
        KometView.Mods => Loc.T("komet:gui-tab-mods", "Mods"),
        KometView.Hitches => Loc.T("komet:gui-tab-hitches", "Hitches"),
        KometView.Profiler => Loc.T("komet:gui-tab-profiler", "Profiler"),
        KometView.Toggles => Loc.T("komet:gui-tab-toggles", "Toggles"),
        KometView.Config => Loc.T("komet:gui-tab-config", "Config"),
        KometView.Stress => Loc.T("komet:gui-tab-stress", "Stress Test"),
        KometView.Conflicts => Loc.T("komet:gui-tab-conflicts", "Conflicts"),
        KometView.Report => Loc.T("komet:gui-tab-report", "Report"),
        _ => v.ToString(),
    };

    private void OnTabClicked(int index, GuiTab tab)
    {
        var next = (KometView)index;
        if (next == view) return;
        if (panel != null) scrollByView[(int)view] = CurrentScroll();
        view = next;
        recomposeWanted = true;
    }

    private double CurrentScroll()
        => SingleComposer?.GetScrollbar("scrollbar")?.CurrentYPosition ?? 0;

    private void OnScroll(float value)
    {
        panel?.ScrollToUnscaled(value);
        scrollByView[(int)view] = value;
    }

    // ---- the toggle rows ------------------------------------------------------------

    /// <summary>
    /// Where the switches page puts its parts inside a content box of a given size.
    ///
    /// Pure, because none of it can be checked from outside the game and all of it used to be
    /// laid out for one size and drawn at another: thirteen rows at a fixed 32-unit pitch need
    /// 460 units below a single row of group buttons, and the content box's floor is 443. The
    /// last switches and the whole message panel were then drawn below the frame - and with
    /// eight group buttons at their widest label, the button row itself ran past the right
    /// edge. Pitch, switch size and panel height therefore come out of the room there is.
    /// </summary>
    internal readonly struct TogglePage
    {
        /// <summary>The uniform button width the row was laid out with - the widest label's,
        /// capped so a long translation wraps the row instead of running off the edge.</summary>
        public double GroupWidth { get; init; }

        /// <summary>Group buttons that fit across the content, at that width.</summary>
        public int GroupsPerRow { get; init; }
        /// <summary>Rows the group buttons wrapped onto.</summary>
        public int GroupRows { get; init; }
        public double RowsTop { get; init; }
        public double RowPitch { get; init; }
        public double SwitchSize { get; init; }
        public double LabelLeft { get; init; }
        public double LabelWidth { get; init; }

        /// <summary>Monospace cells a row's text may use before it is cut.</summary>
        public int LabelCells { get; init; }
        public double PanelTop { get; init; }
        public double PanelHeight { get; init; }
        public int RowCount { get; init; }

        public double RowsBottom => RowsTop + RowPitch * RowCount;
        public double PanelBottom => PanelTop + PanelHeight;
    }

    private const double SidePad = 8;
    private const double GroupButtonHeight = 22;
    private const double GroupButtonGap = 6;
    private const double MaxRowPitch = 32;
    private const double MinRowPitch = 26;

    /// <summary>Below this a switch stops being a clickable target, so only a page that fits no
    /// other way goes here - and it goes here rather than off the bottom of the frame.</summary>
    private const double FloorRowPitch = 20;

    private const double PanelGap = 6;
    private const double PanelWant = 40;
    private const double PanelFloor = 24;

    /// <summary>
    /// Unscaled width of one cell of the 14 px monospace face the rows are drawn in - the same
    /// advance <see cref="ButtonWidth"/> sizes a label with, and the one
    /// <see cref="NominalColumns"/> comes from. A row's text is cut to what its box holds in
    /// these: the panel measures the real font against its own surface, but a row has no
    /// surface of its own to measure against.
    /// </summary>
    internal const double CellWidth = 8.4;

    /// <summary>Group buttons that must fit side by side whatever a translation does to their
    /// labels: two per row keeps eight groups to four rows, which is what still leaves the
    /// switches and their message panel inside the smallest content box.</summary>
    private const int MinGroupsPerRow = 2;

    internal static TogglePage LayOutToggles(double contentWidth, double contentHeight,
                                             int groupCount, double groupWidth, int rowCount)
    {
        groupWidth = Math.Clamp(groupWidth, CellWidth,
            Math.Max(CellWidth, (contentWidth - (MinGroupsPerRow + 1) * GroupButtonGap) / MinGroupsPerRow));
        var perRow = Math.Max(1, (int)((contentWidth - GroupButtonGap) / (groupWidth + GroupButtonGap)));
        var groupRows = Math.Max(1, (groupCount + perRow - 1) / perRow);
        var rowsTop = 4 + groupRows * (GroupButtonHeight + 4) + 14;

        var bottom = contentHeight - SidePad;
        var room = Math.Max(0, bottom - rowsTop);

        // The pitch is what is left over after the panel has had its say, clamped to what a
        // switch is still worth clicking; if even the tightest rows leave the panel below its
        // floor, the rows give way first - a message nobody can read is worse than a dense list.
        var pitch = MaxRowPitch;
        if (rowCount > 0)
        {
            pitch = Math.Clamp((room - PanelGap - PanelWant) / rowCount, MinRowPitch, MaxRowPitch);
            if (room - pitch * rowCount - PanelGap < PanelFloor)
                pitch = Math.Clamp((room - PanelGap - PanelFloor) / rowCount, FloorRowPitch, MaxRowPitch);
        }

        var panelTop = rowsTop + pitch * rowCount + PanelGap;
        var panelHeight = Math.Max(0, bottom - panelTop);

        var switchSize = Math.Clamp(pitch - 6, 14, 26);
        var labelLeft = SidePad + switchSize + 12;
        var labelWidth = Math.Max(CellWidth, contentWidth - labelLeft - SidePad);

        return new TogglePage
        {
            GroupWidth = groupWidth,
            GroupsPerRow = perRow,
            GroupRows = groupRows,
            RowsTop = rowsTop,
            RowPitch = pitch,
            SwitchSize = switchSize,
            LabelLeft = labelLeft,
            LabelWidth = labelWidth,
            LabelCells = Math.Max(4, (int)(labelWidth / CellWidth)),
            PanelTop = panelTop,
            PanelHeight = panelHeight,
            RowCount = rowCount,
        };
    }

    private void ComposeToggleRows(GuiComposer compo, ElementBounds content)
    {
        // One group at a time, chosen by the buttons at the top. All forty-odd switches at once
        // would need a scrolling container and would compose forty-odd elements on every flip;
        // a group is nine rows, composes in one go and is readable.
        // Everything here is placed against the content's own corner, not the background's:
        // these rows belong inside the inset, and the inset is not where the background starts.
        var ox = content.fixedX;
        var oy = content.fixedY;

        var groups = Enum.GetValues<ToggleGroup>();
        var groupWidth = 0.0;
        foreach (var g in groups) groupWidth = Math.Max(groupWidth, ButtonWidth(GroupName(g)) - 12);

        var page = LayOutToggles(content.fixedWidth, content.fixedHeight,
                                 groups.Length, groupWidth, mod.Toggles.CountIn(toggleGroup));
        groupWidth = page.GroupWidth;

        for (var i = 0; i < groups.Length; i++)
        {
            var captured = groups[i];
            var col = i % page.GroupsPerRow;
            var line = i / page.GroupsPerRow;
            compo.AddSmallButton(GroupName(captured),
                () => { toggleGroup = captured; recomposeWanted = true; return true; },
                ElementBounds.Fixed(ox + GroupButtonGap + col * (groupWidth + GroupButtonGap),
                    oy + 4 + line * (GroupButtonHeight + 4), groupWidth, GroupButtonHeight),
                captured == toggleGroup ? EnumButtonStyle.MainMenu : EnumButtonStyle.Normal,
                "grp" + (int)captured);
        }

        var font = CairoFont.WhiteSmallText().WithFont("monospace").WithFontSize(14f);
        var y = page.RowsTop;
        foreach (var e in mod.Toggles.InGroup(toggleGroup))
        {
            var entry = e;
            var unavailable = e.Unavailable?.Invoke();

            compo.AddSwitch(on => OnFlip(entry),
                ElementBounds.Fixed(ox + SidePad, oy + y, page.SwitchSize, page.SwitchSize),
                "sw" + e.Key, page.SwitchSize, 4);
            // Cut to one line rather than autobroken: the engine's static text element wraps to
            // the box WIDTH and then keeps drawing past the box HEIGHT, so a blocked switch's
            // reason - a whole sentence - was drawn straight across the row below it. The
            // sentence is one click away, in the panel under the rows.
            compo.AddStaticText(TextPanel.Ellipsize(Label(entry, unavailable), page.LabelCells), font,
                ElementBounds.Fixed(ox + page.LabelLeft, oy + y + (page.RowPitch - 24) / 2,
                    page.LabelWidth, 24));
            y += page.RowPitch;
        }

        // What the last flip said, in the words the chat command would have used - and where a
        // row's cut-off reason is readable in full.
        panel = new TextPanel(capi,
            ElementBounds.Fixed(ox + SidePad, oy + page.PanelTop,
                content.fixedWidth - 2 * SidePad, page.PanelHeight),
            font);
        compo.AddInteractiveElement(panel, "panel");
    }

    private static string GroupName(ToggleGroup g) => g switch
    {
        ToggleGroup.Culling => Loc.T("komet:gui-grp-culling", "Culling"),
        ToggleGroup.Rendering => Loc.T("komet:gui-grp-rendering", "Rendering"),
        ToggleGroup.Shadows => Loc.T("komet:gui-grp-shadows", "Shadows"),
        ToggleGroup.Chunks => Loc.T("komet:gui-grp-chunks", "Chunks"),
        ToggleGroup.Entities => Loc.T("komet:gui-grp-entities", "Entities"),
        ToggleGroup.Memory => Loc.T("komet:gui-grp-memory", "Memory"),
        ToggleGroup.Server => Loc.T("komet:gui-grp-server", "Server"),
        ToggleGroup.Diagnostics => Loc.T("komet:gui-grp-diag", "Diagnostics"),
        _ => g.ToString(),
    };

    /// <summary>
    /// A toggle row's label: the name, the word '.komet toggle' takes, and the two things that
    /// change how the row must be read - whether it changes what is DRAWN (safemode's set, the
    /// only rows a visual artefact is bisected among) and whether it can be flipped here at all.
    /// </summary>
    private static string Label(ToggleEntry e, string unavailable)
    {
        var s = e.Label + "  ." + e.Key;
        if (e.Visual) s += "  " + Loc.T("komet:gui-visual", "[draws]");
        if (unavailable != null) s += "  - " + unavailable;
        return s;
    }

    private bool OnFlip(ToggleEntry entry)
    {
        var blocked = entry.Unavailable?.Invoke();
        lastFlip = blocked ?? mod.Announce(entry.Flip());
        // The switch's own state is not the truth - half of these refuse to move (a warm-up
        // that needs the entity hold, a pipeline that disabled itself). Recomposing reads the
        // state back out of the systems, so the row can never disagree with the mod.
        recomposeWanted = true;
        return true;
    }

    // ---- the action buttons ---------------------------------------------------------

    private void AddActions(GuiComposer compo, ElementBounds left, ElementBounds right)
    {
        compo.AddSmallButton(Loc.T("komet:gui-close", "Close"), () => { TryClose(); return true; },
            right, EnumButtonStyle.Normal);

        // One width for the whole row, from the longest label. A text button sizes itself in
        // BeforeCalcBounds, which happens long after these bounds are built, so RightCopy would
        // chain from a width of zero and stack every button on top of the first one. Sizing
        // from the label is an estimate, but a uniform one: a translation that overruns it
        // widens every button in the row rather than overlapping the next.
        var actions = Actions();
        var width = 0.0;
        foreach (var (text, _) in actions) width = Math.Max(width, ButtonWidth(text));

        // Built from Fixed rather than copied from `left`: that one carries the padding a
        // self-sizing button wants, and padding on a bounds whose width is already fixed adds
        // to the outer box - every button would overlap the next by its own padding.
        // FixedUnder resolved `left.fixedY` to a number when it was built, so it can be reused.
        var x = 4.0;
        foreach (var (text, action) in actions)
        {
            var run = action;
            compo.AddSmallButton(text, () => { run(); return true; },
                ElementBounds.Fixed(x, left.fixedY, width, 22), EnumButtonStyle.Normal);
            x += width + 10;
        }
    }

    /// <summary>Room for a button's label, in unscaled pixels. Deliberately generous - the
    /// cost of overestimating is a wide button, the cost of underestimating is two of them
    /// on top of each other.</summary>
    private static double ButtonWidth(string text) => Math.Max(70, text.Length * 8.4 + 22);

    /// <summary>
    /// What the buttons under this view do. Every one of them calls the method the chat command
    /// calls - the window is a second way in, never a second implementation.
    /// </summary>
    private List<(string text, Action run)> Actions()
    {
        var list = new List<(string, Action)>(4);

        switch (view)
        {
            case KometView.Stress:
                if (StressTest.Running)
                    list.Add((Loc.T("komet:gui-stress-stop", "Stop"), () => Say(mod.CmdStress("stop"))));
                else
                    list.Add((Loc.T("komet:gui-stress-start", "Start"), () => Say(mod.CmdStress(null))));
                break;

            case KometView.Conflicts:
                list.Add((Loc.T("komet:gui-rescan", "Rescan"), () => { mod.CmdConflicts(); recomposeWanted = true; }));
                break;

            case KometView.Hitches:
                list.Add((Loc.T("komet:gui-clear", "Clear"), () => { mod.CmdHitch("reset"); recomposeWanted = true; }));
                break;

            case KometView.Mods:
                list.Add((Loc.T("komet:gui-rescan", "Rescan"), () => { mod.CmdMods("reset"); recomposeWanted = true; }));
                break;

            case KometView.Report:
                list.Add((Loc.T("komet:gui-report-log", "To the log"), () => Say(mod.CmdReport())));
                list.Add((Loc.T("komet:gui-copy", "Copy"), CopyReport));
                break;
        }

        // On every view, because both answer a question that is asked from every view: is any
        // of this the mod's doing, and do these counters still describe the scene I am in.
        list.Add((Loc.T("komet:gui-safemode", "Safemode"), () => Say(mod.CmdSafeMode())));
        list.Add((Loc.T("komet:gui-reset", "Reset counters"), () => { mod.CmdReset(); recomposeWanted = true; }));
        return list;
    }

    private void Say(string text)
    {
        lastFlip = text;
        if (!string.IsNullOrEmpty(text)) capi.ShowChatMessage(text);
        recomposeWanted = true;
    }

    private void CopyReport()
    {
        try
        {
            capi.Forms.SetClipboardText(mod.ReportText());
            capi.ShowChatMessage(Loc.T("komet:gui-copied", "report copied to the clipboard."));
        }
        catch (Exception e)
        {
            // A refused clipboard (no display server, a locked-down session) must not look like
            // a broken report: the log always has the full text anyway.
            capi.Logger.Warning("komet window: clipboard refused: {0}", e.Message);
            capi.ShowChatMessage(Loc.T("komet:gui-copy-failed",
                "the clipboard refused - the full report is in client-main.log."));
        }
    }

    // ---- the refresh ----------------------------------------------------------------

    public override void OnRenderGUI(float dt)
    {
        if (recomposeWanted)
        {
            recomposeWanted = false;
            // Same rule as the overlay: an instrument that reports performance problems must
            // never become one, and it must never take the game down. A composition that fails
            // closes the window and says why once.
            try
            {
                Compose();
            }
            catch (Exception e)
            {
                capi.Logger.Error("komet window: composing view {0} failed, closing it:\n{1}", view, e);
                TryClose();
                return;
            }
        }

        // A resized window (or a GUI scale change) changes what the dialog may occupy. Compared
        // on the computed content size rather than on the raw frame size, so dragging a window
        // edge rebuilds the composition only when the size it would get actually changes.
        if (ContentSize() != composedFor) recomposeWanted = true;

        accum += dt;
        if (accum >= interval)
        {
            accum = 0;
            Refresh();
        }

        // Cheap and unconditional: a no-op unless the text or the scroll position changed, so
        // dragging the scrollbar redraws on the very next frame instead of waiting for the
        // refresh cadence.
        panel?.Redraw();
        base.OnRenderGUI(dt);
    }

    private void Refresh()
    {
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            // The rows are formatted to a fixed number of monospace cells, and the number the
            // writers default to is the F7 overlay's - which sizes its own box to whatever it
            // produced. This panel cannot grow, so it says how wide it is and the pages are
            // laid out for it: full-width section rules, and a label column that fits a
            // renderer's profiling name instead of cutting three different ones to the same
            // thirteen characters.
            using var wide = DebugHud.WideText(panel?.Columns ?? 0);

            if (view == KometView.Toggles)
            {
                foreach (var e in mod.Toggles.InGroup(toggleGroup))
                {
                    var sw = SingleComposer?.GetSwitch("sw" + e.Key);
                    if (sw != null) sw.On = e.IsOn();
                }

                // The last flip's own sentence when there is one - the answer to a click the
                // player just made outranks the summary - and the page's summary otherwise.
                if (lastFlip != null)
                {
                    panel?.SetText(lastFlip);
                }
                else
                {
                    sb.Clear();
                    mod.ComposeView(view, sb, this);
                    panel?.SetText(sb.ToString());
                }
            }
            else if (panel != null)
            {
                sb.Clear();
                mod.ComposeView(view, sb, this);
                panel.SetText(sb.ToString());

                // Only when the content's height actually changed: SetHeights rasters the
                // scrollbar handle, and doing that four times a second for a number that has
                // not moved is the kind of cost this window is supposed to find, not cause.
                if (panel.LineCount != lastLineCount)
                {
                    lastLineCount = panel.LineCount;
                    var visible = (float)panel.VisibleHeightUnscaled;
                    SingleComposer?.GetScrollbar("scrollbar")
                        ?.SetHeights(visible, (float)Math.Max(panel.ContentHeightUnscaled, visible));
                }

                if (view == KometView.Frametime) graph?.Redraw();
            }
        }
        catch (Exception e)
        {
            // A window that reports performance problems must never become one, and it must
            // never take the game down. Same rule as the overlay: log once, keep going.
            capi.Logger.Error("komet window: refresh of view {0} failed:\n{1}", view, e);
            panel?.SetText("this view failed to compose - see client-main.log");
        }

        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        avgRefreshMs = avgRefreshMs <= 0 ? ms : avgRefreshMs + (ms - avgRefreshMs) * 0.2;

        // Booked to the overlay column, because that is what this is: a frame this window
        // spikes must be attributable, not disappear into "outside the stages".
        FrameStats.AddHudMs(ms);

        // Same rule the overlay uses for its own cadence: spend at most a few percent of wall
        // time on the instrument, whatever the machine.
        //
        // The raster is part of that price and used to be left out of it. Composing a page is
        // 1-2 ms of CPU, but the raster ENDS in a texture upload, and an upload into a texture
        // the GPU is still reading blocks the render thread - on a GPU at 80 % busy a field log
        // shows that at 10-17 ms. The cadence was reading the 1-2 ms and concluding the window
        // was cheap. It now backs off from what the window actually costs.
        interval = (float)DebugHud.NextIntervalSeconds(avgRefreshMs + (panel?.AvgRasterMs ?? 0));
    }

    /// <summary>What this window costs per refresh and how often it refreshes - the two figures
    /// the overview prints about itself. An instrument that will not say its own price is one
    /// more unmeasured thing in the frame.</summary>
    internal (double refreshMs, double rasterMs, double intervalSeconds) OwnCost()
        => (avgRefreshMs, panel?.AvgRasterMs ?? 0, interval);

    public override void Dispose()
    {
        base.Dispose();
        ClearComposers();
        panel = null;
        graph = null;
    }
}
