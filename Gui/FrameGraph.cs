using System;
using System.Globalization;
using Cairo;
using Komet.Measure;
using Vintagestory.API.Client;

namespace Komet.Gui;

/// <summary>
/// The frame times of the last few seconds, drawn.
///
/// Every other row in this window is a smoothed average, and a smoothed average is exactly the
/// wrong instrument for the thing people complain about: one 40 ms frame per second moves a
/// 10 ms mean by a third of a millisecond. The 1 %/0,1 % rows next to this graph count those
/// frames; the graph shows their SHAPE - a regular sawtooth is a cadence (the shadow throttle,
/// the minimap tick), an isolated spike is a hitch, a step is a scene change. Three different
/// problems that read identically as a number.
///
/// It draws <see cref="FrameStats"/>'s ring, which every frame writes to whether this window is
/// open or not, so opening it costs one raster and no new measurement. Where more samples than
/// pixels exist each column shows the WORST frame in its bucket rather than the mean: a graph
/// of spikes that averages them away would be decoration.
/// </summary>
internal sealed class FrameGraph : CairoElement
{
    private readonly CairoFont font;
    private readonly float[] samples = new float[FrameStats.HistoryFrames];

    private LoadedTexture texture;

    /// <summary>The last drawn scale's ceiling, so the axis label can name it.</summary>
    public double CeilingMs { get; private set; }

    public FrameGraph(ICoreClientAPI capi, ElementBounds bounds, CairoFont font) : base(capi, bounds)
    {
        this.font = font;
        texture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    /// <summary>Render thread only: it rasters and uploads. Called on the window's refresh
    /// cadence, never per frame - the ring it reads is filled by the frame boundary.</summary>
    public void Redraw()
    {
        try
        {
            if (!EnsureSurface(16)) return;
            Draw();
            generateTexture(Surface, ref texture);
        }
        catch (Exception e)
        {
            api.Logger.Error("komet window: frame graph failed:\n{0}", e);
        }
    }

    /// <summary>
    /// The bucket a column shows: the worst frame in it. Pure and internal so verify can pin
    /// the two properties that matter - every sample lands in exactly one column, and no spike
    /// can be averaged out of the picture.
    /// </summary>
    internal static float ColumnWorst(float[] data, int count, int column, int columns)
    {
        if (count <= 0 || columns <= 0) return 0;
        var from = (int)((long)column * count / columns);
        var to = (int)((long)(column + 1) * count / columns);
        if (to <= from) to = from + 1;
        if (to > count) to = count;

        float worst = 0;
        for (var i = from; i < to; i++) if (data[i] > worst) worst = data[i];
        return worst;
    }

    /// <summary>
    /// Where the top of the graph sits. Not the worst frame in the window: one 300 ms world-join
    /// spike would flatten twenty seconds of playing into the bottom pixel row and keep it there
    /// until it rolls out of the ring. Twice the 1 % low leaves the spikes visible (they clip
    /// against the ceiling, which reads as "off the scale" rather than as a tall bar) while the
    /// ordinary frames still fill the picture. Floored so an idle 300 fps menu does not draw a
    /// noise field at full height.
    /// </summary>
    internal static double Ceiling(double low1Ms, double medianMs)
        => Math.Max(8.0, Math.Max(low1Ms * 2.0, medianMs * 3.0));

    private void Draw()
    {
        ClearSurface();

        // ground
        Ctx.SetSourceRGBA(0.06, 0.06, 0.07, 0.72);
        Ctx.Rectangle(0, 0, SurfW, SurfH);
        Ctx.Fill();

        FrameStats.UpdateLows();
        var count = FrameStats.CopyHistory(samples, samples.Length);
        var ceiling = Ceiling(FrameStats.Low1PercentMs, FrameStats.MedianFrameMs);
        CeilingMs = ceiling;

        // The three lines worth having behind the bars: the display's own budget where one is
        // known, the median, and the 1 % low. A frame under the first is a frame nobody waits
        // for; the distance between the last two IS the stutter.
        var refreshMs = RefreshBudgetMs();
        if (refreshMs > 0 && refreshMs < ceiling) Guide(refreshMs, ceiling, 0.30, 0.62, 0.34, 0.75);
        if (FrameStats.MedianFrameMs > 0) Guide(FrameStats.MedianFrameMs, ceiling, 0.55, 0.58, 0.65, 0.55);
        if (FrameStats.Low1PercentMs > 0) Guide(FrameStats.Low1PercentMs, ceiling, 0.85, 0.60, 0.25, 0.55);

        if (count > 0)
        {
            for (var x = 0; x < SurfW; x++)
            {
                var ms = ColumnWorst(samples, count, x, SurfW);
                if (ms <= 0) continue;

                var frac = Math.Min(1.0, ms / ceiling);
                var barH = Math.Max(1.0, frac * SurfH);

                // green below the median, amber past the 1 % low, red past twice it - the same
                // three answers the rows underneath give, in the same order.
                if (ms >= FrameStats.Low1PercentMs * 2 && FrameStats.Low1PercentMs > 0)
                    Ctx.SetSourceRGBA(0.90, 0.25, 0.22, 0.95);
                else if (ms >= FrameStats.Low1PercentMs && FrameStats.Low1PercentMs > 0)
                    Ctx.SetSourceRGBA(0.93, 0.70, 0.22, 0.9);
                else
                    Ctx.SetSourceRGBA(0.42, 0.76, 0.46, 0.85);

                Ctx.Rectangle(x, SurfH - barH, 1.0, barH);
                Ctx.Fill();
            }
        }

        // frame around it, and the ceiling named in the corner - a graph without its scale is
        // a picture, not a measurement
        Ctx.SetSourceRGBA(0.4, 0.4, 0.45, 0.6);
        Ctx.LineWidth = 1.0;
        Ctx.Rectangle(0.5, 0.5, SurfW - 1, SurfH - 1);
        Ctx.Stroke();

        font.SetupContext(Ctx);
        var fe = Ctx.FontExtents;
        Ctx.SetSourceRGBA(0.8, 0.8, 0.85, 0.85);
        Ctx.MoveTo(4, fe.Ascent + 2);
        Ctx.ShowText(ceiling.ToString("F0", CultureInfo.CurrentCulture) + " ms");
        Ctx.MoveTo(4, SurfH - 4);
        Ctx.ShowText(count + Loc.T("komet:gui-graph-frames", " frames"));
    }

    private void Guide(double ms, double ceiling, double r, double g, double b, double a)
    {
        var y = SurfH - Math.Min(1.0, ms / ceiling) * SurfH;
        Ctx.SetSourceRGBA(r, g, b, a);
        Ctx.LineWidth = 1.0;
        Ctx.MoveTo(0, y + 0.5);
        Ctx.LineTo(SurfW, y + 0.5);
        Ctx.Stroke();
    }

    /// <summary>
    /// One refresh interval in milliseconds, or 0 when the monitor cannot be asked. The line it
    /// draws is the only "fast enough" that means anything: a frame finished inside it is a
    /// frame the display was going to wait for anyway.
    /// </summary>
    private static double RefreshBudgetMs()
    {
        try
        {
            var platform = Vintagestory.Client.ScreenManager.Platform
                as Vintagestory.Client.NoObf.ClientPlatformWindows;
            if (platform?.window == null) return 0;
            var hz = OpenTK.Windowing.Desktop.Monitors.GetMonitorFromWindow(platform.window)
                .CurrentVideoMode.RefreshRate;
            return hz > 0 ? 1000.0 / hz : 0;
        }
        catch
        {
            return 0;
        }
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (texture.TextureId == 0) return;
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId,
            (int)Bounds.renderX, (int)Bounds.renderY, SurfW, SurfH);
    }

    public override void Dispose()
    {
        texture?.Dispose();
        base.Dispose();
    }
}
