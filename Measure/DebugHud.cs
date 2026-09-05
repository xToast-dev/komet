using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Measure;

/// <summary>
/// On-screen performance overlay, shared by the optimising mod and the vanilla baseline so
/// both report the same things the same way. A mod can append its own section through
/// <see cref="ExtraSection"/>.
///
/// It is also the overlay MACHINERY for the second HUD: <see cref="Komet.Measure.ModHud"/>
/// derives from this and replaces only <see cref="ComposeText"/> and <see cref="SampleWorld"/>.
/// Everything that was hard to get right here - the off-thread raster, the state machine that
/// keeps a view change from flashing the previous view, the adaptive rebuild interval, the
/// "an overlay must never become the stutter it reports" guard - is paid for once and holds
/// for both. What is per-overlay is per-instance (texture, surface, cadence); only the
/// conclusions that are about the MACHINE (cairo refused a worker thread, what a rebuild
/// costs here) stayed static.
///
/// Renders in the Ortho stage just under the GUI manager, so it sits above the world but
/// below dialogs. The text is regenerated a few times a second into a single reused texture -
/// rebuilding it every frame would cost more than everything it reports.
/// </summary>
public class DebugHud : IRenderer
{
    public delegate void SectionWriter(StringBuilder sb, double frameMs);

    private const int LabelWidth = 13;
    private const int ValueWidth = 9;
    private static readonly string Rule = new('─', 48);

    /// <summary>ClientMain.chunkRenderer and ClientWorldMap.chunks are internal.</summary>
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> ChunkRendererRef =
        AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> ChunksRef =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");

    private readonly ICoreClientAPI capi;
    private readonly string title;

    /// <summary>The overlay's heading - subclasses compose their own text and need it.</summary>
    protected string Title => title;

    /// <summary>Draw against the left screen edge instead of the right one. Two overlays on
    /// the same edge would sit on top of each other, and the mod HUD is the one that moves:
    /// the performance HUD's corner is what every screenshot in this project shows.</summary>
    protected bool AnchorLeft;

    /// <summary>Extra rows appended after the built-in sections (full view only).</summary>
    public SectionWriter ExtraSection;

    /// <summary>The few rows that must be visible even in the compact view - the mod's
    /// !!-warnings (safemode, stress test, armed diagnostics). A player glancing at the
    /// small HUD must never mistake a safemode session for a normal one.</summary>
    public SectionWriter ExtraCompactSection;

    /// <summary>
    /// The reduced player view: what is my frame, is the GPU the wall, does it hitch, is the
    /// GC involved, is the world still loading - and nothing else. F7 cycles
    /// aus -> kompakt -> voll; the full view is the diagnostic instrument, the compact one
    /// is for playing. Full is unchanged in content, so screenshots stay comparable.
    ///
    /// A property, because a view change must invalidate the texture: the text rebuild runs
    /// on a timer, and a plain field left the OLD view on screen for up to a rebuild interval
    /// after F7 - the full HUD visibly flashing before the compact one appeared.
    /// </summary>
    public bool Compact
    {
        get => compact;
        set { if (compact != value) { compact = value; dirty = true; } }
    }
    private bool compact = true;

    public bool Visible
    {
        get => visible;
        set { if (visible != value) { visible = value; dirty = true; } }
    }
    private bool visible;

    /// <summary>State changed (F7, a command, first show): the very next rendered frame must
    /// show exactly the new state, never the previous one's pixels. Internal so verify can
    /// pin that the property setters actually set it - the forgotten invalidation WAS the
    /// reported flicker.</summary>
    internal bool dirty = true;

    /// <summary>
    /// Raster the text on a worker instead of the render thread. The full rebuild used to run
    /// inside the ortho stage and its cost landed in a single frame - a field log booked
    /// "hud 3,0 / 3,1 / 7,7" as hitch shares. Off-thread, the frame pays only for sampling,
    /// composing and the GL upload. Static because there is one overlay per process.
    /// </summary>
    public static bool BackgroundRaster = true;

    /// <summary>An off-thread raster threw once (cairo built without threads?): stay on the
    /// synchronous path for the rest of the session instead of failing four times a second.
    /// Static like the interval - one overlay per process, and Compose() reports it.</summary>
    private static bool rasterBroken;

    private System.Threading.Tasks.Task rasterTask;
    private double pendingMainMs, pendingRasterMs;

    private LoadedTexture texture;
    private CairoFont font;
    private readonly TextBackground background;
    private float accum;
    private string lastText = "";
    private int failures;

    // Raster target, reused across rebuilds at a session high-water size. A fresh surface per
    // rebuild would be tolerable; what is not is handing the driver a new width every time -
    // LoadOrUpdateCairoTexture deletes and recreates the GL texture on any size change, and
    // the widest HUD line contains live numbers, so the natural width jitters every rebuild.
    // A stable size keeps the upload on the cheap glTexSubImage2D path.
    private ImageSurface surface;
    private Context ctx;

    // Font metrics probed once (each probe pays a full cairo font-map lookup); re-probed only
    // when the GUI scale changes, which is what they depend on.
    private double charAdvance;   // one monospace cell
    private double ruleWidth;     // the '─' rule line - box-drawing glyphs may fall back wider
    private double ascent;
    private int lineHeight;
    private float metricsScale = -1;

    /// <summary>How long until this overlay rebuilds its text, adapted to what a rebuild costs
    /// (see <see cref="NextIntervalSeconds"/>). Per instance: the mod HUD's text is more
    /// expensive to compose than the performance HUD's, and it must not slow the other one down.</summary>
    private float rebuildInterval = 0.25f;

    /// <summary>This overlay's own smoothed rebuild cost - what its cadence is derived from.</summary>
    private double avgRebuildMs;

    /// <summary>The cadence the report row prints, from whichever overlay last rebuilt. The row
    /// is about the machine ("a rebuild costs 40 ms here"), not about one of the two boxes.</summary>
    private static float lastInterval = 0.25f;

    /// <summary>What one text rebuild (world sample + Cairo raster + texture upload) costs
    /// on THIS machine, smoothed. On the dev box it is ~1-2 ms; a Windows tester's i7-4770 +
    /// RX 570 measured ~40 ms - at the fixed 4 Hz cadence that alone was ~3 booked hitches
    /// per second, all in ortho, all "im stand". The overlay must never be the stutter it
    /// exists to report, hence <see cref="NextIntervalSeconds"/>.</summary>
    public static double AvgRebuildMs { get; private set; }
    public static long StatRebuilds { get; private set; }

    /// <summary>The GL-upload share of <see cref="AvgRebuildMs"/>, so a tester's HUD row says
    /// whether a slow rebuild is the cairo raster or the driver taking the texture.</summary>
    public static double AvgUploadMs { get; private set; }

    private long lastDrawCalls;
    private int drawCallsPerFrame;
    private long vramBytes;
    private long renderedTris, allocatedTris;
    private int poolCount;
    private float fragmentation;
    private int loadedChunks;

    /// <summary>Draw calls issued in the last rendered frame.</summary>
    public int DrawCallsPerFrame => drawCallsPerFrame;

    /// <summary>Mesh pools the chunk renderer holds - the divisor for any per-sweep figure.</summary>
    public int PoolCount => poolCount;

    /// <summary>Triangles the chunk renderer actually submits, and how many it holds.</summary>
    public long RenderedTriangles => renderedTris;

    /// <summary>Just below the GUI manager at 1.0, so dialogs draw over it.</summary>
    public double RenderOrder => 0.97;
    public int RenderRange => 0;

    public DebugHud(ICoreClientAPI capi, string title)
    {
        this.capi = capi;
        this.title = title;

        // Texture and font are created lazily on the first rendered frame, not here: the
        // LoadedTexture needs a live API (LoadOrUpdateCairoTexture dereferences it without a
        // null check, so it must exist before the first upload) and CairoFont's static
        // initialiser needs a running game - while the F7 state machine is exercised by
        // verify on an instance that has neither.

        background = new TextBackground
        {
            FillColor = new[] { 0.0, 0.0, 0.0, 0.62 },
            Padding = 7,
            Radius = 2.0,
            BorderColor = new[] { 0.35, 0.35, 0.35, 0.7 },
            BorderWidth = 1.0
        };
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (!Visible) return;

        // An overlay that reports performance problems must never become one. Anything that
        // goes wrong in here turns the HUD off instead of taking the game down with it.
        try
        {
            RenderInner(dt);
        }
        catch (Exception e)
        {
            if (++failures == 1) capi.Logger.Error("{0} HUD failed, switching it off:\n{1}", title, e);
            if (failures >= 3) Visible = false;
        }
    }

    /// <summary>What the state machine decides to do in one rendered frame. Public surface is
    /// the enum only; the transition rule is <see cref="NextStep"/>.</summary>
    internal enum Step { Draw, RebuildNow, WaitInvisible, Upload, Start }

    /// <summary>
    /// The per-frame decision, pure so verify can pin it. The invariant that fixes the F7
    /// flicker lives here: while <paramref name="dirty"/> is set the texture still holds the
    /// PREVIOUS state's pixels, so the only legal outcomes are an immediate synchronous
    /// rebuild or - if a background raster is still painting the old state - drawing nothing
    /// at all. Plain Draw is never legal on a dirty frame.
    /// </summary>
    internal static Step NextStep(bool dirty, bool rasterInFlight, bool rasterDone,
                                  float accum, float interval, bool hasTexture)
    {
        if (dirty) return rasterInFlight && !rasterDone ? Step.WaitInvisible : Step.RebuildNow;
        if (rasterInFlight) return rasterDone ? Step.Upload : Step.Draw;
        if (!hasTexture || accum >= interval) return Step.Start;
        return Step.Draw;
    }

    /// <summary>The F7 cycle: aus -> kompakt (player view) -> voll (diagnostic) -> aus.
    /// Pure; the caller assigns the result to the properties, which self-invalidate.</summary>
    public static (bool visible, bool compact) CycleF7(bool visible, bool compact)
        => !visible ? (true, true) : compact ? (true, false) : (false, compact);

    private void RenderInner(float dt)
    {
        // draw calls only ever increment, so the per-frame count is the delta. The vanilla
        // debug screen zeroes it, which would make the delta negative - ignore those frames.
        long now = RuntimeStats.drawCallsCount;
        var delta = now - lastDrawCalls;
        if (delta >= 0 && delta < 1_000_000) drawCallsPerFrame = (int)delta;
        lastDrawCalls = now;

        accum += dt;
        texture ??= new LoadedTexture(capi);
        font ??= CairoFont.WhiteSmallText().WithFont("monospace").WithFontSize(15f);

        switch (NextStep(dirty, rasterTask != null, rasterTask?.IsCompleted ?? false,
                         accum, rebuildInterval, texture.TextureId != 0))
        {
            case Step.WaitInvisible:
                // F7 with a raster mid-paint: the finished texture would show the OLD view.
                // A frame or two of no HUD is invisible; a frame of the wrong HUD was the
                // flicker this state machine exists to kill.
                return;
            case Step.RebuildNow:
                if (rasterTask != null)
                {
                    // completed, but composed for the state before the change - discard the
                    // output and force the repaint (lastText already holds ITS text)
                    rasterTask = null;
                    lastText = "";
                }
                RebuildSync();
                dirty = false;
                accum = 0;
                break;
            case Step.Upload:
                FinishRaster();
                break;
            case Step.Start:
                accum = 0;
                StartRebuild();
                break;
        }

        if (texture.TextureId == 0) return;

        float x = AnchorLeft ? 8 : capi.Render.FrameWidth - texture.Width - 8;
        capi.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, x, 8, texture.Width, texture.Height);
    }

    /// <summary>The whole rebuild in one frame - the F7 path, where "exactly the new state,
    /// this frame" outranks spreading the cost. A keypress happens a few times a session;
    /// the recurring 4 Hz refresh goes through <see cref="StartRebuild"/> instead.</summary>
    private void RebuildSync()
    {
        var t0 = Stopwatch.GetTimestamp();
        SampleWorld();
        ProbeMetrics();
        var text = ComposeText();
        if (text != lastText || texture.TextureId == 0)
        {
            lastText = text;
            if (Layout(text, out var lines, out var width, out var height))
            {
                EnsureSurface(width, height);
                Raster(lines, width, height);
                Upload();
            }
        }
        var ms = ElapsedMs(t0);
        FrameStats.AddHudMs(ms);
        FoldRebuild(ms);
    }

    /// <summary>
    /// The recurring refresh: sample and compose on the render thread (they read engine and
    /// stats state), then hand the cairo raster to a worker. The texture keeps showing the
    /// previous numbers for the few frames the paint takes - unnoticeable at a 4 Hz cadence,
    /// and the frame no longer pays for the raster at all.
    /// </summary>
    private void StartRebuild()
    {
        var t0 = Stopwatch.GetTimestamp();
        SampleWorld();
        ProbeMetrics();
        var text = ComposeText();
        if (text == lastText && texture.TextureId != 0)
        {
            var ms0 = ElapsedMs(t0);
            FrameStats.AddHudMs(ms0);
            FoldRebuild(ms0);
            return;
        }
        lastText = text;
        if (!Layout(text, out var lines, out var width, out var height)) return;
        EnsureSurface(width, height);

        if (!BackgroundRaster || rasterBroken)
        {
            Raster(lines, width, height);
            Upload();
            var total = ElapsedMs(t0);
            FrameStats.AddHudMs(total);
            FoldRebuild(total);
            return;
        }

        var mainMs = ElapsedMs(t0);
        FrameStats.AddHudMs(mainMs);
        pendingMainMs = mainMs;
        // The task owns surface and ctx until it completes. Nothing else can touch them in
        // between: NextStep only allows Start and RebuildNow when no raster is in flight.
        rasterTask = System.Threading.Tasks.Task.Run(() =>
        {
            var r0 = Stopwatch.GetTimestamp();
            Raster(lines, width, height);
            pendingRasterMs = ElapsedMs(r0);
        });
    }

    /// <summary>The main-thread tail of a background raster: hand the pixels to the driver.</summary>
    private void FinishRaster()
    {
        var t = rasterTask;
        rasterTask = null;
        if (t.IsFaulted)
        {
            // cairo (or its font map) refused the worker thread on this platform - stay
            // synchronous for the session and repaint, so the HUD never freezes on stale text
            rasterBroken = true;
            lastText = "";
            capi.Logger.Warning("{0} HUD: background raster failed, synchronous from now on: {1}",
                title, t.Exception?.GetBaseException()?.Message);
            return;
        }
        var uploadMs = Upload();
        FrameStats.AddHudMs(uploadMs);
        FoldRebuild(pendingMainMs + pendingRasterMs + uploadMs);
    }

    /// <summary>Total cost of one rebuild, wherever its parts ran - what the adaptive
    /// interval paces on, so a slow machine backs off whether or not it rasters off-thread.</summary>
    private void FoldRebuild(double totalMs)
    {
        avgRebuildMs = avgRebuildMs <= 0 ? totalMs : avgRebuildMs * 0.8 + totalMs * 0.2;
        AvgRebuildMs = AvgRebuildMs <= 0 ? totalMs : AvgRebuildMs * 0.8 + totalMs * 0.2;
        StatRebuilds++;
        rebuildInterval = (float)NextIntervalSeconds(avgRebuildMs);
        lastInterval = rebuildInterval;
    }

    private static double ElapsedMs(long fromTimestamp)
        => (Stopwatch.GetTimestamp() - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Seconds until the next text rebuild, from what a rebuild costs here: 25x the cost, so
    /// the overlay spends at most ~4 % of wall time on itself whatever the hardware. Floored
    /// at the original 4 Hz (fast machines lose nothing) and capped at 2 s (the display must
    /// stay readable as a live figure even where a rebuild is expensive).
    /// </summary>
    public static double NextIntervalSeconds(double avgCostMs)
        => Math.Clamp(avgCostMs * 0.025, 0.25, 2.0);

    /// <summary>Walks every mesh pool, so only worth doing a few times a second. Virtual: an
    /// overlay that reports something other than the terrain has nothing to sample here.</summary>
    protected virtual void SampleWorld()
    {
        try
        {
            if (capi.World is not ClientMain game) return;

            // The pool walk (GetStats + CalcFragmentation over every mesh pool) feeds rows
            // only the full view shows. The compact view - the view people actually play
            // with - earns its smallness by not paying for them either.
            if (!Compact)
            {
                var renderer = ChunkRendererRef(game);
                if (renderer != null)
                {
                    renderer.GetStats(out var used, out var rendered, out var allocated);
                    vramBytes = used;
                    renderedTris = rendered;
                    allocatedTris = allocated;
                    poolCount = renderer.QuantityModelDataPools();
                    fragmentation = renderer.CalcFragmentation();
                }

                if (game.WorldMap != null) loadedChunks = ChunksRef(game.WorldMap)?.Count ?? 0;
            }
            // The per-second rates (GC, allocation, CPU, tesselation) fold at the frame
            // boundary now - see FrameStats.SampleIntervalSeconds - so the overlay only reads
            // them. Folding them here meant a report with the overlay off printed zeros.
        }
        catch
        {
            vramBytes = 0;
        }
    }

    /// <summary>
    /// Font metrics, re-probed only when the GUI scale changes. Must run before Compose():
    /// it reads BarAscii, or the first text would carry bars the box was not sized for.
    /// Render thread only - it talks to the font map.
    /// </summary>
    private void ProbeMetrics()
    {
        if (metricsScale == RuntimeEnv.GUIScale) return;
        charAdvance = font.GetTextExtents("0000000000").Width / 10.0;
        ruleWidth = font.GetTextExtents(Rule).Width;
        // The share bars assume the block glyphs advance exactly one monospace cell.
        // A font that substitutes them from a differently-sized fallback would make
        // bar-carrying lines wider than the computed raster - probed once, and the bars
        // degrade to '#' rather than overflowing the box.
        var barAdvance = font.GetTextExtents("████").Width / 4.0;
        BarAscii = charAdvance <= 0 || Math.Abs(barAdvance - charAdvance) > charAdvance * 0.05;
        var fe = font.GetFontExtents();
        ascent = fe.Ascent;
        lineHeight = (int)fe.Height;
        metricsScale = RuntimeEnv.GUIScale;
    }

    /// <summary>
    /// Splits the text into trimmed lines and sizes the box. The engine's
    /// GenOrUpdateTextTexture is unusable here: its autobreak layout measures every line word
    /// by word against the box width - each measurement a cairo call that re-selects the font
    /// face - which a Windows tester's box turned into ~40 ms per rebuild (1-2 ms on the dev
    /// machine). The HUD is pre-formatted monospace, so it needs none of that: size by
    /// character count, draw each line once.
    /// </summary>
    private bool Layout(string text, out string[] lines, out int width, out int height)
    {
        lines = text.Split('\n');
        var longest = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
            longest = Math.Max(longest, lines[i].Length);
        }
        width = (int)Math.Max(longest * charAdvance, ruleWidth) + 1 + 2 * background.HorPadding;
        height = lineHeight * lines.Length + 1 + 2 * background.VerPadding;
        return width >= 8 && height >= 8;
    }

    private void EnsureSurface(int width, int height)
    {
        (var surfW, var surfH) = NextSurfaceSize(
            surface?.Width ?? 0, surface?.Height ?? 0, width, height, lineHeight);
        if (surface != null && surfW == surface.Width && surfH == surface.Height) return;
        ctx?.Dispose();
        surface?.Dispose();
        surface = new ImageSurface(Format.Argb32, surfW, surfH);
        ctx = new Context(surface);
    }

    /// <summary>
    /// Paints box and text into the surface. Runs on the render thread (F7 path, fallback) or
    /// on a worker (recurring refresh) - never both at once, the state machine sequences it.
    /// </summary>
    private void Raster(string[] lines, int width, int height)
    {
        ctx.Operator = Operator.Clear;
        ctx.Paint();
        ctx.Operator = Operator.Over;

        // Content sits in the surface's right-bottom-padded corner: right-anchored so the box
        // hugs the screen edge even though the surface is wider than the text, top-anchored
        // with transparent (invisible) slack below.
        double xOff = AnchorLeft ? 0.0 : surface.Width - width;
        ctx.SetSourceRGBA(background.FillColor[0], background.FillColor[1],
            background.FillColor[2], background.FillColor[3]);
        GuiElement.RoundRectangle(ctx, xOff, 0.0, width, height, background.Radius);
        if (background.BorderWidth > 0.0)
        {
            ctx.FillPreserve();
            ctx.Operator = Operator.Atop;
            ctx.LineWidth = background.BorderWidth;
            ctx.SetSourceRGBA(background.BorderColor[0], background.BorderColor[1],
                background.BorderColor[2], background.BorderColor[3]);
            ctx.Stroke();
            ctx.Operator = Operator.Over;
        }
        else
        {
            ctx.Fill();
        }

        font.SetupContext(ctx);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            ctx.MoveTo(xOff + background.HorPadding, background.VerPadding + ascent + i * lineHeight);
            ctx.ShowText(lines[i]);
            if (font.RenderTwice) ctx.ShowText(lines[i]);
        }
    }

    /// <summary>Hands the surface to the driver. Render thread only (GL).</summary>
    private double Upload()
    {
        var t0 = Stopwatch.GetTimestamp();
        capi.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
        var uploadMs = ElapsedMs(t0);
        AvgUploadMs = AvgUploadMs <= 0 ? uploadMs : AvgUploadMs * 0.8 + uploadMs * 0.2;
        return uploadMs;
    }

    /// <summary>
    /// The raster surface's next size for the wanted content size: rounded up in coarse steps
    /// (64 px wide, four text lines tall) and never below what it already is. Live numbers make
    /// the content width jitter every rebuild; any returned change of size costs a GL texture
    /// delete + reallocate mid-frame, so almost every rebuild must land on the exact same
    /// answer and take the subimage-update path instead.
    /// </summary>
    public static (int w, int h) NextSurfaceSize(int haveW, int haveH, int contentW, int contentH, int lineHeight)
    {
        var stepH = Math.Max(1, lineHeight) * 4;
        var w = Math.Max(haveW, (contentW + 63) / 64 * 64);
        var h = Math.Max(haveH, (contentH + stepH - 1) / stepH * stepH);
        return (w, h);
    }

    // ---- formatting helpers, also used by the extra section ----

    public static string Ms(double ms) => ms.ToString("F2", CultureInfo.CurrentCulture).PadLeft(6) + " ms";
    public static string N(double v) => v.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Millions with one decimal - triangle counts would otherwise be the widest
    /// number on screen and stretch the whole box for digits nobody reads.</summary>
    public static string Mio(double v) => (v / 1_000_000.0).ToString("F1", CultureInfo.CurrentCulture);

    // ---- the share bar next to each frame bucket ----
    //
    // Ten cells represent the whole frame, so the frame-aufteilung block reads as a picture:
    // the eye compares bar lengths directly instead of parsing a column of percentages.
    // Partial cells use the Unicode block-element eighths; if the font's fallback for those
    // glyphs does not match the monospace cell width (probed once, next to the rule glyph),
    // the bar degrades to '#', which every monospace font gets right.

    private const int BarCells = 10;
    private static readonly char[] BarEighths = { ' ', '▏', '▎', '▍', '▌', '▋', '▊', '▉' };

    /// <summary>Set by the metrics probe when the block glyphs would break the raster.</summary>
    internal static bool BarAscii;

    /// <summary>Pure, so verify can pin the geometry: length in cells tracks ms/frame,
    /// clamped to one frame, and anything visible gets at least a sliver.</summary>
    internal static string Bar(double ms, double frameMs)
    {
        if (frameMs <= 0 || ms < 0.05) return "";
        var frac = Math.Min(1.0, ms / frameMs);
        var eighths = Math.Max(1, (int)Math.Round(frac * BarCells * 8));
        int full = eighths / 8, rem = eighths % 8;
        if (BarAscii) return new string('#', Math.Max(1, full + (rem >= 4 ? 1 : 0)));
        return rem > 0 ? new string('█', full) + BarEighths[rem] : new string('█', full);
    }

    /// <summary>One frame bucket: percent, milliseconds, bar, optional note after the bar.</summary>
    private static void BucketRow(StringBuilder sb, string label, double ms, double frame, string note = null)
    {
        var bar = Bar(ms, frame);
        var tail = note == null
            ? (bar.Length > 0 ? bar : null)
            : bar.PadRight(BarCells + 1) + note;
        Row(sb, label, Pct(ms, frame), Ms(ms), tail);
    }

    /// <summary>One aligned row: label, an optional value column, an optional millisecond column.</summary>
    public static void Row(StringBuilder sb, string label, string value = null, string ms = null, string tail = null)
    {
        sb.Append(' ').Append(label.PadRight(LabelWidth));
        sb.Append((value ?? "").PadLeft(ValueWidth));
        if (ms != null) sb.Append("  ").Append(ms);
        if (tail != null) sb.Append("  ").Append(tail);
        sb.Append('\n');
    }

    public static void Section(StringBuilder sb, string heading)
    {
        var head = "── " + heading + " ";
        sb.Append(head).Append('─', Math.Max(0, Rule.Length - head.Length)).Append('\n');
    }

    public static string Pct(double ms, double frameMs)
        => frameMs > 0 ? (100.0 * ms / frameMs).ToString("F0", CultureInfo.CurrentCulture) + " %" : null;

    /// <summary>
    /// The three biggest buckets of the frame MaxFrameMs refers to, e.g.
    /// "opaque 41,2 + tick 18,3 + draussen 8,1 ms | gc 12". Null until a worst frame exists.
    /// GC pause time is appended separately: a pause freezes whichever stage it lands in, so
    /// it explains an inflated bucket rather than adding to the sum.
    /// </summary>
    public static string WorstFrameTail()
    {
        // built four times a second at most, so the small allocation is irrelevant
        (string label, double ms)[] buckets =
        {
            ("before", FrameStats.WorstStageMs[(int)EnumRenderStage.Before]),
            ("shadow", FrameStats.WorstShadowMs),
            ("opaque", FrameStats.WorstStageMs[(int)EnumRenderStage.Opaque]),
            ("oit", FrameStats.WorstStageMs[(int)EnumRenderStage.OIT]),
            ("post", FrameStats.WorstPostComposeMs),
            ("ortho", FrameStats.WorstStageMs[(int)EnumRenderStage.Ortho]),
            ("done", FrameStats.WorstStageMs[(int)EnumRenderStage.Done]),
            ("tick", FrameStats.WorstGameTickMs),
            ("swap", FrameStats.WorstSwapMs),
            ("outside", Math.Max(0, FrameStats.WorstOutsideMs - FrameStats.WorstSwapMs)),
        };

        double total = 0;
        foreach ((_, var ms) in buckets) total += ms;
        if (total <= 0) return null;

        var sb = new StringBuilder(48);
        for (var rank = 0; rank < 3; rank++)
        {
            var best = -1;
            for (var i = 0; i < buckets.Length; i++)
                if (best < 0 || buckets[i].ms > buckets[best].ms) best = i;
            if (buckets[best].ms < 0.5) break;

            if (sb.Length > 0) sb.Append(" + ");
            sb.Append(buckets[best].label).Append(' ')
              .Append(buckets[best].ms.ToString("F1", CultureInfo.CurrentCulture));
            buckets[best].ms = 0;
        }
        if (sb.Length == 0) return null;
        sb.Append(" ms");

        if (FrameStats.WorstGcPauseMs > 0.5)
            sb.Append(" | gc ").Append(FrameStats.WorstGcPauseMs.ToString("F1", CultureInfo.CurrentCulture));

        return sb.ToString();
    }

    /// <summary>The text this overlay shows. Virtual so a second overlay can be a different
    /// instrument without a second copy of everything below it.</summary>
    protected virtual string ComposeText()
        => Compact
            ? ComposeCompact(title, ExtraCompactSection)
            : Compose(title, drawCallsPerFrame, ClientSettings.ViewDistance, vramBytes, poolCount,
                      fragmentation, loadedChunks, ExtraSection, renderedTris, allocatedTris);

    /// <summary>
    /// The compact view: six-ish rows a player can absorb mid-game. Everything here also
    /// exists in the full view - this is a selection, never a different measurement.
    /// </summary>
    public static string ComposeCompact(string title, SectionWriter warnings)
    {
        var sb = new StringBuilder(400);

        if (!FrameStats.HasData)
        {
            sb.Append(title).Append('\n');
            sb.Append(Loc.T("komet:hud-collecting", "collecting data ... ({0} frames)", FrameStats.TotalFrames));
            return sb.ToString();
        }

        var frame = FrameStats.AvgFrameMs;
        var fps = frame > 0 ? 1000.0 / frame : 0;
        var ci = CultureInfo.CurrentCulture;

        sb.Append(title).Append('\n');
        sb.Append(Rule, 0, 34).Append('\n');
        Row(sb, Loc.Hud("fps"), fps.ToString("F0", ci), Ms(frame));
        if (GpuFrameTimer.GpuMs > 0)
            Row(sb, Loc.Hud("gpu frame"), Pct(GpuFrameTimer.GpuMs, frame), Ms(GpuFrameTimer.GpuMs),
                GpuBusy.IsLimited(GpuFrameTimer.GpuMs, frame) ? Loc.T("komet:hud-gpu-limited", "GPU-LIMITED") : null);
        if (GpuBusy.Available)
            Row(sb, Loc.Hud("gpu load"), GpuBusy.Percent.ToString(ci) + " %", null, GpuBusy.Source);
        if (HitchLog.TotalHitches > 0)
        {
            Row(sb, Loc.Hud("hitches"), N(HitchLog.TotalHitches), null,
                HitchLog.PerMinute.ToString("F1", ci) + "/min");
            var lastHitch = HitchLog.LastTail();
            if (lastHitch != null) Row(sb, "  " + Loc.Hud("last"), null, null, lastHitch);
        }
        if (FrameStats.GcPauseMsPerSecond > 0.05)
            Row(sb, Loc.Hud("gc pauses"), null, Ms(FrameStats.GcPauseMsPerSecond), Loc.T("komet:hud-per-s", "per s"));
        // only while the world is actually loading - a finished world earns a smaller HUD
        if (Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation > 50
            || TesselationStats.ReceivedPerSecond > 0.5)
            Row(sb, Loc.Hud("loading"), TesselationStats.ChunksPerSecond.ToString("F0", ci) + "/s", null,
                Loc.T("komet:hud-queue", "queue {0}", N(Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation)));

        warnings?.Invoke(sb, frame);

        sb.Append(Loc.T("komet:hud-f7-hint", " F7: details, again: off"));
        return sb.ToString();
    }

    /// <summary>
    /// Pure text assembly, split out from the renderer so it can be exercised without a GL
    /// context.
    /// </summary>
    public static string Compose(string title, int drawCallsPerFrame, int viewDistance,
                                 long vramBytes, int poolCount, float fragmentation, int loadedChunks,
                                 SectionWriter extra, long renderedTris = 0, long allocatedTris = 0)
    {
        var sb = new StringBuilder(1400);

        if (!FrameStats.HasData)
        {
            sb.Append(title).Append('\n');
            sb.Append(Loc.T("komet:hud-collecting", "collecting data ... ({0} frames)", FrameStats.TotalFrames));
            return sb.ToString();
        }

        var frame = FrameStats.AvgFrameMs;
        var fps = frame > 0 ? 1000.0 / frame : 0;
        var ci = CultureInfo.CurrentCulture;

        sb.Append(title).Append(" · ").Append(Loc.T("komet:hud-averages", "averages")).Append('\n');
        sb.Append(Rule).Append('\n');

        // ---- how it runs, at a glance ----
        Row(sb, Loc.Hud("fps"), fps.ToString("F0", ci), Ms(frame));
        if (GpuFrameTimer.GpuMs > 0)
        {
            // The one comparison that settles CPU-bound vs GPU-bound: gpu >= cpu frame time
            // means the GPU is the wall and CPU work cannot move the framerate.
            var gpu = GpuFrameTimer.GpuMs;
            Row(sb, Loc.Hud("gpu frame"), Pct(gpu, frame), Ms(gpu), GpuBusy.IsLimited(gpu, frame) ? Loc.T("komet:hud-gpu-limited", "GPU-LIMITED") : null);
        }
        // The span above includes the GPU's idle gaps between submissions; this is what the
        // driver counted as busy. The two differ exactly when the CPU is the wall.
        if (GpuBusy.Available)
            Row(sb, Loc.Hud("gpu load"), GpuBusy.Percent.ToString(ci) + " %", null, Loc.T("komet:hud-busy", "busy ({0})", GpuBusy.Source));
        if (GpuFrameTimer.StageSamples > 0)
        {
            // the GPU's own split, next to the CPU-side stage rows further down
            var shadowGpu = GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone,
                EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone);
            var postGpu = GpuFrameTimer.StageSum(EnumRenderStage.AfterOIT, EnumRenderStage.AfterPostProcessing,
                EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit);
            Row(sb, Loc.Hud("gpu stages"), null, null,
                Loc.T("komet:hud-shadow-sp", "shadow ") + shadowGpu.ToString("F1", ci)
                + " · opaque " + GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Opaque].ToString("F1", ci)
                + " · oit " + GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.OIT].ToString("F1", ci)
                + " · post " + postGpu.ToString("F1", ci)
                + " · ortho " + GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Ortho].ToString("F1", ci) + " ms");
        }
        Row(sb, Loc.Hud("worst"), null, Ms(FrameStats.MaxFrameMs));
        // where the worst frame actually went - a hitch's cause is invisible in the smoothed
        // averages precisely because it is rare
        var worst = WorstFrameTail();
        if (worst != null) Row(sb, "  " + Loc.Hud("of which"), null, null, worst);
        // Every frame over the hitch threshold, attributed and split by camera movement -
        // the row that turns "es ruckelt beim drehen" into a countable statement.
        if (HitchLog.TotalHitches > 0)
        {
            Row(sb, Loc.Hud("hitches"), N(HitchLog.TotalHitches), null,
                HitchLog.PerMinute.ToString("F1", ci) + "/min, " + HitchLog.CommandHint);
            var lastHitch = HitchLog.LastTail();
            if (lastHitch != null) Row(sb, "  " + Loc.Hud("last"), null, null, lastHitch);
        }

        // ---- where the frame goes ----
        // Every bucket of the frame, in the hitch log's order and vocabulary, each with its
        // share of the frame drawn as a bar (ten cells = the whole frame). Including the game
        // tick and the outside-the-stages remainder, the block accounts for 100 % - the eye
        // compares bars instead of parsing a percent column.
        Section(sb, Loc.Hud("frame breakdown"));
        BucketRow(sb, Loc.Hud("before"), FrameStats.StageMs[(int)EnumRenderStage.Before], frame);
        // each cascade including its Done half, so nothing hides between the rows
        BucketRow(sb, Loc.Hud("shadow far"), FrameStats.StageMs[(int)EnumRenderStage.ShadowFar]
                                     + FrameStats.StageMs[(int)EnumRenderStage.ShadowFarDone], frame);
        BucketRow(sb, Loc.Hud("shadow near"), FrameStats.StageMs[(int)EnumRenderStage.ShadowNear]
                                    + FrameStats.StageMs[(int)EnumRenderStage.ShadowNearDone], frame);
        BucketRow(sb, Loc.Hud("opaque"), FrameStats.StageMs[(int)EnumRenderStage.Opaque], frame);
        BucketRow(sb, Loc.Hud("oit"), FrameStats.StageMs[(int)EnumRenderStage.OIT], frame);
        // AfterOIT through AfterBlit - SSAO, god rays, colour grading; a fifth of the frame
        // used to be invisible here
        BucketRow(sb, Loc.Hud("post/compose"), FrameStats.PostComposeMs, frame);
        BucketRow(sb, Loc.Hud("ortho (gui)"), FrameStats.StageMs[(int)EnumRenderStage.Ortho], frame);
        BucketRow(sb, Loc.Hud("done"), FrameStats.StageMs[(int)EnumRenderStage.Done], frame);
        BucketRow(sb, Loc.Hud("game tick"), FrameStats.GameTickMs, frame);
        // Whatever no stage and no tick accounts for: buffer swap, frame limiter, driver
        // back-pressure. Naming it stops it from being mistaken for measurement error.
        BucketRow(sb, Loc.Hud("outside"), FrameStats.OutsideStagesMs, frame,
            FrameStats.AvgSwapMs > 0.005
                ? Loc.T("komet:hud-of-which-swap", "of which swap {0}", FrameStats.AvgSwapMs.ToString("F2", ci))
                : Loc.T("komet:hud-swap-driver", "swap/driver"));

        // ---- gc ----
        // GC pauses stop every thread at once - the only mechanism that slows the render
        // thread, the tesselation thread and the occlusion worker by the same factor at the
        // same time. When this section is large, no renderer is guilty; the allocations are.
        Section(sb, Loc.Hud("gc"));
        if (FrameStats.GcPauseMsPerSecond > 0.05)
            Row(sb, Loc.Hud("gc pauses"), FrameStats.Gen0PerSecond.ToString("F0", ci) + "/s",
                Ms(FrameStats.GcPauseMsPerSecond),
                Loc.T("komet:hud-per-s-alloc", "per s · {0} MB/s alloc", FrameStats.AllocMbPerSecond.ToString("F0", ci))
                + (FrameStats.Gen2PerSecond > 0.05
                    ? " · gen2 " + FrameStats.Gen2PerSecond.ToString("F1", ci) + "/s"
                    : ""));
        // Where those bytes come from, thread by thread, so "the allocations are guilty"
        // has a next question to ask. Whatever no one measures stays visible as "rest"
        // instead of disappearing into the total - the share this row was built to expose
        // was exactly the unmeasured one.
        if (FrameStats.AllocMbPerSecond >= 32)
        {
            var tessAlloc = TesselationStats.AllocMbPerSecond;
            var unattributed = Math.Max(0.0,
                FrameStats.AllocMbPerSecond - FrameStats.MainAllocMbPerSecond
                - FrameStats.NetAllocMbPerSecond - FrameStats.PrefetchAllocMbPerSecond - tessAlloc);
            Row(sb, Loc.Hud("alloc sources"), "MB/s", null,
                Loc.T("komet:hud-net", "net ") + FrameStats.NetAllocMbPerSecond.ToString("F0", ci)
                + " · main " + FrameStats.MainAllocMbPerSecond.ToString("F0", ci)
                // prefetch is usually zero; it stays measured, but only earns screen width
                // when it has something to say
                + (FrameStats.PrefetchAllocMbPerSecond >= 0.5
                    ? " · prefetch " + FrameStats.PrefetchAllocMbPerSecond.ToString("F0", ci)
                    : "")
                + " · tess " + tessAlloc.ToString("F0", ci)
                + Loc.T("komet:hud-rest", " · rest ") + unattributed.ToString("F0", ci));
        }
        // The mode, stated without a verdict attached.
        //
        // This row used to nag whenever it saw workstation GC, on the strength of server GC
        // cutting total pause time from 131 to 6 ms/s. That reading was too narrow: a hitch is
        // about the longest single freeze, not the sum, and server GC buys its low total with
        // rare very long ephemeral pauses - one of 65 ms was measured here, in a gen0
        // collection, which no amount of background collection can help because ephemeral
        // collections are never background. Which mode is right is now an open measurement, so
        // the HUD reports and the hitch log judges (see HitchLog.GcModeVerdict), rather than
        // both of them assuming.
        Row(sb, Loc.Hud("gc mode"), System.Runtime.GCSettings.IsServerGC ? "server" : Loc.T("komet:hud-workstation", "workst."), null,
            HitchLog.WorstEphemeralPauseMs >= 1.0
                ? Loc.T("komet:hud-longest-ephemeral", "longest gen0/1 pause {0} ms", HitchLog.WorstEphemeralPauseMs.ToString("F0", ci))
                : null);

        // ---- the world and the loading pipeline ----
        Section(sb, Loc.Hud("world & loading"));
        // Not RuntimeStats.renderedTriangles: SystemRenderTerrain only fills those while the
        // engine's own debug screen is open, which is why the triangle figure once read "0 von 0".
        Row(sb, Loc.Hud("draw calls"), N(drawCallsPerFrame), null,
            Loc.T("komet:hud-triangles", "triangles {0} of {1} mio", Mio(renderedTris), Mio(allocatedTris)));
        Row(sb, Loc.Hud("entities"), N(RuntimeStats.renderedEntities));
        Row(sb, Loc.Hud("chunks"), N(loadedChunks), null,
            Loc.T("komet:hud-queue-2", "queue {0}/{1}",
                N(RuntimeStats.chunksAwaitingTesselation), N(RuntimeStats.chunksAwaitingPooling)));
        if (TesselationStats.TotalChunks > 0)
        {
            Row(sb, Loc.Hud("tesselation"), TesselationStats.ChunksPerSecond.ToString("F0", ci) + "/s",
                Ms(TesselationStats.MsPerChunk),
                Loc.T("komet:hud-per-chunk", "per chunk · {0} neighbours · {1} MB/s",
                    TesselationStats.NeighbourMsPerChunk.ToString("F1", ci),
                    TesselationStats.AllocMbPerSecond.ToString("F0", ci)));
            // arrival rate from the server - a low number here with an empty queue means the
            // wait is server-side (worldgen/sending), not this client
            Row(sb, Loc.Hud("received"), TesselationStats.ReceivedPerSecond.ToString("F0", ci) + "/s", null,
                Loc.T("komet:hud-from-server", "from server"));
        }
        if (vramBytes > 0)
            Row(sb, Loc.Hud("terrain vram"), N(vramBytes / 1048576.0) + " MB", null,
                Loc.T("komet:hud-pools-frag", "{0} pools · {1} % frag", poolCount, (fragmentation * 100f).ToString("F0", ci)));
        Row(sb, Loc.Hud("chunk upload"), null, Ms(FrameStats.AvgUploadMs),
            Loc.T("komet:hud-max", "max {0}", FrameStats.MaxUploadMs.ToString("F1", ci)));
        // Cores the whole process keeps busy. Low at idle is HEALTH, not waste - a frame is
        // a latency problem and the serial main thread caps it (Amdahl); this row is for
        // judging the streaming pipeline, where the workers should actually show up.
        if (FrameStats.CpuCoresBusy > 0.05)
            Row(sb, Loc.Hud("cpu cores"),
                (100.0 * FrameStats.CpuCoresBusy / Environment.ProcessorCount).ToString("F0", ci) + " %",
                null,
                Loc.T("komet:hud-cores-busy", "{0} of {1} cores busy",
                    FrameStats.CpuCoresBusy.ToString("F1", ci), Environment.ProcessorCount));
        Row(sb, Loc.Hud("view distance"), N(viewDistance), null, Loc.T("komet:hud-blocks", "blocks"));
        // The overlay's own price, so it can never again masquerade as an engine problem:
        // a Windows tester's ~40 ms Cairo rebuild at fixed 4 Hz WAS the ortho stutter.
        if (AvgRebuildMs >= 0.05)
            Row(sb, Loc.Hud("hud rebuild"), null, Ms(AvgRebuildMs),
                Loc.T("komet:hud-rebuild", "every {0} s · upload {1} ms",
                    lastInterval.ToString("0.##", ci), AvgUploadMs.ToString("F1", ci))
                + (BackgroundRaster && !rasterBroken ? Loc.T("komet:hud-raster-worker", " · raster in worker") : ""));

        extra?.Invoke(sb, frame);

        return sb.ToString().TrimEnd('\n');
    }

    public void Dispose()
    {
        // A raster still painting owns surface and ctx. Waiting a moment is fine here (world
        // leave, not a frame); if it is genuinely hung, leak both to the finaliser rather
        // than dispose them under the worker's brush.
        var rasterDone = true;
        try { rasterDone = rasterTask?.Wait(500) ?? true; } catch { /* faulted counts as done */ }
        rasterTask = null;

        texture?.Dispose();
        texture = null;
        if (rasterDone)
        {
            ctx?.Dispose();
            surface?.Dispose();
        }
        ctx = null;
        surface = null;
    }
}
