using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Komet.Culling;
using Komet.Guard;
using Komet.Gui;
using Komet.Measure;
using Komet.Patches;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.Client;

namespace Komet;

/// <summary>
/// What each page of the '.komet' window says.
///
/// Not one figure in here is measured for the window. Every row reads the same static the F7
/// overlay, the chat replies and '.komet report' read, and wherever a block of rows already
/// existed for one of those surfaces the window calls that block rather than restating it -
/// <see cref="DebugHud.WriteFrameBreakdown"/>, <see cref="DebugHud.WriteGc"/>,
/// <see cref="WriteSweepRows"/> and its neighbours, <see cref="ModProfiler.Write"/>,
/// <see cref="HitchLog.BuildReport"/>, <see cref="PatchGuard.ReportLines"/>. The pages are an
/// arrangement of what exists, which is the only arrangement that cannot drift away from it.
///
/// Two costs are handled here rather than in the window: the pages whose text is expensive to
/// build (the full report, the patch scan, the config listing) go through <see cref="Slow"/> and
/// rebuild at most once a second, and the mod table reads the ten-second scan instead of forcing
/// one. Everything else is cheap enough to build on the window's own cadence.
/// </summary>
public partial class KometModSystem
{
    /// <summary>Cache for the pages that are expensive to compose - see <see cref="Slow"/>.</summary>
    private string slowText;
    private KometView slowView = (KometView)(-1);
    private long slowAtTicks;

    /// <summary>
    /// A page whose text costs real work to build: the full report, the patch guard's scan, the
    /// reflected config listing. The window refreshes four times a second and none of these move
    /// at that rate, so they are rebuilt when the page is opened and then once a second. The
    /// alternative measured badly in the only way that matters here - a performance window that
    /// spends a millisecond a second building a page nobody is watching change.
    /// </summary>
    private string Slow(KometView view, Func<string> build)
    {
        var now = Stopwatch.GetTimestamp();
        if (slowView == view && slowText != null
            && (now - slowAtTicks) < Stopwatch.Frequency) return slowText;

        slowView = view;
        slowAtTicks = now;
        slowText = build();
        return slowText;
    }

    /// <summary>
    /// A heading with a block under it, and a stated "nothing" when the block had nothing to
    /// say. An empty heading reads as a broken page; a heading that says it is empty reads as
    /// an idle system, and telling those two apart is the oldest rule in this project - the
    /// edge-priority counter is printed at zero for exactly this reason. Nineteen pages need
    /// it more than one overlay did.
    /// </summary>
    private static void Block(StringBuilder sb, string heading, Action<StringBuilder> write)
    {
        DebugHud.Section(sb, heading);
        var before = sb.Length;
        write(sb);
        if (sb.Length == before)
            DebugHud.Row(sb, "", null, null, Loc.T("komet:gui-nothing-here", "nothing measured yet"));
    }

    /// <summary>
    /// The text of one page. Called by the window on its refresh cadence with a reused buffer;
    /// nothing in here may allocate per row beyond what the shared formatting helpers already do.
    /// </summary>
    internal void ComposeView(KometView view, StringBuilder sb, KometDialog dlg)
    {
        var frame = FrameStats.AvgFrameMs;

        // Anything that changes what every other number means goes on top of EVERY page, not
        // just the overview. A reader who tabbed straight to the GPU page must not have to
        // guess whether safemode is on or a stress test is halfway through a slice.
        WriteKometWarnings(sb, frame);
        if (sb.Length > 0) sb.Append('\n');

        // The pages that describe the frame need frames; the ones that describe the
        // installation do not, and a player who opens the window in the main menu should still
        // get an answer out of them.
        if (!FrameStats.HasData && view != KometView.Config && view != KometView.Conflicts
                                && view != KometView.Toggles && view != KometView.Stress)
        {
            sb.Append(Loc.T("komet:hud-collecting", "collecting data ... ({0} frames)", FrameStats.TotalFrames));
            return;
        }

        switch (view)
        {
            case KometView.Overview: ViewOverview(sb, frame, dlg); break;
            case KometView.Frametime: ViewFrametime(sb, frame); break;
            case KometView.Cpu: ViewCpu(sb, frame); break;
            case KometView.Gpu: ViewGpu(sb, frame); break;
            case KometView.Rendering: ViewRendering(sb, frame); break;
            case KometView.Culling: ViewCulling(sb, frame); break;
            case KometView.Entities: ViewEntities(sb); break;
            case KometView.Chunks: ViewChunks(sb); break;
            case KometView.Memory: ViewMemory(sb); break;
            case KometView.Threads: ViewThreads(sb); break;
            case KometView.Cache: ViewCache(sb); break;
            case KometView.Mods: sb.Append(Slow(view, () => ModProfileText(20, rescan: false))); break;
            case KometView.Hitches: ViewHitches(sb); break;
            case KometView.Profiler: ViewProfiler(sb, frame); break;
            case KometView.Toggles: ViewToggles(sb); break;
            case KometView.Config: sb.Append(Slow(view, ConfigText)); break;
            case KometView.Stress: ViewStress(sb); break;
            case KometView.Conflicts: sb.Append(Slow(view, ConflictText)); break;
            case KometView.Report: sb.Append(Slow(view, BuildFullReport)); break;
        }
    }

    // ---- overview ---------------------------------------------------------------------

    private void ViewOverview(StringBuilder sb, double frame, KometDialog dlg)
    {
        var ci = CultureInfo.CurrentCulture;
        FrameStats.UpdateLows();

        DebugHud.Section(sb, Loc.Hud("frame"));
        DebugHud.Row(sb, Loc.Hud("fps"), (frame > 0 ? 1000.0 / frame : 0).ToString("F0", ci), DebugHud.Ms(frame));
        WriteLowRows(sb);
        DebugHud.Row(sb, Loc.Hud("worst"), null, DebugHud.Ms(FrameStats.MaxFrameMs), DebugHud.WorstFrameTail());

        // The one line that decides which of the other pages is worth opening.
        DebugHud.Section(sb, Loc.Hud("cpu or gpu"));
        var bound = FrameVerdict.Current();
        DebugHud.Row(sb, Loc.Hud("verdict"), null, null,
            FrameVerdict.Text(bound) + (FrameVerdict.Advice(bound) != null ? " - " + FrameVerdict.Advice(bound) : ""));
        var cpuMs = Math.Max(0, frame - FrameStats.OutsideStagesMs);
        DebugHud.Row(sb, Loc.Hud("cpu frame"), DebugHud.Pct(cpuMs, frame), DebugHud.Ms(cpuMs),
            Loc.T("komet:gui-stages-tick", "render stages + game tick"));
        if (GpuFrameTimer.GpuMs > 0)
            DebugHud.Row(sb, Loc.Hud("gpu frame"), DebugHud.Pct(GpuFrameTimer.GpuMs, frame), DebugHud.Ms(GpuFrameTimer.GpuMs),
                Loc.T("komet:gui-gpu-samples", "{0} samples", DebugHud.N(GpuFrameTimer.StatSamples)));
        if (GpuBusy.Available)
            DebugHud.Row(sb, Loc.Hud("gpu load"), GpuBusy.Percent.ToString(ci) + " %", null, GpuBusy.Source);
        DebugHud.Row(sb, Loc.Hud("outside"), DebugHud.Pct(FrameStats.OutsideStagesMs, frame),
            DebugHud.Ms(FrameStats.OutsideStagesMs),
            Loc.T("komet:gui-swap-of", "swap {0} ms - vsync, limiter, driver", FrameStats.AvgSwapMs.ToString("F2", ci)));

        DebugHud.Section(sb, Loc.Hud("gc"));
        DebugHud.Row(sb, Loc.Hud("gc pauses"), FrameStats.Gen0PerSecond.ToString("F0", ci) + "/s",
            DebugHud.Ms(FrameStats.GcPauseMsPerSecond),
            Loc.T("komet:hud-per-s-alloc", "per s · {0} MB/s alloc", FrameStats.AllocMbPerSecond.ToString("F0", ci)));
        DebugHud.Row(sb, Loc.Hud("gc heap"), DebugHud.N(FrameStats.GcHeapMb) + " MB", null,
            System.Runtime.GCSettings.IsServerGC ? "server gc" : "workstation gc");

        DebugHud.Section(sb, Loc.Hud("world"));
        SampleWorldForWindow();
        DebugHud.Row(sb, Loc.Hud("draw calls"), DebugHud.N(windowDrawCalls), null,
            Loc.T("komet:hud-triangles", "triangles {0} of {1} mio",
                DebugHud.Mio(windowWorld.RenderedTris), DebugHud.Mio(windowWorld.AllocatedTris)));
        DebugHud.Row(sb, Loc.Hud("entities"), DebugHud.N(RuntimeStats.renderedEntities), null,
            Loc.T("komet:gui-entities-loaded", "{0} loaded · {1} animation frames skipped",
                DebugHud.N(LoadedEntityCount()), DebugHud.N(EntityAnimPatches.StatSkipped)));
        DebugHud.Row(sb, Loc.Hud("chunks"), DebugHud.N(windowWorld.LoadedChunks), null,
            Loc.T("komet:hud-queue-2", "queue {0}/{1}",
                DebugHud.N(RuntimeStats.chunksAwaitingTesselation), DebugHud.N(RuntimeStats.chunksAwaitingPooling)));
        DebugHud.Row(sb, Loc.Hud("terrain vram"), DebugHud.N(windowWorld.VramBytes / 1048576.0) + " MB", null,
            Loc.T("komet:hud-pools-frag", "{0} pools · {1} % frag",
                windowWorld.PoolCount, (windowWorld.Fragmentation * 100f).ToString("F0", ci)));

        // What this window costs, in the window. An instrument that will not say its own price
        // is one more unmeasured thing in the frame - and this one is booked to the overlay
        // column of the hitch log, so it can never hide in "outside" either.
        DebugHud.Section(sb, Loc.Hud("this window"));
        (var refreshMs, var rasterMs, var intervalS) = dlg?.OwnCost() ?? (0, 0, 0);
        DebugHud.Row(sb, Loc.Hud("refresh"), null, DebugHud.Ms(refreshMs),
            Loc.T("komet:gui-every", "every {0} s · raster {1} ms · counted as hud",
                intervalS.ToString("0.##", ci), rasterMs.ToString("F2", ci)));
        if (DebugHud.AvgRebuildMs >= 0.05)
            DebugHud.Row(sb, Loc.Hud("f7 overlay"), null, DebugHud.Ms(DebugHud.AvgRebuildMs),
                Loc.T("komet:gui-overlay-upload", "upload {0} ms", DebugHud.AvgUploadMs.ToString("F2", ci)));
    }

    /// <summary>
    /// The distribution rows, shared by the overview and the frametime page: the two figures
    /// that separate "slow" from "stuttery", each with the frame rate they correspond to,
    /// because a player thinks in fps and a frame budget is in milliseconds.
    /// </summary>
    private static void WriteLowRows(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;
        FrameStats.UpdateLows();
        if (FrameStats.HistoryCount == 0) return;

        string Fps(double ms) => ms > 0 ? " = " + (1000.0 / ms).ToString("F0", ci) + " fps" : "";

        DebugHud.Row(sb, Loc.Hud("median"), null, DebugHud.Ms(FrameStats.MedianFrameMs), Fps(FrameStats.MedianFrameMs).TrimStart(' ', '='));
        DebugHud.Row(sb, Loc.Hud("1 % low"), null, DebugHud.Ms(FrameStats.Low1PercentMs),
            Loc.T("komet:gui-low-tail", "mean of the worst 1 %{0}", Fps(FrameStats.Low1PercentMs)));
        DebugHud.Row(sb, Loc.Hud("0.1 % low"), null, DebugHud.Ms(FrameStats.Low01PercentMs),
            Loc.T("komet:gui-low-tail-01", "mean of the worst 0,1 %{0}", Fps(FrameStats.Low01PercentMs)));
        DebugHud.Row(sb, Loc.Hud("window"), DebugHud.N(FrameStats.HistoryCount), DebugHud.Ms(FrameStats.WindowWorstMs),
            Loc.T("komet:gui-window-tail", "frames kept · longest of them"));
    }

    // ---- frametime --------------------------------------------------------------------

    private void ViewFrametime(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;

        DebugHud.Section(sb, Loc.Hud("distribution"));
        DebugHud.Row(sb, Loc.Hud("fps"), (frame > 0 ? 1000.0 / frame : 0).ToString("F0", ci), DebugHud.Ms(frame),
            Loc.T("komet:gui-smoothed", "smoothed mean"));
        WriteLowRows(sb);
        // The mean against the median is the whole "is it smooth" question in one comparison:
        // far apart means a tail is carrying the average, and the graph above shows its shape.
        if (FrameStats.MedianFrameMs > 0)
            DebugHud.Row(sb, Loc.Hud("tail weight"),
                (100.0 * (frame - FrameStats.MedianFrameMs) / FrameStats.MedianFrameMs).ToString("F0", ci) + " %",
                null, Loc.T("komet:gui-tail-weight", "how far the mean sits above the median"));

        DebugHud.Section(sb, Loc.Hud("worst frame"));
        DebugHud.Row(sb, Loc.Hud("worst"), null, DebugHud.Ms(FrameStats.MaxFrameMs), DebugHud.WorstFrameTail());
        DebugHud.Row(sb, Loc.Hud("gc pause"), null, DebugHud.Ms(FrameStats.WorstGcPauseMs),
            Loc.T("komet:gui-in-worst", "in that frame"));

        DebugHud.Section(sb, Loc.Hud("hitches"));
        if (HitchLog.TotalHitches == 0)
        {
            DebugHud.Row(sb, Loc.Hud("hitches"), "0", null,
                Loc.T("komet:gui-no-hitches", "no frame over {0} ms or {1}x the average yet",
                    HitchLog.MinMs.ToString("F0", ci), HitchLog.Factor.ToString("0.#", ci)));
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("hitches"), DebugHud.N(HitchLog.TotalHitches), null,
                HitchLog.PerMinute.ToString("F1", ci) + Loc.T("komet:gui-per-min", "/min"));
            DebugHud.Row(sb, Loc.Hud("while"), null, null,
                Loc.T("komet:gui-hitch-split", "turning {0} · moving {1} · standing {2}",
                    DebugHud.N(HitchLog.CountTurning), DebugHud.N(HitchLog.CountMoving), DebugHud.N(HitchLog.CountStill)));
            DebugHud.Row(sb, Loc.Hud("of which gc"), DebugHud.N(HitchLog.CountGcPause), null,
                Loc.T("komet:gui-hitch-gen2", "{0} with a gen2 collection", DebugHud.N(HitchLog.CountGen2)));
            var last = HitchLog.LastTail();
            if (last != null) DebugHud.Row(sb, Loc.Hud("last"), null, null, last);
            DebugHud.Row(sb, Loc.Hud("full list"), null, null,
                Loc.T("komet:gui-see-hitches", "the Hitches page, or '.komet hitch'"));
        }

        // WriteFrameBreakdown brings its own heading - the same one the overlay prints.
        DebugHud.WriteFrameBreakdown(sb, frame);
    }

    // ---- cpu --------------------------------------------------------------------------

    private void ViewCpu(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;

        // The client runs the whole game tick AND every render stage on one thread, so this
        // block is the frame rate. Everything else on this page explains a row of it. The
        // block brings its own heading, the one the overlay has always printed.
        DebugHud.WriteFrameBreakdown(sb, frame);

        DebugHud.Section(sb, Loc.Hud("game tick"));
        DebugHud.Row(sb, Loc.Hud("game tick"), DebugHud.Pct(FrameStats.GameTickMs, frame), DebugHud.Ms(FrameStats.GameTickMs));
        if (TickProfiler.Enabled && TickProfiler.StatWrapped > 0)
        {
            TickProfiler.Write(sb, 8, ci, FrameStats.GameTickMs);
            DebugHud.Row(sb, Loc.Hud("of which measured"),
                TickProfiler.StatWrapped + "/" + TickProfiler.StatTotal, null,
                Loc.T("komet:gui-listeners", "tick listeners"));
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("tick listener"), null, null,
                Loc.T("komet:gui-tickprofiler-off", "the tick profiler is off - '.komet toggle tickprofiler'"));
        }

        DebugHud.Section(sb, Loc.Hud("main thread tasks"));
        if (MainThreadTaskPatches.Enabled)
        {
            WriteMainTaskRow(sb, ci, "");
            MainThreadTaskPatches.Write(sb, 8, ci);
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("mt tasks"), null, null,
                Loc.T("komet:gui-mtt-off", "attribution off - '.komet toggle mtt'"));
        }

        DebugHud.Section(sb, Loc.Hud("waits and synchronisation"));
        DebugHud.Row(sb, Loc.Hud("sweep wait"), null, DebugHud.Ms(JobScheduler.StatWaitTicks * 1000.0 / Stopwatch.Frequency),
            Loc.T("komet:gui-since-reset", "since the last reset"));
        DebugHud.Row(sb, Loc.Hud("swap"), DebugHud.Pct(FrameStats.AvgSwapMs, frame), DebugHud.Ms(FrameStats.AvgSwapMs),
            Loc.T("komet:gui-swap-note", "driver back-pressure or the frame limiter, not rendering"));
        DebugHud.Row(sb, Loc.Hud("outside"), DebugHud.Pct(FrameStats.OutsideStagesMs, frame),
            DebugHud.Ms(FrameStats.OutsideStagesMs),
            Loc.T("komet:gui-outside-note", "no stage and no tick accounts for this"));
        DebugHud.Row(sb, Loc.Hud("gc pauses"), null, DebugHud.Ms(FrameStats.GcPauseMsPerSecond),
            Loc.T("komet:gui-gc-note", "per second, every thread stopped at once"));

        DebugHud.Section(sb, Loc.Hud("cpu"));
        DebugHud.Row(sb, Loc.Hud("cpu cores"),
            (100.0 * FrameStats.CpuCoresBusy / Math.Max(1, Environment.ProcessorCount)).ToString("F0", ci) + " %", null,
            Loc.T("komet:hud-cores-busy", "{0} of {1} cores busy",
                FrameStats.CpuCoresBusy.ToString("F1", ci), Environment.ProcessorCount));
        DebugHud.Row(sb, Loc.Hud("cores"), CpuTopology.PhysicalCores + "/" + CpuTopology.LogicalCores, null,
            Loc.T("komet:gui-cores-source", "physical/logical ({0})", CpuTopology.Source));
    }

    // ---- gpu --------------------------------------------------------------------------

    private void ViewGpu(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;

        if (!GpuFrameTimer.Enabled)
        {
            DebugHud.Row(sb, Loc.Hud("gpu"), null, null,
                Loc.T("komet:gui-gpu-off", "GPU timing is off - MeasureGpuTime in komet.json"));
            return;
        }

        DebugHud.Section(sb, Loc.Hud("gpu"));
        var bound = FrameVerdict.Current();
        DebugHud.Row(sb, Loc.Hud("verdict"), null, null, FrameVerdict.Text(bound));
        DebugHud.Row(sb, Loc.Hud("gpu frame"), DebugHud.Pct(GpuFrameTimer.GpuMs, frame), DebugHud.Ms(GpuFrameTimer.GpuMs),
            Loc.T("komet:gui-gpu-span", "GL span, {0} samples - counts idle gaps too",
                DebugHud.N(GpuFrameTimer.StatSamples)));
        if (GpuBusy.Available)
            DebugHud.Row(sb, Loc.Hud("gpu load"), GpuBusy.Percent.ToString(ci) + " %", null,
                Loc.T("komet:hud-busy", "busy ({0})", GpuBusy.Source));
        DebugHud.Row(sb, Loc.Hud("cpu frame"), null, DebugHud.Ms(frame),
            Loc.T("komet:gui-cpu-side", "the CPU's own frame, for comparison"));

        if (GpuFrameTimer.StageSamples == 0)
        {
            DebugHud.Row(sb, Loc.Hud("per stage"), null, null,
                Loc.T("komet:gui-gpu-nostages", "no per-stage samples yet"));
            return;
        }

        // Where the GPU's milliseconds actually go. This is the block that turned "the frame is
        // GPU bound" into "86 % of it is the two shadow passes".
        DebugHud.Section(sb, Loc.Hud("gpu by stage"));
        void Stage(string label, double ms, string tail = null)
            => DebugHud.Row(sb, label, DebugHud.Pct(ms, GpuFrameTimer.StampSpanMs > 0 ? GpuFrameTimer.StampSpanMs : frame),
                            DebugHud.Ms(ms), tail);

        Stage(Loc.Hud("before"), GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Before]);
        var far = GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone);
        Stage(Loc.Hud("shadow far"), far,
            GpuFrameTimer.FarDrawnSamples > 0
                ? Loc.T("komet:gui-when-drawn", "{0} ms in the frames that drew it",
                    GpuFrameTimer.FarDrawnGpuMs.ToString("F1", ci))
                : null);
        Stage(Loc.Hud("shadow near"), GpuFrameTimer.StageSum(EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone));
        Stage(Loc.Hud("opaque"), GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Opaque]);
        Stage(Loc.Hud("oit"), GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.OIT]);
        Stage(Loc.Hud("post/compose"), GpuFrameTimer.StageSum(EnumRenderStage.AfterOIT,
            EnumRenderStage.AfterPostProcessing, EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit));
        Stage(Loc.Hud("ortho (gui)"), GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Ortho]);
        Stage(Loc.Hud("done"), GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Done]);
        DebugHud.Row(sb, "= " + Loc.Hud("frame by stamps"), null, DebugHud.Ms(GpuFrameTimer.StampSpanMs),
            Loc.T("komet:gui-stamp-note", "{0} samples - read the stages against this",
                DebugHud.N(GpuFrameTimer.StageSamples)));

        Block(sb, Loc.Hud("shadow maps"), WriteShadowRows);
        DebugHud.Row(sb, Loc.Hud("solid passes"), null, null,
            (ShadowCullPatches.Enabled
                ? Loc.T("komet:gui-backfaces-culled", "back faces culled")
                : Loc.T("komet:gui-backfaces-all", "every face (vanilla)"))
            + " · "
            + (ShadowCullPatches.DepthOnly
                ? Loc.T("komet:gui-depthonly-on", "depth-only shader")
                : Loc.T("komet:gui-depthonly-off", "alpha-test shader (vanilla)"))
            + (ShadowCullPatches.DepthOnlyState != null ? " · " + ShadowCullPatches.DepthOnlyState : ""));
    }

    // ---- rendering --------------------------------------------------------------------

    private void ViewRendering(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;
        SampleWorldForWindow();

        DebugHud.Section(sb, Loc.Hud("what is submitted"));
        DebugHud.Row(sb, Loc.Hud("draw calls"), DebugHud.N(windowDrawCalls), null,
            Loc.T("komet:hud-triangles", "triangles {0} of {1} mio",
                DebugHud.Mio(windowWorld.RenderedTris), DebugHud.Mio(windowWorld.AllocatedTris)));
        var raw = rawRangesPerFrame?.PerFrame ?? 0;
        var emitted = rangesPerFrame?.PerFrame ?? 0;
        DebugHud.Row(sb, Loc.Hud("draw ranges"), DebugHud.N(emitted), null,
            Loc.T("komet:hud-of-raw", "of {0} ({1}x)", DebugHud.N(raw),
                (emitted > 0 ? raw / emitted : 1).ToString("F1", ci)));
        DebugHud.Row(sb, Loc.Hud("terrain vram"), DebugHud.N(windowWorld.VramBytes / 1048576.0) + " MB", null,
            Loc.T("komet:hud-pools-frag", "{0} pools · {1} % frag",
                windowWorld.PoolCount, (windowWorld.Fragmentation * 100f).ToString("F0", ci)));
        DebugHud.Row(sb, Loc.Hud("view distance"), DebugHud.N(Vintagestory.Client.NoObf.ClientSettings.ViewDistance), null,
            Loc.T("komet:hud-blocks", "blocks"));

        Block(sb, Loc.Hud("shadows"), WriteShadowRows);
        Block(sb, Loc.Hud("gates"), WriteTickFirepitRows);

        WriteProfilerRows(sb, frame);
        if (!RendererProfiler.Enabled)
            DebugHud.Row(sb, Loc.Hud("renderers"), null, null,
                Loc.T("komet:gui-profiler-off",
                    "the renderer profiler is off - the Profiler page arms it (it costs frame time)"));
    }

    // ---- culling ----------------------------------------------------------------------

    private void ViewCulling(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;

        Block(sb, Loc.Hud("visibility sweep"), b => WriteSweepRows(b, frame));
        DebugHud.Row(sb, Loc.Hud("kernel"), null, null, CullKernel());
        DebugHud.Row(sb, Loc.Hud("gap merging"), null, null,
            FastCuller.GapMergeDrawRanges
                ? Loc.T("komet:gui-gapmerge-on", "on - {0} ranges bridged, {1} parts, {2} mio triangles",
                    DebugHud.N(FastCuller.StatRangesBridged), DebugHud.N(FastCuller.StatPartsBridged),
                    DebugHud.Mio(FastCuller.StatTrisBridged))
                : Loc.T("komet:gui-gapmerge-off", "off - only seamlessly adjacent ranges"));
        DebugHud.Row(sb, Loc.Hud("cell target"), DebugHud.N(FastCuller.PartsPerCellTarget), null,
            Loc.T("komet:gui-cells", "parts per grid cell · {0} cells skipped, {1} buckets",
                DebugHud.N(FastCuller.StatCellsSkipped), DebugHud.N(FastCuller.StatBucketsSkipped)));

        DebugHud.Section(sb, Loc.Hud("occlusion"));
        DebugHud.Row(sb, Loc.Hud("occlusion"), DebugHud.N(FastChunkCuller.StatPasses), DebugHud.Ms(FastChunkCuller.StatLastMs),
            Loc.T("komet:gui-occl-peak", "peak {0} ms · {1} threads · {2} rate limited",
                FastChunkCuller.StatPeakMs.ToString("F1", ci), JobScheduler.ActiveWorkers,
                DebugHud.N(FastChunkCuller.StatRateLimited))
            + (FastChunkCuller.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        DebugHud.Row(sb, Loc.Hud("chunks walked"), DebugHud.N(FastChunkCuller.StatChunksSnapshotted), null,
            Loc.T("komet:gui-grid-fallbacks", "{0} grid fallbacks", DebugHud.N(FastChunkCuller.StatGridFallbacks)));

        DebugHud.Section(sb, Loc.Hud("cross-check"));
        DebugHud.Row(sb, Loc.Hud("sweep check"), DebugHud.N(CullVerifier.StatChecked), null,
            CullVerifier.SampleEvery > 0
                ? (CullVerifier.StatMismatches > 0
                    ? Loc.T("komet:hud-mismatches", "!! {0} MISMATCHES (log)", DebugHud.N(CullVerifier.StatMismatches))
                    : Loc.T("komet:hud-all-vanilla", "all identical to vanilla"))
                : Loc.T("komet:gui-cullcheck-off", "off - '.komet toggle cullcheck' compares against vanilla"));
    }

    // ---- entities ---------------------------------------------------------------------

    private void ViewEntities(StringBuilder sb)
    {
        DebugHud.Section(sb, Loc.Hud("counts"));
        DebugHud.Row(sb, Loc.Hud("entities"), DebugHud.N(RuntimeStats.renderedEntities), null,
            Loc.T("komet:gui-entities-loaded-2", "drawn · {0} loaded", DebugHud.N(LoadedEntityCount())));

        Block(sb, Loc.Hud("budgets"), WriteEntityBudgetRows);
        Block(sb, Loc.Hud("animation"), b =>
        {
            WriteEntityAnimRows(b);
            if (EntityAnimPatches.Enabled) EntityAnimPatches.Write(b, CultureInfo.CurrentCulture);
        });

        DebugHud.Section(sb, Loc.Hud("block entities"));
        DebugHud.Row(sb, Loc.Hud("animatable gate"), DebugHud.N(AnimatableCullPatches.StatSkipped), null,
            Loc.T("komet:hud-of-calls-skipped", "of {0} calls skipped", DebugHud.N(AnimatableCullPatches.StatCalls))
            + (AnimatableCullPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));

        DebugHud.Section(sb, Loc.Hud("sync (server)"));
        WriteEntitySyncRows(sb);
    }

    /// <summary>The integrated server's side of the entity traffic - only meaningful in single
    /// player, and the row says so rather than showing zeros without explanation.</summary>
    private void WriteEntitySyncRows(StringBuilder sb)
    {
        if (capi != null && !capi.IsSinglePlayer)
        {
            DebugHud.Row(sb, Loc.Hud("entity sync"), null, null,
                Loc.T("komet:gui-server-remote", "a remote server - these only measure an integrated one"));
            return;
        }

        var sent = EntitySyncPatches.StatPositionsSent;
        var skipped = EntitySyncPatches.StatPositionsSkipped;
        DebugHud.Row(sb, Loc.Hud("positions"), DebugHud.N(sent), null,
            Loc.T("komet:gui-positions-skipped", "{0} skipped by distance · {1} tracking holds",
                DebugHud.N(skipped), DebugHud.N(EntitySyncPatches.StatHysteresisHolds))
            + (EntitySyncPatches.DistanceSendRate ? "" : Loc.T("komet:hud-off", " (OFF)")));
        DebugHud.Row(sb, Loc.Hud("attributes"), DebugHud.N(EntitySyncPatches.StatAttrPathsSent), null,
            Loc.T("komet:gui-attrs-skipped", "{0} unchanged paths dropped · {1} packets suppressed",
                DebugHud.N(EntitySyncPatches.StatAttrPathsSkipped),
                DebugHud.N(EntitySyncPatches.StatAttrPacketsSuppressed))
            + (EntitySyncPatches.AttributeNoOpSkip ? "" : Loc.T("komet:hud-off", " (OFF)")));
    }

    // ---- chunks -----------------------------------------------------------------------

    private void ViewChunks(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;
        SampleWorldForWindow();

        DebugHud.Section(sb, Loc.Hud("queues"));
        DebugHud.Row(sb, Loc.Hud("chunks"), DebugHud.N(windowWorld.LoadedChunks), null,
            Loc.T("komet:hud-queue-2", "queue {0}/{1}",
                DebugHud.N(RuntimeStats.chunksAwaitingTesselation), DebugHud.N(RuntimeStats.chunksAwaitingPooling)));
        DebugHud.Row(sb, Loc.Hud("received"), TesselationStats.ReceivedPerSecond.ToString("F0", ci) + "/s", null,
            Loc.T("komet:hud-from-server", "from server"));

        DebugHud.Section(sb, Loc.Hud("tesselation"));
        if (TesselationStats.TotalChunks > 0)
        {
            DebugHud.Row(sb, Loc.Hud("tesselation"), TesselationStats.ChunksPerSecond.ToString("F0", ci) + "/s",
                DebugHud.Ms(TesselationStats.MsPerChunk),
                Loc.T("komet:hud-per-chunk", "per chunk · {0} neighbours · {1} MB/s",
                    TesselationStats.NeighbourMsPerChunk.ToString("F1", ci),
                    TesselationStats.AllocMbPerSecond.ToString("F0", ci)));
            DebugHud.Row(sb, Loc.Hud("relight"), null, DebugHud.Ms(TesselationStats.RelightMsPerChunk),
                Loc.T("komet:gui-edge-share", "{0} % of the marks are edge-only",
                    TesselationStats.EdgeSharePercent.ToString("F0", ci)));
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("tesselation"), null, null, Loc.T("komet:gui-nothing-yet", "nothing measured yet"));
        }

        Block(sb, Loc.Hud("upload and pipeline"), WriteChunkRows);
        DebugHud.Row(sb, Loc.Hud("chunk upload"), null, DebugHud.Ms(FrameStats.AvgUploadMs),
            Loc.T("komet:hud-max", "max {0}", FrameStats.MaxUploadMs.ToString("F1", ci)));

        Block(sb, Loc.Hud("minimap and tasks"), WriteMinimapTaskRows);
    }

    // ---- memory -----------------------------------------------------------------------

    private void ViewMemory(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;

        DebugHud.WriteGc(sb);

        DebugHud.Section(sb, Loc.Hud("process"));
        DebugHud.Row(sb, Loc.Hud("gc heap"), DebugHud.N(FrameStats.GcHeapMb) + " MB", null,
            Loc.T("komet:gui-promoted", "{0} MB/s promoted, {1} MB per collection",
                FrameStats.PromotedMbPerSecond.ToString("F0", ci), FrameStats.PromotedMbPerGc.ToString("F1", ci)));
        try
        {
            using var proc = Process.GetCurrentProcess();
            DebugHud.Row(sb, Loc.Hud("working set"), DebugHud.N(proc.WorkingSet64 / 1048576.0) + " MB", null,
                Loc.T("komet:gui-rss", "resident, the whole process"));
        }
        catch
        {
            // A refused /proc read is not worth a row of its own.
        }

        Block(sb, Loc.Hud("allocation sources"), b =>
        {
            if (ClientAllocPatches.Enabled && ClientAllocPatches.Entries.Count > 0)
                ClientAllocPatches.Write(b, ci);
            if (ServerAllocPatches.Enabled && ServerAllocPatches.Entries.Count > 0)
                ServerAllocPatches.Write(b, ci);
            if (AllocSampler.Enabled) AllocSampler.Write(b, ci);
            else
                DebugHud.Row(b, Loc.Hud("alloc sampler"), null, null,
                    Loc.T("komet:gui-allocsample-off",
                        "off - '.komet toggle allocsample' names the allocating types"));
        });

        DebugHud.Section(sb, Loc.Hud("pools"));
        WriteVramRow(sb);
        WritePoolRows(sb);
    }

    /// <summary>The three pools whose whole purpose is to keep bytes out of the collector.</summary>
    private static void WritePoolRows(StringBuilder sb)
    {
        var hits = MeshRecyclerPatches.StatHits;
        var misses = MeshRecyclerPatches.StatMisses;
        var total = hits + misses;
        DebugHud.Row(sb, Loc.Hud("mesh recycler"),
            total > 0 ? (100.0 * hits / total).ToString("F0", CultureInfo.CurrentCulture) + " %" : null, null,
            Loc.T("komet:gui-recycler", "{0} of {1} · {2} MB held · {3} evicted",
                DebugHud.N(hits), DebugHud.N(total),
                DebugHud.N(MeshRecyclerPatches.HeldBytes / 1048576.0),
                DebugHud.N(MeshRecyclerPatches.StatEvicted))
            + (MeshRecyclerPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));

        var eHits = TightClonePatches.StatExtrasHits;
        var eTotal = eHits + TightClonePatches.StatExtrasMisses;
        DebugHud.Row(sb, Loc.Hud("extras pool"),
            eTotal > 0 ? (100.0 * eHits / eTotal).ToString("F0", CultureInfo.CurrentCulture) + " %" : null, null,
            Loc.T("komet:gui-extras", "{0} of {1} · {2} dropped",
                DebugHud.N(eHits), DebugHud.N(eTotal), DebugHud.N(TightClonePatches.StatExtrasDropped))
            + (TightClonePatches.PoolExtras ? "" : Loc.T("komet:hud-off", " (OFF)")));

        DebugHud.Row(sb, Loc.Hud("tight clone"), DebugHud.N(TightClonePatches.StatClones), null,
            Loc.T("komet:gui-clone-saved", "{0} MB not copied",
                DebugHud.N(TightClonePatches.StatBytesSaved / 1048576.0))
            + (TightClonePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
    }

    // ---- threads ----------------------------------------------------------------------

    /// <summary>Reused across refreshes - the page is composed four times a second and a
    /// monitor that allocates is one more thing in the frame it reports on.</summary>
    private static readonly JobScheduler.WorkerSnapshot[] workerRows =
        new JobScheduler.WorkerSnapshot[JobScheduler.MaxWorkers];

    private static readonly JobKind[] WorkloadOrder =
    {
        JobKind.Cull, JobKind.MeshPrep, JobKind.ChunkPrep, JobKind.Occlusion, JobKind.Hud, JobKind.Warmup,
    };

    private static string WorkloadName(JobKind k) => k switch
    {
        JobKind.Cull => Loc.T("komet:gui-job-cull", "culling"),
        JobKind.MeshPrep => Loc.T("komet:gui-job-meshprep", "mesh prep"),
        JobKind.ChunkPrep => Loc.T("komet:gui-job-chunkprep", "chunk prep"),
        JobKind.Occlusion => Loc.T("komet:gui-job-occlusion", "occlusion"),
        JobKind.Hud => Loc.T("komet:gui-job-hud", "hud raster"),
        _ => Loc.T("komet:gui-job-warmup", "anim prewarm"),
    };

    /// <summary>The state word a worker row shows. Deliberately the vocabulary of what this mod
    /// actually runs: chunk generation is the server's worldgen threads, chunk loading is the
    /// network thread, and tesselation, meshing and the GPU upload are engine threads no mod can
    /// schedule - so there is no GENERATING, TESSELLATING or UPLOADING to show, and inventing
    /// one would be a lie in the one place people look to find out where the time went.</summary>
    private static string StateName(WorkerState s) => s switch
    {
        WorkerState.Culling => "CULLING",
        WorkerState.Meshing => "MESHING",
        WorkerState.Loading => "LOADING",
        WorkerState.Occluding => "OCCLUDING",
        WorkerState.Rastering => "RASTERING",
        WorkerState.Warming => "WARMUP",
        WorkerState.Waiting => "WAITING",
        WorkerState.Parked => "PARKED",
        _ => "IDLE",
    };

    /// <summary>"chunk 124,-52" for a job that carries one, blank otherwise. The dedup key IS
    /// the engine's index3d, so it decodes with the map's own multipliers.</summary>
    private static string WhereOf(long key)
    {
        if (key == long.MinValue || JobScheduler.KeyMulX <= 0) return "";
        var bare = key & 0x7FFFFFFFFFFFFFFFL;
        var mulX = JobScheduler.KeyMulX;
        var mulZ = JobScheduler.KeyMulZ;
        var x = (int)(bare % mulX);
        var rest = bare / mulX;
        var z = (int)(rest % mulZ);
        var y = (int)(rest / mulZ);
        return Loc.T("komet:gui-job-chunk", "chunk {0},{1},{2}", x, y, z);
    }

    private void ViewThreads(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;
        var toMs = 1000.0 / Stopwatch.Frequency;

        DebugHud.Section(sb, Loc.Hud("worker pool"));
        var total = JobScheduler.WorkerCount;
        var active = JobScheduler.ActiveWorkers;
        var busy = JobScheduler.BusyWorkers;
        DebugHud.Row(sb, Loc.Hud("workers"), busy + "/" + active, null,
            Loc.T("komet:gui-workers", "busy of {0} awake ({1} in the pool) · {2} idle · {3} % utilised",
                active, total, Math.Max(0, active - busy),
                (100.0 * JobScheduler.Utilisation).ToString("F0", ci)));
        DebugHud.Row(sb, Loc.Hud("job queue"), DebugHud.N(JobScheduler.PendingJobs), null,
            Loc.T("komet:gui-jobs-rate", "queued · {0}/s · {1} done · {2} cancelled · {3} duplicates dropped",
                JobScheduler.JobsPerSecond.ToString("F0", ci), DebugHud.N(JobScheduler.StatCompleted),
                DebugHud.N(JobScheduler.StatCancelled), DebugHud.N(JobScheduler.StatDuplicates)));
        DebugHud.Row(sb, Loc.Hud("batches"), DebugHud.N(JobScheduler.StatBatches), null,
            Loc.T("komet:gui-batches", "fork/join · {0} ran inline ({1} of those contended)",
                DebugHud.N(JobScheduler.StatInline), DebugHud.N(JobScheduler.StatContendedInline)));
        // The one number that says whether the parallel batch is helping or just moving the
        // wait around: time a caller spent waiting for its own workers.
        DebugHud.Row(sb, Loc.Hud("caller wait"), null, DebugHud.Ms(JobScheduler.StatWaitTicks * toMs),
            Loc.T("komet:gui-since-reset", "since the last reset")
            + (JobScheduler.PriorityLowered ? Loc.T("komet:gui-nice", " · priority lowered") : ""));
        DebugHud.Row(sb, Loc.Hud("handoff"), DebugHud.N(JobScheduler.HandoffDepth), null,
            Loc.T("komet:gui-handoff", "waiting for the main thread · {0} run · {1} frames deferred",
                DebugHud.N(JobScheduler.StatHandoffs), DebugHud.N(JobScheduler.StatHandoffDeferrals)));

        DebugHud.Section(sb, Loc.Hud("workers"));
        JobScheduler.SnapshotInto(workerRows, out var n);
        for (var i = 0; i < n; i++)
        {
            var w = workerRows[i];
            var busyRow = w.State is not (WorkerState.Idle or WorkerState.Parked);
            DebugHud.Row(sb, "W" + w.Index.ToString("00", ci), StateName(w.State),
                busyRow ? DebugHud.Ms(w.Ms) : null,
                (busyRow ? WhereOf(w.Key) : "")
                + (w.Nice ? Loc.T("komet:gui-nice-worker", " (background)") : ""));
        }
        if (n == 0) sb.Append(Loc.T("komet:gui-no-workers", " the worker pool is not running\n"));

        DebugHud.Section(sb, Loc.Hud("workload"));
        foreach (var kind in WorkloadOrder)
        {
            var done = JobScheduler.JobsOf(kind);
            if (done == 0 && JobScheduler.QueuedOf(kind) == 0) continue;
            DebugHud.Row(sb, DebugHud.Label(WorkloadName(kind)), DebugHud.N(JobScheduler.QueuedOf(kind)),
                DebugHud.Ms(JobScheduler.AvgMsOf(kind)),
                Loc.T("komet:gui-workload", "queued · avg · peak {0} · {1} done",
                    DebugHud.Ms(JobScheduler.PeakMsOf(kind)).TrimStart(), DebugHud.N(done)));
        }

        DebugHud.Section(sb, Loc.Hud("machine"));
        DebugHud.Row(sb, Loc.Hud("cores"), CpuTopology.PhysicalCores + "/" + CpuTopology.LogicalCores, null,
            Loc.T("komet:gui-cores-source", "physical/logical ({0})", CpuTopology.Source));
        DebugHud.Row(sb, Loc.Hud("cpu cores"),
            (100.0 * FrameStats.CpuCoresBusy / Math.Max(1, Environment.ProcessorCount)).ToString("F0", ci) + " %", null,
            Loc.T("komet:hud-cores-busy", "{0} of {1} cores busy",
                FrameStats.CpuCoresBusy.ToString("F1", ci), Environment.ProcessorCount));

        DebugHud.Section(sb, Loc.Hud("main thread queue"));
        WriteMainTaskRow(sb, ci, MainThreadTaskPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
    }

    /// <summary>The task drain's counters. Two pages show them - the CPU page inside its
    /// attribution block, the threads page next to the queues - and they used to say it twice.</summary>
    private static void WriteMainTaskRow(StringBuilder sb, CultureInfo ci, string note)
        => DebugHud.Row(sb, Loc.Hud("mt tasks"), DebugHud.N(MainThreadTaskPatches.StatTasks),
            DebugHud.Ms(FrameStats.AvgMainTaskMs),
            Loc.T("komet:gui-task-budget", "budget {0} ms · {1} frames capped · {2} deferred",
                MainThreadTaskPatches.BudgetMs.ToString("0.#", ci),
                DebugHud.N(MainThreadTaskPatches.StatBudgetCuts),
                DebugHud.N(MainThreadTaskPatches.StatDeferredTasks)) + note);

    // ---- cache ------------------------------------------------------------------------

    private void ViewCache(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;

        DebugHud.Section(sb, Loc.Hud("sweep cache"));
        DebugHud.Row(sb, Loc.Hud("rebuilds"), DebugHud.N(rebuildsPerFrame?.PerFrame ?? 0), DebugHud.Ms(RebuildMsPerFrame()),
            Loc.T("komet:gui-per-frame", "per frame"));
        DebugHud.Row(sb, Loc.Hud("cache kept"), DebugHud.N(FastCuller.StatIncInserts), null,
            Loc.T("komet:gui-inc", "inserts · {0} removals - a cache kept instead of rebuilt",
                DebugHud.N(FastCuller.StatIncRemovals)));
        DebugHud.Row(sb, Loc.Hud("cells dropped"), DebugHud.N(FastCuller.StatCellsSkipped), null,
            Loc.T("komet:gui-buckets", "{0} buckets, {1} pools skipped whole",
                DebugHud.N(FastCuller.StatBucketsSkipped), DebugHud.N(FastCuller.StatPoolsSkipped)));

        DebugHud.Section(sb, Loc.Hud("tesselation cache"));
        var pipeTotal = WindowPrebuilder.StatHits + WindowPrebuilder.StatMisses;
        DebugHud.Row(sb, Loc.Hud("window pipe"),
            pipeTotal > 0 ? (100.0 * WindowPrebuilder.StatHits / pipeTotal).ToString("F0", ci) + " %" : null, null,
            Loc.T("komet:gui-window-pipe", "{0} of {1} · {2} stale · {3} validated",
                DebugHud.N(WindowPrebuilder.StatHits), DebugHud.N(pipeTotal),
                DebugHud.N(WindowPrebuilder.StatStale), DebugHud.N(WindowPrebuilder.StatValidated)));
        DebugHud.Row(sb, Loc.Hud("prefetch"), DebugHud.N(TesselationPatches.StatPrefetchedUnpacks), null,
            Loc.T("komet:hud-chunks-preunpacked", "chunks pre-unpacked"));

        DebugHud.Section(sb, Loc.Hud("memory pools"));
        WritePoolRows(sb);

        DebugHud.Section(sb, Loc.Hud("firepit gate"));
        DebugHud.Row(sb, Loc.Hud("firepit gate"), DebugHud.N(FirepitPatches.StatSkipped), null,
            Loc.T("komet:hud-skipped-cache-vanilla", "skipped, {0} cache, {1} vanilla",
                DebugHud.N(FirepitPatches.StatFastPath), DebugHud.N(FirepitPatches.StatNearVanilla))
            + (FirepitPatches.FastPathBroken ? Loc.T("komet:hud-cache-broken", " !! CACHE BROKEN (log)") : ""));

        DebugHud.Section(sb, Loc.T("komet:gui-sec-edge-repairs", "edge repairs"));
        DebugHud.Row(sb, Loc.Hud("edge coalesce"), DebugHud.N(EdgeCoalescePatches.StatAbsorbed), null,
            Loc.T("komet:hud-saved-flushed-open", "saved, {0} flushed, {1} open",
                DebugHud.N(EdgeCoalescePatches.StatFlushed), DebugHud.N(EdgeCoalescePatches.PendingCount))
            + (EdgeCoalescePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
    }

    // ---- hitches ----------------------------------------------------------------------

    private void ViewHitches(StringBuilder sb)
    {
        // The hitch log's own report, unchanged - it is what '.komet hitch' prints and what the
        // full report carries, and a second rendering of it would be a second thing to keep
        // right. Rebuilt at most once a second: it only changes when a frame hitches.
        sb.Append(Slow(KometView.Hitches, HitchLog.BuildReport));
    }

    // ---- profiler ---------------------------------------------------------------------

    private void ViewProfiler(StringBuilder sb, double frame)
    {
        var ci = CultureInfo.CurrentCulture;

        DebugHud.Section(sb, Loc.Hud("armed"));
        var diag = ActiveDiagnostics();
        DebugHud.Row(sb, Loc.Hud("diagnostics"), null, null,
            diag ?? Loc.T("komet:gui-no-diagnostics", "none - the Toggles page arms them"));

        DebugHud.Section(sb, Loc.Hud("most expensive renderers"));
        if (RendererProfiler.Enabled || RendererProfiler.Count > 0)
        {
            RendererProfiler.Write(sb, 20);
            DebugHud.Row(sb, "= " + Loc.Hud("all together"), null, DebugHud.Ms(RendererProfiler.TotalMs),
                Loc.T("komet:hud-names", "{0} names", RendererProfiler.Count));
            DebugHud.Row(sb, Loc.Hud("of which measured"),
                RendererProfiler.StatWrapped + "/" + RendererProfiler.StatTotal, null,
                RendererProfiler.Enabled
                    ? null
                    : Loc.T("komet:gui-before-only", "only the before stage - the full set is '.komet toggle profiler'"));
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("renderers"), null, null,
                Loc.T("komet:gui-profiler-off",
                    "the renderer profiler is off - the Profiler page arms it (it costs frame time)"));
        }

        DebugHud.Section(sb, Loc.Hud("tick listeners"));
        if (TickProfiler.Enabled && TickProfiler.StatWrapped > 0)
            TickProfiler.Write(sb, 20, ci, FrameStats.GameTickMs);
        else
            DebugHud.Row(sb, Loc.Hud("tick listener"), null, null,
                Loc.T("komet:gui-tickprofiler-off", "the tick profiler is off - '.komet toggle tickprofiler'"));

        DebugHud.Section(sb, Loc.Hud("main thread tasks"));
        if (MainThreadTaskPatches.Enabled) MainThreadTaskPatches.Write(sb, 20, ci);
        else DebugHud.Row(sb, Loc.Hud("mt tasks"), null, null,
            Loc.T("komet:gui-mtt-off", "attribution off - '.komet toggle mtt'"));

        DebugHud.Section(sb, Loc.Hud("dirty marks"));
        DebugHud.Row(sb, Loc.Hud("dirty marks"),
            RetessSourcePatches.MarksPerSecond.ToString("F0", ci) + "/s", null,
            RetessSourcePatches.EdgeMarksPerSecond.ToString("F0", ci)
            + Loc.T("komet:hud-edge-marks", "/s edge, '.komet retess'"));

        Block(sb, Loc.Hud("block packets (server)"), b => PacketSourcePatches.Write(b, ci));

        DebugHud.Section(sb, Loc.Hud("mods"));
        DebugHud.Row(sb, Loc.Hud("mods"), DebugHud.Pct(ModProfiler.TotalMs, frame), DebugHud.Ms(ModProfiler.TotalMs),
            Loc.T("komet:gui-mods-page", "{0} loaded - the Mods page has the table", ModProfiler.ModCount));
    }

    // ---- toggles ----------------------------------------------------------------------

    /// <summary>
    /// What the switches beside this text add up to. The window draws the toggles themselves as
    /// switches - this is the summary underneath them, and it exists because the one question a
    /// page of forty switches cannot answer by looking at it is "which of these am I currently
    /// NOT running", which is the question every visual bug report starts from.
    /// </summary>
    private void ViewToggles(StringBuilder sb)
    {
        var reg = Toggles;
        int on = 0, off = 0;
        var drawingOff = new List<string>(8);
        foreach (var e in reg.Entries)
        {
            var isOn = e.IsOn();
            if (isOn) on++; else off++;
            if (!isOn && e.Visual) drawingOff.Add(e.Key);
        }

        DebugHud.Section(sb, Loc.Hud("state"));
        DebugHud.Row(sb, Loc.Hud("systems"), on + "/" + (on + off), null,
            Loc.T("komet:gui-toggles-on", "on right now"));
        DebugHud.Row(sb, Loc.Hud("drawing off"), DebugHud.N(drawingOff.Count), null,
            drawingOff.Count == 0
                ? Loc.T("komet:gui-nothing-vanilla", "nothing handed back to vanilla")
                : string.Join(", ", drawingOff));
        if (safeMode)
            DebugHud.Row(sb, "!! " + Loc.Hud("safemode"), null, null,
                Loc.T("komet:gui-safemode-holds", "safemode holds every [draws] system at vanilla"));

        sb.Append('\n').Append(Loc.T("komet:gui-toggle-hint", "Flip one at a time and watch the frametime view. [draws] marks the systems safemode switches off in one go - a visual artefact is bisected among those and nowhere else. What a flip did is answered here and in the chat."));
    }

    // ---- config -----------------------------------------------------------------------

    /// <summary>
    /// The settings, read off the live config object by reflection - the same way
    /// <see cref="ConfigDelta"/> does it, and for the same reason: a hand-written list stops
    /// covering the setting added after it, which is always the one being asked about. The ones
    /// that differ from the shipped default are marked, because that line is what a field report
    /// needs and what nobody remembers to write down.
    /// </summary>
    private string ConfigText()
    {
        var sb = new StringBuilder(6144);
        var ci = CultureInfo.InvariantCulture;

        DebugHud.Section(sb, Loc.Hud("file"));
        DebugHud.Row(sb, Loc.Hud("config"), null, null, ConfigFile);
        DebugHud.Row(sb, Loc.Hud("layout"), KometConfig.Current.ToString(ci), null,
            Loc.T("komet:gui-layout-note", "a newer layout backs the old file up and regenerates it"));
        var delta = ConfigDelta(config);
        DebugHud.Row(sb, Loc.Hud("differing"), null, null, delta ?? Loc.T("komet:gui-all-default", "nothing - all defaults"));
        DebugHud.Row(sb, Loc.Hud("note"), null, null,
            Loc.T("komet:gui-config-live", "changes here need a restart; the Toggles page changes systems live"));

        DebugHud.Section(sb, Loc.Hud("settings"));
        var defaults = new KometConfig();
        foreach (var p in typeof(KometConfig).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite) continue;
            object mine = p.GetValue(config), std = p.GetValue(defaults);
            var differs = !Equals(mine, std) && p.Name != nameof(KometConfig.ConfigVersion);
            sb.Append(differs ? " * " : "   ").Append(p.Name).Append(" = ")
              .Append(Convert.ToString(mine, ci));
            if (differs) sb.Append("   (default ").Append(Convert.ToString(std, ci)).Append(')');
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ---- stress -----------------------------------------------------------------------

    private void ViewStress(StringBuilder sb)
    {
        var ci = CultureInfo.CurrentCulture;

        DebugHud.Section(sb, Loc.Hud("stress test"));
        if (StressTest.Running)
        {
            var slice = StressTest.SliceIndex + 1;
            var total = StressTest.SliceCount;
            var round = StressTest.SystemCount > 0
                ? Math.Min(StressTest.RoundCount, StressTest.SliceIndex / (StressTest.SystemCount * 2) + 1)
                : 1;
            DebugHud.Row(sb, Loc.Hud("phase"), null, null,
                StressTest.CurrentPhase ?? Loc.T("komet:gui-baseline", "baseline"));
            DebugHud.Row(sb, Loc.Hud("round"), round + "/" + StressTest.RoundCount, null,
                Loc.T("komet:gui-slice", "slice {0} of {1}, {2} s each", slice, total,
                    StressTest.SecondsPerSlice.ToString("0.#", ci)));
            DebugHud.Row(sb, Loc.Hud("progress"), null, null,
                DebugHud.Bar(slice, Math.Max(1, total)) + "  "
                + (100.0 * slice / Math.Max(1, total)).ToString("F0", ci) + " %");

            (var frames, var avgMs, var worstMs) = StressTest.CurrentSliceStats;
            DebugHud.Row(sb, Loc.Hud("this slice"), DebugHud.N(frames), DebugHud.Ms(avgMs),
                frames == 0
                    ? Loc.T("komet:gui-settling", "settling - the first frames after a flip are dropped")
                    : Loc.T("komet:gui-worst-so-far", "worst {0} ms so far", worstMs.ToString("F1", ci)));
        }
        else
        {
            DebugHud.Row(sb, Loc.Hud("stress test"), null, null,
                Loc.T("komet:gui-stress-idle", "not running - Start below, or '.komet stress [seconds]'"));
            DebugHud.Row(sb, Loc.Hud("method"), null, null,
                Loc.T("komet:gui-stress-method",
                    "baseline, system, baseline, system ... every test slice has a baseline on both"));
            DebugHud.Row(sb, "", null, null,
                Loc.T("komet:gui-stress-method-2",
                    "sides, so the scene drifting while you play cancels out. Moving is fine."));
            if (safeMode)
                DebugHud.Row(sb, "!! " + Loc.Hud("safemode"), null, null,
                    Loc.T("komet:msg-safemode-blocks",
                        "Safemode is on - take it back with '.komet safemode' first, then test."));
        }

        var table = StressTest.LiveReport();
        if (table != null)
        {
            DebugHud.Section(sb, StressTest.Running
                ? Loc.Hud("results so far")
                : Loc.Hud("last run"));
            sb.Append(table).Append('\n');
        }
    }

    // ---- conflicts --------------------------------------------------------------------

    private string ConflictText()
    {
        var sb = new StringBuilder(2048);

        DebugHud.Section(sb, Loc.Hud("engine"));
        DebugHud.Row(sb, Loc.Hud("version"), null, null, Vintagestory.API.Config.GameVersion.LongGameVersion);
        DebugHud.Row(sb, Loc.Hud("checked"), DebugHud.N(PatchGuard.MethodsChecked), null,
            Loc.T("komet:gui-unverified", "{0} not matching the verified build · {1} scans",
                DebugHud.N(PatchGuard.MethodsUnverified), DebugHud.N(PatchGuard.Scans)));
        if (MeasurementPatches.SkippedBrackets.Count > 0)
            DebugHud.Row(sb, Loc.Hud("not measured"), null, null,
                string.Join(", ", MeasurementPatches.SkippedBrackets));

        if (ForeignClient.Findings.Count > 0)
        {
            DebugHud.Section(sb, "!! " + Loc.Hud("incompatible client"));
            DebugHud.Row(sb, Loc.Hud("client"), null, null, ForeignClient.Describe());
            foreach (var f in ForeignClient.Findings)
                DebugHud.Row(sb, "", null, null, f.How);
            DebugHud.Row(sb, "", null, null,
                Loc.T("komet:gui-foreign-note",
                    "it replaces the same engine code komet does - the figures do not describe komet alone"));
        }

        DebugHud.Section(sb, Loc.Hud("patch guard"));
        sb.Append(PatchGuard.ReportLines());
        return sb.ToString().TrimEnd('\n');
    }

    // ---- shared sampling --------------------------------------------------------------

    private DebugHud.WorldSample windowWorld;
    private int windowDrawCalls;
    private long windowLastDrawCalls;
    private long windowSampledAt;

    /// <summary>
    /// The chunk renderer walk, on the window's behalf, at most four times a second however
    /// often the pages ask for it. It is the same static <see cref="DebugHud.SampleWorld(ICoreClientAPI, ref DebugHud.WorldSample)"/> the
    /// overlay calls - GetStats and CalcFragmentation both traverse every mesh pool, which is
    /// the one genuinely expensive thing any of these pages reads, so it is the one thing they
    /// share a result of.
    /// </summary>
    private void SampleWorldForWindow()
    {
        var now = Stopwatch.GetTimestamp();
        if (windowSampledAt != 0 && (now - windowSampledAt) * 4 < Stopwatch.Frequency) return;
        windowSampledAt = now;

        DebugHud.SampleWorld(capi, ref windowWorld);

        // Draw calls only ever increment, so the per-frame figure is the delta. The engine's own
        // debug screen zeroes the counter, which would make it negative - ignore those samples.
        var count = RuntimeStats.drawCallsCount;
        var delta = count - windowLastDrawCalls;
        if (delta >= 0 && delta < 1_000_000 && windowLastDrawCalls != 0) windowDrawCalls = (int)delta;
        windowLastDrawCalls = count;
    }

    /// <summary>Entities the client holds, not just the ones drawn. Cheap - a dictionary's own
    /// count - but it needs a world, and the window opens without one.</summary>
    private int LoadedEntityCount()
    {
        try { return capi?.World?.LoadedEntities?.Count ?? 0; }
        catch { return 0; }
    }
}
