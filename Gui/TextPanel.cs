using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cairo;
using Komet.Measure;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Komet.Gui;

/// <summary>
/// A block of pre-formatted monospace text inside a dialog: split at the newlines the composer
/// already put there, one ShowText per line, into a Surface that is reused across refreshes.
///
/// It is deliberately not a <c>GuiElementDynamicText</c>, and the reason is measured rather
/// than stylistic. The engine's text elements lay text out through TextDrawUtil, which breaks
/// lines by measuring word by word against the box width - a cairo call per measurement, each
/// one re-selecting the font face. A Windows tester's box turned that into ~40 ms per rebuild
/// for the F7 overlay's dozen rows (1-2 ms on the dev machine), which is why the overlay stopped
/// using it; this window shows five times as many rows, several times a second. Pre-formatted
/// monospace needs none of that layout, so it does not pay for it.
///
/// Only the VISIBLE lines are drawn. Scrolling a thousand-line report therefore costs what
/// scrolling a ten-line one costs, and the Surface - with it the texture upload that every
/// refresh pays for - stays the size of the panel rather than the size of the content. That is
/// the difference between a constant 1,7 MB upload and one that grows with the report.
///
/// What it does NOT skip is the line break. The rows come from writers that were built for the
/// F7 overlay, which sizes its box to the longest line it produced - so nothing there is ever
/// too long. This panel is a fixed width, and the same rows ran straight past its border and
/// were cut off mid-word by the Surface edge: the tick-listener line lost its list of the most
/// expensive listeners, the main-thread line lost its budget figures, and neither looked
/// truncated - it looked broken. Overlong lines are therefore wrapped here, at a space where
/// there is one, with the continuation indented so a wrapped row still reads as one row. It is
/// a character count against a monospace cell, not the engine's word-by-word measuring - a
/// forty-row page with six overlong rows wraps in 4 us here, and only when the text or the
/// panel's width in cells actually changes.
/// </summary>
internal sealed class TextPanel : CairoElement
{
    private readonly CairoFont font;

    /// <summary>The text as it will be drawn: the composer's own lines, wrapped to the panel.
    /// Reused across refreshes - a page is rebuilt four times a second.</summary>
    private readonly List<string> lines = new(96);

    private string text = "";

    /// <summary>The width <see cref="lines"/> was wrapped to, so a resize or a GUI scale change
    /// re-wraps and an unchanged panel does not.</summary>
    private int wrapColumns = -1;

    /// <summary>
    /// Two textures, used in turn.
    ///
    /// The upload is a glTexSubImage2D straight out of the cairo Surface, and the driver may
    /// not start it while the GPU is still reading that texture for a frame already in flight -
    /// it blocks the render thread until it is. On a GPU that is 80 % busy that is not a
    /// theoretical stall: a field log has this panel's stage (ortho) at 10-17 ms per refresh
    /// with 1,2 ms of it booked as work, i.e. the rest spent waiting. Writing into the texture
    /// that was NOT the one drawn last gives the GPU a full refresh of slack to finish with the
    /// other one, for 1,7 MB of video memory.
    /// </summary>
    private readonly LoadedTexture[] textures = new LoadedTexture[2];
    private int front;


    /// <summary>A cell of air on each side, so the first and last column are not welded to the
    /// inset's border. In scaled pixels, like everything else the raster works in.</summary>
    private static double Padding => scaled(3);

    // Font metrics, probed once per GUI scale: each probe is a full cairo font-map lookup.
    private double charAdvance;
    private double ascent;
    private int lineHeight = 12;
    private float metricsScale = -1;

    /// <summary>How far the content is scrolled, in scaled pixels.</summary>
    private int scrollPx;

    private bool dirty = true;

    /// <summary>What one raster of this panel last cost, smoothed. The window prints it: an
    /// instrument that cannot say what it costs is one more unmeasured thing in the frame.</summary>
    public double AvgRasterMs { get; private set; }

    public TextPanel(ICoreClientAPI capi, ElementBounds bounds, CairoFont font) : base(capi, bounds)
    {
        this.font = font;
        textures[0] = new LoadedTexture(capi);
        textures[1] = new LoadedTexture(capi);
    }

    /// <summary>Line height in scaled pixels - what a scroll step is measured in.</summary>
    public int LineHeight => Math.Max(1, lineHeight);

    /// <summary>Lines as they are drawn - wrapped ones counted separately, because that is what
    /// the scrollbar has to scroll past.</summary>
    public int LineCount => lines.Count;

    /// <summary>The content's height in the unscaled units the scrollbar works in.</summary>
    public double ContentHeightUnscaled => lines.Count * LineHeight / (double)Math.Max(0.01f, RuntimeEnv.GUIScale);

    /// <summary>The panel's own height in those same units.</summary>
    public double VisibleHeightUnscaled => Bounds.InnerHeight / Math.Max(0.01f, RuntimeEnv.GUIScale);

    /// <summary>
    /// How many monospace cells fit across the panel. The composer asks before it builds a page,
    /// so the tables it lays out are the width of the panel they land in rather than the width
    /// of the overlay they were first written for.
    /// </summary>
    public int Columns
    {
        get
        {
            ProbeMetrics();
            if (charAdvance <= 0) return 48;
            return Math.Max(8, (int)((Bounds.InnerWidth - 2 * Padding) / charAdvance));
        }
    }

    /// <summary>
    /// New content. Unchanged text is not a redraw - the numbers move on most refreshes, but a
    /// paused game or a view with nothing live in it then costs nothing at all.
    /// </summary>
    public void SetText(string value)
    {
        value ??= "";
        if (value == text && wrapColumns == Columns) return;
        text = value;
        Wrap();
    }

    /// <summary>Breaks the composer's lines into the ones that get drawn. Called when the text
    /// changes and when the panel's width in cells does - a GUI scale change is the second
    /// one, and it used to leave the page wrapped for the old width.</summary>
    private void Wrap()
    {
        wrapColumns = Columns;
        lines.Clear();
        var start = 0;
        while (start <= text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl;
            WrapInto(lines, text.Substring(start, end - start).TrimEnd(), wrapColumns);
            if (nl < 0) break;
            start = nl + 1;
        }
        dirty = true;
    }

    /// <summary>
    /// One composed line, as the one to three lines it is drawn as. Pure, so verify can pin the
    /// geometry without a GL context: nothing it returns is ever wider than the panel, and no
    /// word is ever lost between the pieces.
    ///
    /// Broken at the last space that still fits, and hard at the cell otherwise - a type name
    /// or a file path has nowhere to break, and pushing it past the border is what this exists
    /// to stop. The continuation carries the row's own leading space plus two, so a wrapped
    /// table row reads as a continuation and not as a new row.
    /// </summary>
    internal static void WrapInto(List<string> into, string line, int columns)
    {
        if (line.Length <= columns)
        {
            into.Add(line);
            return;
        }

        var lead = 0;
        while (lead < line.Length && line[lead] == ' ') lead++;
        var indent = Math.Min(lead + 2, Math.Max(0, columns / 4));

        var start = 0;
        var first = true;
        while (start < line.Length)
        {
            var room = Math.Max(1, columns - (first ? 0 : indent));
            if (line.Length - start <= room)
            {
                into.Add(Piece(line, start, line.Length, first ? 0 : indent));
                return;
            }

            // The break has to leave something on the line: searching back past the row's own
            // leading spaces would break at one of them and emit a blank line before a long
            // unbreakable run.
            var text = start;
            while (text < line.Length && line[text] == ' ') text++;

            var limit = start + room;
            var cut = -1;
            for (var i = limit; i > text; i--)
                if (line[i] == ' ') { cut = i; break; }
            if (cut <= text) cut = limit;

            into.Add(Piece(line, start, cut, first ? 0 : indent));
            start = cut;
            while (start < line.Length && line[start] == ' ') start++;
            first = false;
        }
    }

    /// <summary>
    /// One line cut to what a box of <paramref name="columns"/> cells holds, with a trailing
    /// ellipsis when something was dropped.
    ///
    /// The counterpart to <see cref="WrapInto"/>, for the places that have exactly one line of
    /// room and no second one to spill into - a toggle row, whose next line is the next
    /// switch. The engine's static text element autobreaks instead, and drew a blocked
    /// switch's reason straight over the row below it.
    /// </summary>
    internal static string Ellipsize(string line, int columns)
    {
        if (line == null) return "";
        if (columns <= 0) return "";
        if (line.Length <= columns) return line;
        return columns == 1 ? "…" : string.Concat(line.AsSpan(0, columns - 1), "…");
    }

    private static string Piece(string line, int from, int to, int indent)
    {
        var part = line.Substring(from, to - from).TrimEnd();
        return indent > 0 ? new string(' ', indent) + part : part;
    }

    /// <summary>Scroll position from the scrollbar, which counts in unscaled pixels.</summary>
    public void ScrollToUnscaled(double value)
    {
        var px = (int)Math.Max(0, value * RuntimeEnv.GUIScale);
        if (px == scrollPx) return;
        scrollPx = px;
        dirty = true;
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        dirty = true;
        Redraw();
    }

    /// <summary>
    /// Rasters the visible lines and uploads them. Render thread only (it talks to GL), and a
    /// no-op unless the text or the scroll position actually changed.
    /// </summary>
    public void Redraw()
    {
        if (!dirty) return;
        var t0 = Stopwatch.GetTimestamp();

        try
        {
            ProbeMetrics();
            if (!EnsureSurface(8)) return;
            if (wrapColumns != Columns) Wrap();
            Raster();
            var next = front ^ 1;
            generateTexture(Surface, ref textures[next]);
            front = next;
            dirty = false;
        }
        catch (Exception e)
        {
            // A panel that cannot draw must not take the window - let alone the game - down.
            // It goes blank, the log says why once, and everything else keeps working.
            dirty = false;
            api.Logger.Error("komet window: text panel raster failed:\n{0}", e);
        }

        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        AvgRasterMs = AvgRasterMs <= 0 ? ms : AvgRasterMs + (ms - AvgRasterMs) * 0.2;

        // Booked where the overlay's own cost is booked, for the same reason: this runs inside
        // the Ortho stage, and a frame it spikes has to be attributable to the instrument that
        // spiked it instead of turning up as an unexplained render stage. It used to be timed
        // and printed but never booked, so a hitch showed 17 ms of ortho next to 1,2 ms of
        // "hud" and nothing joined the two.
        FrameStats.AddHudMs(ms);
    }

    private void ProbeMetrics()
    {
        if (metricsScale == RuntimeEnv.GUIScale) return;
        charAdvance = font.GetTextExtents("0000000000").Width / 10.0;
        // The share bars assume the block glyphs advance exactly one monospace cell; a font
        // that substitutes them from a differently sized fallback would draw past the panel.
        // Probed the same way the overlay probes it, so both degrade to '#' together.
        var barAdvance = font.GetTextExtents("████").Width / 4.0;
        DebugHud.BarAscii = charAdvance <= 0 || Math.Abs(barAdvance - charAdvance) > charAdvance * 0.05;
        var fe = font.GetFontExtents();
        ascent = fe.Ascent;
        lineHeight = Math.Max(1, (int)fe.Height);
        metricsScale = RuntimeEnv.GUIScale;
        dirty = true;
    }

    private void Raster()
    {
        ClearSurface();
        font.SetupContext(Ctx);

        // Which lines can be seen: the first one the scroll offset reaches, and as many as fit
        // plus one, so a half line at the bottom edge is drawn rather than popping into place.
        var first = Math.Max(0, scrollPx / LineHeight);
        var offsetY = -(scrollPx % LineHeight);
        var visible = SurfH / LineHeight + 2;

        for (var i = 0; i < visible; i++)
        {
            var idx = first + i;
            if (idx >= lines.Count) break;
            var line = lines[idx];
            if (line.Length == 0) continue;
            Ctx.MoveTo(Padding, offsetY + i * LineHeight + ascent);
            Ctx.ShowText(line);
        }
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        var texture = textures[front];
        if (texture.TextureId == 0) return;
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId,
            (int)Bounds.renderX, (int)Bounds.renderY, SurfW, SurfH);
    }

    public override void Dispose()
    {
        textures[0]?.Dispose();
        textures[1]?.Dispose();
        base.Dispose();
    }
}
