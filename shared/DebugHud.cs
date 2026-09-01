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
    /// </summary>
    public bool Compact = true;

    private LoadedTexture texture;
    private readonly CairoFont font;
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

    // Static because the text sections that report them are static; there is only ever one
    // overlay per process (the baseline refuses to load next to the mod).
    private static float rebuildInterval = 0.25f;

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

    public bool Visible;

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

        // LoadOrUpdateCairoTexture dereferences the target without a null check, so the
        // texture object has to exist before the first upload.
        texture = new LoadedTexture(capi);

        font = CairoFont.WhiteSmallText().WithFont("monospace").WithFontSize(15f);
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

    private void RenderInner(float dt)
    {
        // draw calls only ever increment, so the per-frame count is the delta. The vanilla
        // debug screen zeroes it, which would make the delta negative - ignore those frames.
        long now = RuntimeStats.drawCallsCount;
        long delta = now - lastDrawCalls;
        if (delta >= 0 && delta < 1_000_000) drawCallsPerFrame = (int)delta;
        lastDrawCalls = now;

        accum += dt;
        if (accum >= rebuildInterval || texture.TextureId == 0)
        {
            accum = 0;
            long t0 = Stopwatch.GetTimestamp();
            SampleWorld();
            Rebuild();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            AvgRebuildMs = AvgRebuildMs <= 0 ? ms : AvgRebuildMs * 0.8 + ms * 0.2;
            StatRebuilds++;
            FrameStats.AddHudMs(ms);
            rebuildInterval = (float)NextIntervalSeconds(AvgRebuildMs);
        }

        if (texture.TextureId == 0) return;

        float x = capi.Render.FrameWidth - texture.Width - 8;
        capi.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, x, 8, texture.Width, texture.Height);
    }

    /// <summary>
    /// Seconds until the next text rebuild, from what a rebuild costs here: 25x the cost, so
    /// the overlay spends at most ~4 % of wall time on itself whatever the hardware. Floored
    /// at the original 4 Hz (fast machines lose nothing) and capped at 2 s (the display must
    /// stay readable as a live figure even where a rebuild is expensive).
    /// </summary>
    public static double NextIntervalSeconds(double avgCostMs)
        => Math.Clamp(avgCostMs * 0.025, 0.25, 2.0);

    /// <summary>Walks every mesh pool, so only worth doing a few times a second.</summary>
    private void SampleWorld()
    {
        try
        {
            if (capi.World is not ClientMain game) return;

            ChunkRenderer renderer = ChunkRendererRef(game);
            if (renderer != null)
            {
                renderer.GetStats(out long used, out long rendered, out long allocated);
                vramBytes = used;
                renderedTris = rendered;
                allocatedTris = allocated;
                poolCount = renderer.QuantityModelDataPools();
                fragmentation = renderer.CalcFragmentation();
            }

            if (game.WorldMap != null) loadedChunks = ChunksRef(game.WorldMap)?.Count ?? 0;

            TesselationStats.Sample();
            FrameStats.SampleGc();
        }
        catch
        {
            vramBytes = 0;
        }
    }

    private void Rebuild()
    {
        // Metrics first: Compose() reads BarAscii, so the glyph probe has to have run before
        // the first text is built - or the first raster draws bars the box was not sized for.
        if (metricsScale != RuntimeEnv.GUIScale)
        {
            charAdvance = font.GetTextExtents("0000000000").Width / 10.0;
            ruleWidth = font.GetTextExtents(Rule).Width;
            // The share bars assume the block glyphs advance exactly one monospace cell.
            // A font that substitutes them from a differently-sized fallback would make
            // bar-carrying lines wider than the computed raster - probed once, and the bars
            // degrade to '#' rather than overflowing the box.
            double barAdvance = font.GetTextExtents("████").Width / 4.0;
            BarAscii = charAdvance <= 0 || Math.Abs(barAdvance - charAdvance) > charAdvance * 0.05;
            FontExtents fe = font.GetFontExtents();
            ascent = fe.Ascent;
            lineHeight = (int)fe.Height;
            metricsScale = RuntimeEnv.GUIScale;
        }

        string text = Compose();
        if (text == lastText && texture.TextureId != 0) return;
        lastText = text;

        // The engine's GenOrUpdateTextTexture is unusable here: its autobreak layout measures
        // every line word by word against the box width - each measurement a cairo call that
        // re-selects the font face - which a Windows tester's box turned into ~40 ms per
        // rebuild (1-2 ms on the dev machine). The HUD is pre-formatted monospace, so it needs
        // none of that: size by character count, draw each line once.
        string[] lines = text.Split('\n');
        int longest = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
            longest = Math.Max(longest, lines[i].Length);
        }

        int width = (int)Math.Max(longest * charAdvance, ruleWidth) + 1 + 2 * background.HorPadding;
        int height = lineHeight * lines.Length + 1 + 2 * background.VerPadding;
        if (width < 8 || height < 8) return;

        (int surfW, int surfH) = NextSurfaceSize(
            surface?.Width ?? 0, surface?.Height ?? 0, width, height, lineHeight);
        if (surface == null || surfW != surface.Width || surfH != surface.Height)
        {
            ctx?.Dispose();
            surface?.Dispose();
            surface = new ImageSurface(Format.Argb32, surfW, surfH);
            ctx = new Context(surface);
        }

        ctx.Operator = Operator.Clear;
        ctx.Paint();
        ctx.Operator = Operator.Over;

        // Content sits in the surface's right-bottom-padded corner: right-anchored so the box
        // hugs the screen edge even though the surface is wider than the text, top-anchored
        // with transparent (invisible) slack below.
        double xOff = surfW - width;
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
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            ctx.MoveTo(xOff + background.HorPadding, background.VerPadding + ascent + i * lineHeight);
            ctx.ShowText(lines[i]);
            if (font.RenderTwice) ctx.ShowText(lines[i]);
        }

        long t0 = Stopwatch.GetTimestamp();
        capi.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
        double uploadMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        AvgUploadMs = AvgUploadMs <= 0 ? uploadMs : AvgUploadMs * 0.8 + uploadMs * 0.2;
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
        int stepH = Math.Max(1, lineHeight) * 4;
        int w = Math.Max(haveW, (contentW + 63) / 64 * 64);
        int h = Math.Max(haveH, (contentH + stepH - 1) / stepH * stepH);
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
        double frac = Math.Min(1.0, ms / frameMs);
        int eighths = Math.Max(1, (int)Math.Round(frac * BarCells * 8));
        int full = eighths / 8, rem = eighths % 8;
        if (BarAscii) return new string('#', Math.Max(1, full + (rem >= 4 ? 1 : 0)));
        return rem > 0 ? new string('█', full) + BarEighths[rem] : new string('█', full);
    }

    /// <summary>One frame bucket: percent, milliseconds, bar, optional note after the bar.</summary>
    private static void BucketRow(StringBuilder sb, string label, double ms, double frame, string note = null)
    {
        string bar = Bar(ms, frame);
        string tail = note == null
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
        string head = "── " + heading + " ";
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
            ("schatten", FrameStats.WorstShadowMs),
            ("opaque", FrameStats.WorstStageMs[(int)EnumRenderStage.Opaque]),
            ("oit", FrameStats.WorstStageMs[(int)EnumRenderStage.OIT]),
            ("post", FrameStats.WorstPostComposeMs),
            ("ortho", FrameStats.WorstStageMs[(int)EnumRenderStage.Ortho]),
            ("done", FrameStats.WorstStageMs[(int)EnumRenderStage.Done]),
            ("tick", FrameStats.WorstGameTickMs),
            ("swap", FrameStats.WorstSwapMs),
            ("draussen", Math.Max(0, FrameStats.WorstOutsideMs - FrameStats.WorstSwapMs)),
        };

        double total = 0;
        foreach ((_, double ms) in buckets) total += ms;
        if (total <= 0) return null;

        var sb = new StringBuilder(48);
        for (int rank = 0; rank < 3; rank++)
        {
            int best = -1;
            for (int i = 0; i < buckets.Length; i++)
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

    private string Compose()
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
            sb.Append("sammelt Daten ... (").Append(FrameStats.TotalFrames).Append(" frames)");
            return sb.ToString();
        }

        double frame = FrameStats.AvgFrameMs;
        double fps = frame > 0 ? 1000.0 / frame : 0;
        CultureInfo ci = CultureInfo.CurrentCulture;

        sb.Append(title).Append('\n');
        sb.Append(Rule, 0, 34).Append('\n');
        Row(sb, "fps", fps.ToString("F0", ci), Ms(frame));
        if (GpuFrameTimer.GpuMs > 0)
            Row(sb, "gpu-frame", Pct(GpuFrameTimer.GpuMs, frame), Ms(GpuFrameTimer.GpuMs),
                GpuFrameTimer.GpuMs >= frame * 0.95 ? "GPU-LIMITIERT" : null);
        if (HitchLog.TotalHitches > 0)
        {
            Row(sb, "ruckler", N(HitchLog.TotalHitches), null,
                HitchLog.PerMinute.ToString("F1", ci) + "/min");
            string lastHitch = HitchLog.LastTail();
            if (lastHitch != null) Row(sb, "  zuletzt", null, null, lastHitch);
        }
        if (FrameStats.GcPauseMsPerSecond > 0.05)
            Row(sb, "gc-pausen", null, Ms(FrameStats.GcPauseMsPerSecond), "je s");
        // only while the world is actually loading - a finished world earns a smaller HUD
        if (Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation > 50
            || TesselationStats.ReceivedPerSecond > 0.5)
            Row(sb, "laden", TesselationStats.ChunksPerSecond.ToString("F0", ci) + "/s", null,
                "warteschlange " + N(Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation));

        warnings?.Invoke(sb, frame);

        sb.Append(" F7: details, noch mal: aus");
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
            sb.Append("sammelt Daten ... (").Append(FrameStats.TotalFrames).Append(" frames)");
            return sb.ToString();
        }

        double frame = FrameStats.AvgFrameMs;
        double fps = frame > 0 ? 1000.0 / frame : 0;
        CultureInfo ci = CultureInfo.CurrentCulture;

        sb.Append(title).Append(" · Mittelwerte\n");
        sb.Append(Rule).Append('\n');

        // ---- how it runs, at a glance ----
        Row(sb, "fps", fps.ToString("F0", ci), Ms(frame));
        if (GpuFrameTimer.GpuMs > 0)
        {
            // The one comparison that settles CPU-bound vs GPU-bound: gpu >= cpu frame time
            // means the GPU is the wall and CPU work cannot move the framerate.
            double gpu = GpuFrameTimer.GpuMs;
            Row(sb, "gpu-frame", Pct(gpu, frame), Ms(gpu), gpu >= frame * 0.95 ? "GPU-LIMITIERT" : null);
        }
        Row(sb, "schlechtester", null, Ms(FrameStats.MaxFrameMs));
        // where the worst frame actually went - a hitch's cause is invisible in the smoothed
        // averages precisely because it is rare
        string worst = WorstFrameTail();
        if (worst != null) Row(sb, "  davon", null, null, worst);
        // Every frame over the hitch threshold, attributed and split by camera movement -
        // the row that turns "es ruckelt beim drehen" into a countable statement.
        if (HitchLog.TotalHitches > 0)
        {
            Row(sb, "ruckler", N(HitchLog.TotalHitches), null,
                HitchLog.PerMinute.ToString("F1", ci) + "/min, " + HitchLog.CommandHint);
            string lastHitch = HitchLog.LastTail();
            if (lastHitch != null) Row(sb, "  zuletzt", null, null, lastHitch);
        }

        // ---- where the frame goes ----
        // Every bucket of the frame, in the hitch log's order and vocabulary, each with its
        // share of the frame drawn as a bar (ten cells = the whole frame). Including the game
        // tick and the outside-the-stages remainder, the block accounts for 100 % - the eye
        // compares bars instead of parsing a percent column.
        Section(sb, "frame-aufteilung");
        BucketRow(sb, "before", FrameStats.StageMs[(int)EnumRenderStage.Before], frame);
        // each cascade including its Done half, so nothing hides between the rows
        BucketRow(sb, "schatten fern", FrameStats.StageMs[(int)EnumRenderStage.ShadowFar]
                                     + FrameStats.StageMs[(int)EnumRenderStage.ShadowFarDone], frame);
        BucketRow(sb, "schatten nah", FrameStats.StageMs[(int)EnumRenderStage.ShadowNear]
                                    + FrameStats.StageMs[(int)EnumRenderStage.ShadowNearDone], frame);
        BucketRow(sb, "opaque", FrameStats.StageMs[(int)EnumRenderStage.Opaque], frame);
        BucketRow(sb, "oit", FrameStats.StageMs[(int)EnumRenderStage.OIT], frame);
        // AfterOIT through AfterBlit - SSAO, god rays, colour grading; a fifth of the frame
        // used to be invisible here
        BucketRow(sb, "post/compose", FrameStats.PostComposeMs, frame);
        BucketRow(sb, "ortho (gui)", FrameStats.StageMs[(int)EnumRenderStage.Ortho], frame);
        BucketRow(sb, "done", FrameStats.StageMs[(int)EnumRenderStage.Done], frame);
        BucketRow(sb, "game tick", FrameStats.GameTickMs, frame);
        // Whatever no stage and no tick accounts for: buffer swap, frame limiter, driver
        // back-pressure. Naming it stops it from being mistaken for measurement error.
        BucketRow(sb, "ausserhalb", FrameStats.OutsideStagesMs, frame,
            FrameStats.AvgSwapMs > 0.005
                ? "davon swap " + FrameStats.AvgSwapMs.ToString("F2", ci)
                : "swap/treiber");

        // ---- gc ----
        // GC pauses stop every thread at once - the only mechanism that slows the render
        // thread, the tesselation thread and the occlusion worker by the same factor at the
        // same time. When this section is large, no renderer is guilty; the allocations are.
        Section(sb, "gc");
        if (FrameStats.GcPauseMsPerSecond > 0.05)
            Row(sb, "gc-pausen", FrameStats.Gen0PerSecond.ToString("F0", ci) + "/s",
                Ms(FrameStats.GcPauseMsPerSecond),
                "je s · " + FrameStats.AllocMbPerSecond.ToString("F0", ci) + " MB/s alloc"
                + (FrameStats.Gen2PerSecond > 0.05
                    ? " · gen2 " + FrameStats.Gen2PerSecond.ToString("F1", ci) + "/s"
                    : ""));
        // Where those bytes come from, thread by thread, so "the allocations are guilty"
        // has a next question to ask. Whatever no one measures stays visible as "rest"
        // instead of disappearing into the total - the share this row was built to expose
        // was exactly the unmeasured one.
        if (FrameStats.AllocMbPerSecond >= 32)
        {
            double tessAlloc = TesselationStats.AllocMbPerSecond;
            double unattributed = Math.Max(0.0,
                FrameStats.AllocMbPerSecond - FrameStats.MainAllocMbPerSecond
                - FrameStats.NetAllocMbPerSecond - FrameStats.PrefetchAllocMbPerSecond - tessAlloc);
            Row(sb, "alloc-quellen", "MB/s", null,
                "netz " + FrameStats.NetAllocMbPerSecond.ToString("F0", ci)
                + " · main " + FrameStats.MainAllocMbPerSecond.ToString("F0", ci)
                // prefetch is usually zero; it stays measured, but only earns screen width
                // when it has something to say
                + (FrameStats.PrefetchAllocMbPerSecond >= 0.5
                    ? " · prefetch " + FrameStats.PrefetchAllocMbPerSecond.ToString("F0", ci)
                    : "")
                + " · tess " + tessAlloc.ToString("F0", ci)
                + " · rest " + unattributed.ToString("F0", ci));
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
        Row(sb, "gc-modus", System.Runtime.GCSettings.IsServerGC ? "server" : "workst.", null,
            HitchLog.WorstEphemeralPauseMs >= 1.0
                ? "längste gen0/1-pause " + HitchLog.WorstEphemeralPauseMs.ToString("F0", ci) + " ms"
                : null);

        // ---- the world and the loading pipeline ----
        Section(sb, "welt & laden");
        // Not RuntimeStats.renderedTriangles: SystemRenderTerrain only fills those while the
        // engine's own debug screen is open, which is why the triangle figure once read "0 von 0".
        Row(sb, "draw calls", N(drawCallsPerFrame), null,
            "dreiecke " + Mio(renderedTris) + " von " + Mio(allocatedTris) + " mio");
        Row(sb, "entities", N(RuntimeStats.renderedEntities));
        Row(sb, "chunks", N(loadedChunks), null,
            "warteschlange " + N(RuntimeStats.chunksAwaitingTesselation)
            + "/" + N(RuntimeStats.chunksAwaitingPooling));
        if (TesselationStats.TotalChunks > 0)
        {
            Row(sb, "tesselation", TesselationStats.ChunksPerSecond.ToString("F0", ci) + "/s",
                Ms(TesselationStats.MsPerChunk),
                "je chunk · " + TesselationStats.NeighbourMsPerChunk.ToString("F1", ci) + " nachbarn · "
                    + TesselationStats.AllocMbPerSecond.ToString("F0", ci) + " MB/s");
            // arrival rate from the server - a low number here with an empty queue means the
            // wait is server-side (worldgen/sending), not this client
            Row(sb, "empfangen", TesselationStats.ReceivedPerSecond.ToString("F0", ci) + "/s", null,
                "vom server");
        }
        if (vramBytes > 0)
            Row(sb, "terrain vram", N(vramBytes / 1048576.0) + " MB", null,
                poolCount + " pools · " + (fragmentation * 100f).ToString("F0", ci) + " % frag");
        Row(sb, "chunk upload", null, Ms(FrameStats.AvgUploadMs),
            "max " + FrameStats.MaxUploadMs.ToString("F1", ci));
        // Cores the whole process keeps busy. Low at idle is HEALTH, not waste - a frame is
        // a latency problem and the serial main thread caps it (Amdahl); this row is for
        // judging the streaming pipeline, where the workers should actually show up.
        if (FrameStats.CpuCoresBusy > 0.05)
            Row(sb, "cpu-kerne",
                (100.0 * FrameStats.CpuCoresBusy / Environment.ProcessorCount).ToString("F0", ci) + " %",
                null,
                FrameStats.CpuCoresBusy.ToString("F1", ci) + " von "
                    + Environment.ProcessorCount + " kernen beschäftigt");
        Row(sb, "sichtweite", N(viewDistance), null, "blöcke");
        // The overlay's own price, so it can never again masquerade as an engine problem:
        // a Windows tester's ~40 ms Cairo rebuild at fixed 4 Hz WAS the ortho stutter.
        if (AvgRebuildMs >= 0.05)
            Row(sb, "hud-aufbau", null, Ms(AvgRebuildMs),
                "alle " + rebuildInterval.ToString("0.##", ci)
                + " s · davon upload " + AvgUploadMs.ToString("F1", ci) + " ms");

        extra?.Invoke(sb, frame);

        return sb.ToString().TrimEnd('\n');
    }

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        ctx?.Dispose();
        ctx = null;
        surface?.Dispose();
        surface = null;
    }
}
