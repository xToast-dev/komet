using System;
using System.Diagnostics;
using Cairo;
using Komet.Measure;

/// <summary>
/// What the '.komet' window costs to draw, and why it does not use the engine's text elements.
///
/// The claim in TextPanel's header - that pre-formatted monospace does not need the engine's
/// word-by-word autobreak layout, and that skipping it is worth a lot - was inherited from the
/// F7 overlay, where it came from a field report rather than from a measurement on this
/// machine. This prices both paths against each other on whatever machine runs it, on a page
/// the size the window actually shows.
///
/// The autobreak side is a MODEL, and is labelled as one: TextDrawUtil needs a CairoFont, and
/// a CairoFont needs the running client's font-measuring context. What is reproduced is the
/// shape of its cost - one cairo text measurement per word per line, each re-selecting the font
/// face - because that shape is the whole argument.
/// </summary>
internal static class WindowBench
{
    /// <summary>A page the size the window shows: the overview is about this long and this wide.</summary>
    private const int Lines = 34;
    private const int Width = 860;
    private const int Height = 528;

    private static string[] Page()
    {
        var lines = new string[Lines];
        for (var i = 0; i < Lines; i++)
            lines[i] = i % 6 == 0
                ? "── frame breakdown ─────────────────────────────"
                : $" shadow far      {i,4} %   {i + 0.37:F2} ms  ████████▍ of which swap {i * 0.11:F2}";
        return lines;
    }

    /// <summary>
    /// cairo-sharp imports "libcairo-2", which is the Windows spelling; the game installs its
    /// own resolver at startup and this harness has no game around it. Mapping the name to the
    /// platform's own soname is all that is needed, and a machine without cairo skips the
    /// section rather than failing the benchmark.
    /// </summary>
    private static bool CairoReady()
    {
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(ImageSurface).Assembly,
                (name, asm, path) => name is "libcairo-2" or "cairo"
                    ? System.Runtime.InteropServices.NativeLibrary.Load("libcairo.so.2", asm, path)
                    : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // a resolver is already installed - fine, that is the case this exists for
        }

        try
        {
            using var probe = new ImageSurface(Format.Argb32, 8, 8);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine("\nwindow raster: skipped, cairo did not load (" + e.GetType().Name + ")");
            return false;
        }
    }

    public static void Run()
    {
        if (!CairoReady()) return;
        Console.WriteLine("\nwindow raster (" + Lines + " lines, " + Width + "x" + Height + " px)");

        var lines = Page();
        using var surface = new ImageSurface(Format.Argb32, Width, Height);
        using var ctx = new Context(surface);

        // The panel's path: clear the reused surface, one ShowText per visible line.
        void Panel()
        {
            ctx.Operator = Operator.Clear;
            ctx.Paint();
            ctx.Operator = Operator.Over;
            ctx.SelectFontFace("monospace", FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(14);
            ctx.SetSourceRGBA(1, 1, 1, 1);
            var fe = ctx.FontExtents;
            for (var i = 0; i < lines.Length; i++)
            {
                ctx.MoveTo(0, fe.Ascent + i * fe.Height);
                ctx.ShowText(lines[i]);
            }
        }

        // The shape of the engine's layout: measure every word against the box before drawing
        // the line, re-selecting the font face for each measurement the way CairoFont does.
        void Autobreak()
        {
            ctx.Operator = Operator.Clear;
            ctx.Paint();
            ctx.Operator = Operator.Over;
            ctx.SetSourceRGBA(1, 1, 1, 1);
            var y = 0.0;
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var word in lines[i].Split(' '))
                {
                    ctx.SelectFontFace("monospace", FontSlant.Normal, FontWeight.Normal);
                    ctx.SetFontSize(14);
                    ctx.TextExtents(word);
                }
                ctx.SelectFontFace("monospace", FontSlant.Normal, FontWeight.Normal);
                ctx.SetFontSize(14);
                var fe = ctx.FontExtents;
                ctx.MoveTo(0, fe.Ascent + y);
                ctx.ShowText(lines[i]);
                y += fe.Height;
            }
        }

        Console.WriteLine($"  panel (one ShowText per line)   {Time(Panel):F3} ms");
        Console.WriteLine($"  autobreak-shaped layout (model) {Time(Autobreak):F3} ms");

        // The other per-refresh cost the window carries: the percentile sort behind the 1 % and
        // 0,1 % rows. It runs at most once per frame however many readers ask for it, so this is
        // its whole price per refresh.
        FrameStats.Reset();
        for (var i = 0; i < FrameStats.HistoryFrames + 64; i++)
            FrameStats.Advance(Stopwatch.GetTimestamp(), 0);
        var lowsMs = Time(() => { FrameStats.ForceLowsForBench(); });
        Console.WriteLine($"  percentile sort over {FrameStats.HistoryCount,5} frames  {lowsMs:F3} ms");

        Console.WriteLine("  at 4 Hz that is " + (Time(Panel) * 4).ToString("F2") + " ms per second of wall time");
    }

    private static double Time(Action a)
    {
        for (var i = 0; i < 20; i++) a();          // warm the JIT and the font cache
        var sw = Stopwatch.StartNew();
        const int Runs = 200;
        for (var i = 0; i < Runs; i++) a();
        return sw.Elapsed.TotalMilliseconds / Runs;
    }
}
