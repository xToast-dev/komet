using System;
using System.Globalization;
using System.Text;
using Komet.Culling;
using Komet.Guard;
using Komet.Measure;
using Komet.Runtime;
using Vintagestory.API.Client;

namespace Komet;

/// <summary>
/// The reporting half of the mod: the komet section of the HUD and the .komet chat text.
/// Everything here only reads counters - nothing in this file may change behaviour.
/// </summary>
public partial class KometModSystem
{
    /// <summary>
    /// Everything needed to judge a play session, in one block: environment, the settings that
    /// differ from the defaults, the frame accounting, and the full hitch log.
    ///
    /// This exists because every diagnosis so far has been assembled by hand out of three or
    /// four separate commands, and the one that mattered was regularly the one not run. A single
    /// block that is complete by construction cannot be partially reported.
    /// </summary>
    private string BuildFullReport()
    {
        var ci = CultureInfo.CurrentCulture;
        var sb = new StringBuilder(6144);

        sb.Append("==================== Komet report ").Append(KometVersion.Display(Mod.Info.Version))
          .Append(" ====================\n");

        // ---- environment ----
        // The GC pair is deliberately both halves: what was asked for and what the runtime
        // actually does. A DOTNET_gcServer that never reached the process looks exactly like
        // one that was never set, and that difference has already cost one wrong conclusion.
        var asked = Environment.GetEnvironmentVariable("DOTNET_gcServer")
                    ?? Environment.GetEnvironmentVariable("COMPlus_gcServer");
        sb.AppendFormat(ci, "environment: {0} logical cores, .net {1}, {2}\n",
            Environment.ProcessorCount, Environment.Version, Environment.OSVersion.VersionString);
        sb.AppendFormat(ci, "gc: mode {0}, requested {1}, latency {2} | uptime {3:F0} min\n",
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
            asked ?? "(nothing set)",
            System.Runtime.GCSettings.LatencyMode,
            uptime.Elapsed.TotalMinutes);
        // Physical cores decide every thread budget in the mod (cull, occlusion, worldgen);
        // the source says whether the OS answered or the rule of thumb did - a laptop's
        // 2c/4t reported as four cores was the whole thread oversubscription story of 02.09.
        sb.AppendFormat(ci, "cores: {0} physical of {1} logical ({2})\n",
            CpuTopology.PhysicalCores, CpuTopology.LogicalCores, CpuTopology.Source);
        sb.AppendFormat(ci, "cull threads {0}, occlusion threads {1}, safemode {2}\n",
            FastCuller.Workers.ThreadCount + 1, FastChunkCuller.Workers.ThreadCount + 1,
            safeMode ? "ON - these numbers say nothing about the mod!" : "aus");
        if (MeasurementPatches.SkippedBrackets.Count > 0)
            sb.AppendFormat(ci, "measurement without: {0} (the engine build differs, those lines stay empty)\n",
                string.Join(", ", MeasurementPatches.SkippedBrackets));
        // Is anybody else on komet's methods, and is this the engine komet was verified
        // against - the two questions a field report from a modified client has to answer
        // before any of its numbers mean anything.
        sb.Append(PatchGuard.ReportLines());

        // Display pacing, because a field report showed 36 fps at 7% CPU with the GPU just
        // over the refresh budget - vsync quantising into half-rate frames looks exactly like
        // "nichts ist ausgelastet und trotzdem langsam". The three deciding facts belong in
        // every report; a failed display query must never take the report down.
        try
        {
            var platform = Vintagestory.Client.ScreenManager.Platform
                as Vintagestory.Client.NoObf.ClientPlatformWindows;
            var refresh = "?";
            if (platform?.window != null)
                refresh = OpenTK.Windowing.Desktop.Monitors.GetMonitorFromWindow(platform.window)
                    .CurrentVideoMode.RefreshRate.ToString(ci);
            var vsync = Vintagestory.Client.NoObf.ClientSettings.VsyncMode;
            sb.AppendFormat(ci, "display: vsync {0}, fps limit {1:F0}, monitor {2} Hz\n",
                vsync == 1 ? "an" : vsync == 0 ? "aus" : "mode " + vsync,
                Vintagestory.Client.ScreenManager.Platform?.MaxFps ?? 0, refresh);
        }
        catch (Exception e)
        {
            sb.Append("display: cannot be queried (").Append(e.GetType().Name).Append(")\n");
        }

        var delta = ConfigDelta(config);
        sb.Append("config: ").Append(ConfigFile).Append(" layout ").Append(KometConfig.Current)
          .Append(", differing from the default: ").Append(delta ?? "none").Append('\n');

        // ---- the frame ----
        sb.Append("\n---- frame ----\n").Append(BuildStats()).Append('\n');

        // ---- hitches ----
        sb.Append("\n---- hitches ----\n").Append(HitchLog.BuildReport()).Append('\n');

        // ---- dirty marks, only when the sampler is on ----
        if (Patches.RetessSourcePatches.SampleSources)
            sb.Append("\n---- dirty marks ----\n")
              .Append(Patches.RetessSourcePatches.BuildReport()).Append('\n');

        sb.Append("==================== end ====================");
        return sb.ToString();
    }

    /// <summary>
    /// The config properties that differ from a freshly constructed default, as "name=value".
    /// Reflected rather than listed by hand: a hand-written list silently stops covering the
    /// setting added after it, which is always the one being asked about.
    /// </summary>
    internal static string ConfigDelta(KometConfig live)
    {
        var defaults = new KometConfig();
        var parts = new System.Collections.Generic.List<string>(8);
        foreach (var p in typeof(KometConfig).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite) continue;
            // Bookkeeping, not a setting: it carries the config layout version and is stamped
            // on load, so it differs from a freshly constructed default every single time,
            // which made the line read as "something is off" in a session where nothing was.
            if (p.Name == nameof(KometConfig.ConfigVersion)) continue;
            object mine = p.GetValue(live), std = p.GetValue(defaults);
            if (Equals(mine, std)) continue;
            parts.Add(p.Name + "=" + Convert.ToString(mine, CultureInfo.InvariantCulture));
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The rows that only mean something when the optimisations are actually running.</summary>
    private void WriteKometSection(StringBuilder sb, double frameMs)
    {
        if (Patches.RendererProfiler.Enabled)
        {
            DebugHud.Section(sb, Loc.Hud("most expensive renderers"));
            Patches.RendererProfiler.Write(sb, 8);
            // Against the stage totals this says whether the list above explains the frame.
            // The firepit renderer was found exactly because it did not.
            DebugHud.Row(sb, "= " + Loc.Hud("all together"), null, DebugHud.Ms(Patches.RendererProfiler.TotalMs),
                Loc.T("komet:hud-names", "{0} names", Patches.RendererProfiler.Count));
            DebugHud.Row(sb, Loc.Hud("of which measured"),
                Patches.RendererProfiler.StatWrapped + "/" + Patches.RendererProfiler.StatTotal);
        }

        DebugHud.Section(sb, "komet");
        WriteKometWarnings(sb, frameMs);

        // -- the sweep --
        DebugHud.Row(sb, Loc.Hud("visibility"), DebugHud.Pct(FrameStats.AvgCullMs, frameMs), DebugHud.Ms(FrameStats.AvgCullMs),
            CullKernelShort());
        DebugHud.Row(sb, Loc.Hud("parts tested"), DebugHud.N(partsPerFrame?.PerFrame ?? 0), null,
            Loc.T("komet:hud-cells-skipped", "{0} cells skipped", DebugHud.N(cellsSkippedPerFrame?.PerFrame ?? 0)));
        DebugHud.Row(sb, Loc.Hud("of which rebuild"), DebugHud.N(rebuildsPerFrame?.PerFrame ?? 0), DebugHud.Ms(RebuildMsPerFrame()),
            FastCuller.StatIncInserts + FastCuller.StatIncRemovals > 0
                ? DebugHud.N(FastCuller.StatIncInserts) + " +/" + DebugHud.N(FastCuller.StatIncRemovals) + Loc.T("komet:hud-incremental", " - incremental")
                : null);
        // raw running totals: if these stay at zero the patch is not firing at all, which is a
        // different problem from the smoothed per-frame figures reading zero
        DebugHud.Row(sb, Loc.Hud("sweeps/frame"), DebugHud.N(sweepsPerFrame?.PerFrame ?? 0), null,
            Loc.T("komet:hud-parallel-batches", "{0} parallel batches", DebugHud.N(batchesPerFrame?.PerFrame ?? 0)));
        if (CullVerifier.SampleEvery > 0 || CullVerifier.StatMismatches > 0)
            DebugHud.Row(sb, Loc.Hud("sweep check"), DebugHud.N(CullVerifier.StatChecked), null,
                CullVerifier.StatMismatches > 0
                    ? Loc.T("komet:hud-mismatches", "!! {0} MISMATCHES (log)", DebugHud.N(CullVerifier.StatMismatches))
                    : Loc.T("komet:hud-all-vanilla", "all identical to vanilla"));
        var raw = rawRangesPerFrame?.PerFrame ?? 0;
        var emitted = rangesPerFrame?.PerFrame ?? 0;
        DebugHud.Row(sb, Loc.Hud("draw ranges"), DebugHud.N(emitted), null,
            Loc.T("komet:hud-of-raw", "of {0} ({1}x)", DebugHud.N(raw),
                (emitted > 0 ? raw / emitted : 1).ToString("F1", CultureInfo.CurrentCulture)));
        DebugHud.Row(sb, Loc.Hud("occlusion"), null, DebugHud.Ms(FastChunkCuller.StatLastMs), Loc.T("komet:hud-worker-thread", "worker thread"));

        // -- shadows --
        var shadowFrames = Patches.ShadowThrottlePatches.FarRendered + Patches.ShadowThrottlePatches.FarSkipped;
        if (shadowFrames > 0)
            // "schatten-takt", not "schatten fern": the frame-aufteilung block above already
            // has a row named schatten fern that means milliseconds - one name, one meaning
            DebugHud.Row(sb, Loc.Hud("shadow cadence"),
                "1/" + Patches.ShadowThrottlePatches.FarInterval + "-1/" + Patches.ShadowThrottlePatches.FarMaxSkip, null,
                (100.0 * Patches.ShadowThrottlePatches.FarSkipped / shadowFrames).ToString("F0", CultureInfo.CurrentCulture)
                + Loc.T("komet:hud-far-cascades-saved", " % far cascades saved"));
        if (Patches.ShadowPatches.ShadowDistance > 0)
        {
            DebugHud.Row(sb, Loc.Hud("shadows to"), DebugHud.N(Patches.ShadowPatches.ShadowDistance), null,
                Loc.T("komet:hud-blocks-box-fade", "blocks · box {0} · fade {1}",
                    Patches.ShadowPatches.SymmetricBox ? Loc.T("komet:hud-box-sphere", "sphere") : "vanilla",
                    Patches.ShadowPatches.FadeFix ? Loc.T("komet:hud-fade-fix", "fix") : "vanilla"));
            // texels per block is the number that decides whether thin geometry (foliage!)
            // still casts a shadow - map edge over the box's world size
            DebugHud.Row(sb, Loc.Hud("shadow map"), Patches.ShadowResPatches.EffectiveMapSize + "px", null,
                ShadowTexelsPerBlock().ToString("F1", CultureInfo.CurrentCulture) + Loc.T("komet:hud-texels-per-block", " texels per block"));
        }

        // -- uploads and the loading pipeline --
        DebugHud.Row(sb, Loc.Hud("upload gain"), UploadBudget.Gain.ToString("P0", CultureInfo.CurrentCulture), null,
            Loc.T("komet:hud-of-vanilla-budget", "of the vanilla budget")
            + (UploadBudget.StatPressureCuts > 0
                ? Loc.T("komet:hud-frame-pressure", " · {0}x frame pressure", UploadBudget.StatPressureCuts)
                : ""));
        // Shown while the budget is armed even at 0 activity: "0 chunks" is correct idleness,
        // a missing row would be indistinguishable from a prefix that never ran (the edge-prio
        // lesson - idle and broken must not look the same).
        if (Patches.PrioUploadPatches.Enabled || Patches.PrioUploadPatches.StatUploadedChunks > 0)
            DebugHud.Row(sb, Loc.Hud("prio upload"), DebugHud.N(Patches.PrioUploadPatches.StatUploadedChunks), null,
                Loc.T("komet:hud-chunks-spread", "chunks, {0}x spread", DebugHud.N(Patches.PrioUploadPatches.StatDeferrals))
                + (Patches.PrioUploadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (InflowBrake.Enabled)
            DebugHud.Row(sb, Loc.Hud("inflow"), InflowBrake.CurrentPercent + " %", null,
                InflowBrake.CurrentPercent < 100
                    ? Loc.T("komet:hud-columns-per-tick", "{0} columns / {1} ms", InflowBrake.CurrentColumns, InflowBrake.CurrentTickMs)
                    : Loc.T("komet:hud-full", "full"));
        if (Patches.TesselationPatches.StatPrefetchedUnpacks > 0)
            DebugHud.Row(sb, Loc.Hud("prefetch"), DebugHud.N(Patches.TesselationPatches.StatPrefetchedUnpacks), null,
                Loc.T("komet:hud-chunks-preunpacked", "chunks pre-unpacked"));
        var pipeTotal = WindowPrebuilder.StatHits + WindowPrebuilder.StatMisses;
        if (pipeTotal > 0)
        {
            var tail = DebugHud.N(WindowPrebuilder.StatHits) + "/" + DebugHud.N(pipeTotal) + Loc.T("komet:hud-windows", " windows");
            if (WindowPrebuilder.StatStale > 0) tail += ", " + DebugHud.N(WindowPrebuilder.StatStale) + " stale";
            if (WindowPrebuilder.ValidateRemaining > 0)
                tail += Loc.T("komet:hud-validated", " (validated {0})", DebugHud.N(WindowPrebuilder.StatValidated));
            DebugHud.Row(sb, Loc.Hud("window pipe"),
                (100.0 * WindowPrebuilder.StatHits / pipeTotal).ToString("F0", CultureInfo.CurrentCulture) + " %",
                null, tail);
        }
        // Always shown while the feature is on: "0 vorgezogen über N sweeps" is the line
        // that separates "correctly idle" from "prefix never ran" - the first field report
        // could not tell the two apart, which is this project's oldest trap.
        if (Patches.EdgeRetessPriorityPatches.Enabled || Patches.EdgeRetessPriorityPatches.StatPromoted > 0)
            DebugHud.Row(sb, Loc.Hud("edge prio"), DebugHud.N(Patches.EdgeRetessPriorityPatches.StatPromoted), null,
                Loc.T("komet:hud-edge-repairs", "edge repairs promoted, {0} sweeps", DebugHud.N(Patches.EdgeRetessPriorityPatches.StatSweeps))
                + (Patches.EdgeRetessPriorityPatches.StatBusySkips > 0
                    ? ", " + DebugHud.N(Patches.EdgeRetessPriorityPatches.StatBusySkips) + Loc.T("komet:hud-prio-full", "x prio full")
                    : "")
                + (Patches.EdgeRetessPriorityPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        // Shown while the gate is armed even at 0 - idle must not look like broken.
        if (Patches.AnimatableCullPatches.Enabled || Patches.AnimatableCullPatches.StatCalls > 0)
            DebugHud.Row(sb, Loc.Hud("animatable gate"), DebugHud.N(Patches.AnimatableCullPatches.StatSkipped), null,
                Loc.T("komet:hud-of-calls-skipped", "of {0} calls skipped", DebugHud.N(Patches.AnimatableCullPatches.StatCalls))
                + (Patches.AnimatableCullPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (Patches.EdgeCoalescePatches.StatAbsorbed + Patches.EdgeCoalescePatches.StatFlushed > 0)
            DebugHud.Row(sb, Loc.Hud("edge coalesce"), DebugHud.N(Patches.EdgeCoalescePatches.StatAbsorbed), null,
                Loc.T("komet:hud-saved-flushed-open", "saved, {0} flushed, {1} open",
                    DebugHud.N(Patches.EdgeCoalescePatches.StatFlushed), DebugHud.N(Patches.EdgeCoalescePatches.PendingCount))
                + (Patches.EdgeCoalescePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (Patches.RetessSourcePatches.MarksPerSecond > 0.5)
            DebugHud.Row(sb, Loc.Hud("dirty marks"),
                Patches.RetessSourcePatches.MarksPerSecond.ToString("F0", CultureInfo.CurrentCulture) + "/s",
                null,
                Patches.RetessSourcePatches.EdgeMarksPerSecond.ToString("F0", CultureInfo.CurrentCulture)
                + Loc.T("komet:hud-edge-marks", "/s edge, '.komet retess'"));

        // -- the rest --
        if (Patches.EntityTessPatches.StatAllowed + Patches.EntityTessPatches.StatDeferred > 0)
            DebugHud.Row(sb, Loc.Hud("entity tess"), DebugHud.N(Patches.EntityTessPatches.StatAllowed), null,
                Loc.T("komet:hud-deferred", "{0} deferred", DebugHud.N(Patches.EntityTessPatches.StatDeferred))
                // the budget's liveness gap made visible: the first call per frame is
                // uncapped, so ONE fat entity can still spike a frame - this names it
                + (Patches.EntityTessPatches.StatWorstMs >= 5
                    ? Loc.T("komet:hud-slowest", " · slowest {0} ms", Patches.EntityTessPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)) + (Patches.EntityTessPatches.StatWorstName != null
                          ? " (" + Patches.EntityTessPatches.StatWorstName + ")" : "")
                    : "")
                // a window that had to reopen itself means the frame boundary is not firing -
                // without this line that state is invisible and reads as "animals missing"
                + (Patches.EntityTessPatches.StatStaleResets > 0
                    ? Loc.T("komet:hud-no-frame-boundary", " · {0}x without frame boundary", DebugHud.N(Patches.EntityTessPatches.StatStaleResets)) : "")
                + (Patches.EntityTessPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        // Shown while armed even at 0 - a join flood is the only time it has work, and
        // "0 geladen" then has to read as correct idleness, not as a prefix that never ran.
        if (Patches.EntityLoadPatches.Enabled || Patches.EntityLoadPatches.StatLoaded > 0)
            DebugHud.Row(sb, Loc.Hud("entity load"), DebugHud.N(Patches.EntityLoadPatches.StatLoaded), null,
                Loc.T("komet:hud-open-frames", "{0} open, {1} frames spread",
                    DebugHud.N(Patches.EntityLoadPatches.PendingCount), DebugHud.N(Patches.EntityLoadPatches.StatDeferredFrames))
                + (Patches.EntityLoadPatches.StatWorstMs >= 5
                    ? Loc.T("komet:hud-slowest", " · slowest {0} ms", Patches.EntityLoadPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)) + (Patches.EntityLoadPatches.StatWorstCode != null
                          ? " (" + Patches.EntityLoadPatches.StatWorstCode + ")" : "")
                    : "")
                + (Patches.EntityLoadPatches.StatStaleFlushes > 0
                    ? Loc.T("komet:hud-no-frame-boundary", " · {0}x without frame boundary", DebugHud.N(Patches.EntityLoadPatches.StatStaleFlushes)) : "")
                + (Patches.EntityLoadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (Patches.MinimapPatches.Enabled || Patches.MinimapPatches.StatTicks > 0)
            DebugHud.Row(sb, Loc.Hud("minimap"), Patches.MinimapPatches.Cap + "/tick", DebugHud.Ms(Patches.MinimapPatches.AvgTickMs),
                Loc.T("komet:hud-upload-ticks", "{0} upload ticks", DebugHud.N(Patches.MinimapPatches.StatTicks))
                + (Patches.MinimapPatches.DirectUpload
                    ? Loc.T("komet:hud-direct-tiles", ", direct {0} tiles", DebugHud.N(Patches.MinimapPatches.StatDirectPieces))
                    : Loc.T("komet:hud-fbo-path", ", FBO path"))
                + (Patches.MinimapPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (Patches.MainThreadTaskPatches.Enabled)
        {
            var topTasks = Patches.MainThreadTaskPatches.Top(1);
            DebugHud.Row(sb, Loc.Hud("mt tasks"), null, DebugHud.Ms(FrameStats.AvgMainTaskMs),
                (topTasks.Count > 0
                    ? Loc.T("komet:hud-mostly", "mostly {0} {1} ms", topTasks[0].code, topTasks[0].ms.ToString("F2", CultureInfo.CurrentCulture))
                    : Loc.T("komet:hud-no-tasks", "no tasks"))
                + (Patches.MainThreadTaskPatches.StatBudgetCuts > 0
                    ? Loc.T("komet:hud-frames-capped", " · {0} frames capped", DebugHud.N(Patches.MainThreadTaskPatches.StatBudgetCuts)) : ""));
        }
        // Shown while armed even at 0 animated - an empty scene has nothing to animate,
        // and that has to read as idleness, not as a loop that never ran.
        if (Patches.EntityAnimPatches.Enabled || Patches.EntityAnimPatches.StatAnimated > 0)
            DebugHud.Row(sb, Loc.Hud("entity anim"), DebugHud.N((long)Math.Round(Patches.EntityAnimPatches.AvgAnimated)) + "/frame",
                DebugHud.Ms(Patches.EntityAnimPatches.AvgAnimMs),
                Loc.T("komet:hud-pre-render", "pre-render {0} ms, {1} anim frames saved",
                    Patches.EntityAnimPatches.AvgBeforeMs.ToString("F2", CultureInfo.CurrentCulture),
                    DebugHud.N(Patches.EntityAnimPatches.StatSkipped))
                + (Patches.EntityAnimPatches.LodEnabled ? "" : Loc.T("komet:hud-lod-off", " (lod OFF)"))
                + (Patches.EntityAnimPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (Patches.TickProfiler.Enabled && Patches.TickProfiler.StatWrapped > 0)
        {
            var topTick = Patches.TickProfiler.Top(1);
            DebugHud.Row(sb, Loc.Hud("tick listener"), DebugHud.N(Patches.TickProfiler.StatWrapped), DebugHud.Ms(Patches.TickProfiler.TotalMs),
                topTick.Count > 0
                    ? Loc.T("komet:hud-mostly", "mostly {0} {1} ms", topTick[0].name, topTick[0].ms.ToString("F2", CultureInfo.CurrentCulture))
                    : "");
        }
        if (Patches.FirepitPatches.StatSkipped > 0 || Patches.FirepitPatches.StatFastPath > 0
            || Patches.FirepitPatches.StatNearVanilla > 0)
            DebugHud.Row(sb, Loc.Hud("firepit gate"), DebugHud.N(Patches.FirepitPatches.StatSkipped), null,
                Loc.T("komet:hud-skipped-cache-vanilla", "skipped, {0} cache, {1} vanilla",
                    DebugHud.N(Patches.FirepitPatches.StatFastPath), DebugHud.N(Patches.FirepitPatches.StatNearVanilla))
                + (Patches.FirepitPatches.FastPathBroken ? Loc.T("komet:hud-cache-broken", " !! CACHE BROKEN (log)") : ""));
        if (PoolReclaimer.StatPoolsReclaimed > 0)
            DebugHud.Row(sb, Loc.Hud("vram freed"), DebugHud.N(PoolReclaimer.StatBytesReclaimed / 1048576.0) + " MB", null,
                Loc.T("komet:hud-pools-returned", "{0} pools returned", DebugHud.N(PoolReclaimer.StatPoolsReclaimed)));
    }

    /// <summary>
    /// The !!-rows alone. Shared between the full view's komet section and the compact view:
    /// a safemode session, a running stress test or an armed diagnostic changes what every
    /// other number means, so no view may hide them.
    /// </summary>
    private void WriteKometWarnings(StringBuilder sb, double frameMs)
    {
        if (safeMode) DebugHud.Row(sb, "!! SAFEMODE", Loc.T("komet:hud-on", "ON"), null, Loc.T("komet:hud-safemode-tail", "everything vanilla, '.komet safemode'"));
        if (StressTest.StatusLine != null) DebugHud.Row(sb, "!! STRESSTEST", null, null, StressTest.StatusLine);
        var diag = ActiveDiagnostics();
        if (diag != null) DebugHud.Row(sb, "!! " + Loc.Hud("DIAGNOSTICS"), null, null, diag);
    }

    /// <summary>Which of the two bit-identical sweep kernels is running, and on how many threads.</summary>
    private static string CullKernel()
    {
        var kernel = !FastCuller.VectorAvailable ? "scalar (no AVX CPU)"
                      : FastCuller.VectorCulling ? "avx2 (4 parts per instruction)"
                      : "scalar (vector kernel off)";
        var helpers = FastCuller.Workers.ThreadCount;
        return helpers == 0
            ? kernel + ", 1 thread"
            : kernel + ", " + (helpers + 1) + " threads (its own, not the thread pool)";
    }

    /// <summary>The same, but sized for a HUD tail - the long form was the widest line of the
    /// whole overlay and stretched every other row's whitespace with it.</summary>
    private static string CullKernelShort()
    {
        var kernel = FastCuller.VectorAvailable && FastCuller.VectorCulling ? "avx2" : "scalar";
        var threads = FastCuller.Workers.ThreadCount + 1;
        return kernel + " · " + (threads == 1 ? "1 thread" : threads + " threads");
    }

    /// <summary>
    /// Everything the mod is measuring rather than optimising, listed so it cannot hide.
    ///
    /// This line exists because of a specific failure: the whole mod was repeatedly reported as
    /// slower than its own safemode, and every stress phase for the drawing systems came back
    /// as noise - because safemode switches off what komet DRAWS and none of these, which are
    /// what komet MEASURES. Instrumentation that is invisible in the report is instrumentation
    /// that gets blamed on something else.
    /// </summary>
    private string ActiveDiagnostics()
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if (Patches.RendererProfiler.Enabled)
            parts.Add("renderer profiler (" + Patches.RendererProfiler.StatWrapped + " wrapped, 'toggle profiler')");
        if (Patches.RetessSourcePatches.SampleSources) parts.Add("retess sources ('toggle retess')");
        if (CullVerifier.SampleEvery > 0) parts.Add("sweep cross-check ('toggle cullcheck')");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// Shadow map texels per world block: map edge over the box's longest light-space side.
    /// This is the number that decides whether a leaf still casts a shadow, and the only honest
    /// way to weigh a box change against a resolution change.
    ///
    /// Measured off the box the engine actually built, not derived from ShadowDistance. The old
    /// estimate assumed vanilla's box was 0.78 x ShadowDistance across, which is its far plane's
    /// width in VIEW space - after the light transform the frustum's full depth folds into the
    /// same axes, so vanilla's real footprint is far larger than that and the symmetric box's
    /// cost correspondingly far smaller than this file used to claim. Falls back to the estimate
    /// only before the first shadow pass has run.
    /// </summary>
    private static double ShadowTexelsPerBlock()
    {
        // ShadowBoxSpan is captured right after the far cascade renders, so it is the real
        // number within a frame of world join; the estimate below only covers those frames.
        var span = Patches.ShadowPatches.ShadowBoxSpan;
        if (span <= 0)
        {
            var distance = Patches.ShadowPatches.ShadowDistance;
            if (distance <= 0) return 0;
            span = Patches.ShadowPatches.SymmetricBox
                ? 2.0 * Patches.ShadowPatches.BoxRadiusFactor * distance
                : 0.78 * distance;
        }
        return Patches.ShadowResPatches.EffectiveMapSize / span;
    }

    /// <summary>
    /// Milliseconds a frame spent rebuilding pool caches, i.e. the share of "sichtbarkeit"
    /// that is not the sweep at all. The counter is ticks since start, so the per-frame value
    /// comes from the same smoothed delta the other counters use.
    /// </summary>
    private double RebuildMsPerFrame()
        => (rebuildTicksPerFrame?.PerFrame ?? 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Chat command output never reaches any log file, which makes numbers reported in chat
    /// unrecoverable after the session. Mirroring them into client-main.log means a .komet
    /// reading can be looked up later instead of retyped from a screenshot.
    /// </summary>
    private string LoggedStats()
    {
        var stats = BuildStats();
        Mod.Logger.Notification("stats requested:\n{0}", stats);
        return stats;
    }

    /// <summary>
    /// The .komet text: everything the HUD shows, in a form that survives as one log line.
    /// Built line by line - its previous life as one 56-placeholder format string made every
    /// added figure a game of index bingo.
    /// </summary>
    private string BuildStats()
    {
        if (!config.FastFrustumCulling) return "komet: fast frustum culling is disabled in komet.json";
        if (FastCuller.StatSweeps == 0) return "komet: no cull sweeps recorded yet - join a world first";
        if (!FrameStats.HasData) return "komet: still collecting - the counters need a few hundred rendered frames";

        var ci = CultureInfo.CurrentCulture;
        var frame = FrameStats.AvgFrameMs;
        var fps = frame > 0 ? 1000.0 / frame : 0;
        double Pct(double ms) => frame > 0 ? 100.0 * ms / frame : 0;

        var sb = new StringBuilder(1024);
        sb.AppendFormat(ci, "komet {0} - averages over 240 frames (F7 = HUD)\n",
            KometVersion.Display(Mod.Info.Version));

        sb.AppendFormat(ci, "frame {0:F2} ms = {1:F0} fps, worst {2:F1} ms",
            frame, fps, FrameStats.MaxFrameMs);
        var worst = DebugHud.WorstFrameTail();
        if (worst != null) sb.Append(" (of which ").Append(worst).Append(')');
        // The gpu figure carries its sample count: a frozen value and a live one look the
        // same, and a frozen one (the ring stall of 02.09.) was read as "GPU-bound" twice.
        // The span is not the load: GL_TIME_ELAPSED counts the GPU's idle gaps too, so the
        // driver's busy figure (sysfs, amdgpu) prints next to it where it exists.
        sb.AppendFormat(ci, " | game tick {0:F2} ms | gpu {1:F2} ms ({7} samples{8}) | gc {2:F1} ms/s pauses, "
            + "{3:F0} MB/s alloc, gen0 {4:F0}/s, gen2 {5:F1}/s, mode {6}\n",
            FrameStats.GameTickMs, GpuFrameTimer.GpuMs, FrameStats.GcPauseMsPerSecond,
            FrameStats.AllocMbPerSecond, FrameStats.Gen0PerSecond, FrameStats.Gen2PerSecond,
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation", GpuFrameTimer.StatSamples,
            GpuBusy.Available ? string.Format(ci, ", {0} % busy according to {1}", GpuBusy.Percent, GpuBusy.Source) : "");
        // Where the GPU's milliseconds go, stage by stage: the figure that decides whether the
        // shadow map step, the far cascade's LOD or a post-processing mod is the lever.
        if (GpuFrameTimer.StageSamples > 0)
            sb.AppendFormat(ci, "  gpu per stage: before {0:F1} | shadow {1:F1} (far {2:F1}, near {3:F1}) | opaque {4:F1} | "
                + "oit {5:F1} | post {6:F1} | ortho {7:F1} | done {8:F1} ms ({9} samples, GPU span per stage)\n",
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Before],
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone, EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone),
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone),
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone),
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Opaque],
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.OIT],
                GpuFrameTimer.StageSum(EnumRenderStage.AfterOIT, EnumRenderStage.AfterPostProcessing, EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit),
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Ortho],
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Done],
                GpuFrameTimer.StageSamples);
        // The per-thread allocation split lived only in the F7 overlay until 01.09. - the
        // one field report that was supposed to decide the network-decompression question
        // arrived without it, because reports come from '.komet report', not screenshots.
        // Whatever nobody measures stays visible as "rest" instead of vanishing.
        // "rest" is what no measured thread accounts for. Until 02.09. that was almost
        // entirely the integrated server (worldgen, serialization, compression) sharing this
        // process - 193 of 279 MB/s in the join-flood reports, behind 35 gen0 collections a
        // second. The server's own attribution (per thread, per suspect) now prints on the
        // next line, and its thread-level sum leaves the rest column.
        // Survivors, not just allocation: the pause is paid for what the collector keeps.
        // "befoerdert" is gen0 and gen1 promotion together, so an object promoted twice counts
        // twice - read the share as an upper bound. Close to the allocation rate = streamed
        // world data that has to live, and only loading less per second helps; far below it
        // = garbage, and the alloc-quellen lines say whose.
        if (FrameStats.GcInfosSeen > 0)
            sb.AppendFormat(ci, "  gc details: gen1 {0:F1}/s, promoted {1:F0} MB/s = {2:F1} MB per collection "
                + "(up to {3:F0} % of the allocation survives), last {4} {5:F1} ms pause, heap {6:F0} MB\n",
                FrameStats.Gen1PerSecond, FrameStats.PromotedMbPerSecond, FrameStats.PromotedMbPerGc,
                FrameStats.AllocMbPerSecond > 0 ? Math.Min(100.0, 100.0 * FrameStats.PromotedMbPerSecond / FrameStats.AllocMbPerSecond) : 0,
                FrameStats.LastGcGeneration >= 0 ? "gen" + FrameStats.LastGcGeneration : "-",
                FrameStats.LastGcPauseMs, FrameStats.GcHeapMb);
        var serverMb = Patches.ServerAllocPatches.ThreadMbPerSecond;
        var clientMb = Patches.ClientAllocPatches.ThreadMbPerSecond;
        var poolMb = Patches.ClientAllocPatches.PoolMbPerSecond;
        var clientOn = Patches.ClientAllocPatches.Enabled && Patches.ClientAllocPatches.Entries.Count > 0;
        if (FrameStats.AllocMbPerSecond >= 8)
        {
            // With the client worker threads measured at thread level, "tess" and "netz" are
            // contained in client-threads (the meshing bracket still prints in the laden: line);
            // without it the older split stands.
            if (clientOn)
                sb.AppendFormat(ci, "  alloc sources: main {0:F0}, client threads {1:F0}, thread pool {2:F0}, "
                    + "prefetch {3:F0}, server {4:F0}, rest {5:F0} MB/s (rest = unmeasured)\n",
                    FrameStats.MainAllocMbPerSecond, clientMb, poolMb,
                    FrameStats.PrefetchAllocMbPerSecond, serverMb,
                    Math.Max(0.0, FrameStats.AllocMbPerSecond - FrameStats.MainAllocMbPerSecond
                        - clientMb - poolMb - FrameStats.PrefetchAllocMbPerSecond - serverMb));
            else
                sb.AppendFormat(ci, "  alloc sources: net {0:F0}, main {1:F0}, prefetch {2:F0}, "
                    + "tess {3:F0}, server {4:F0}, rest {5:F0} MB/s (rest = unmeasured)\n",
                    FrameStats.NetAllocMbPerSecond, FrameStats.MainAllocMbPerSecond,
                    FrameStats.PrefetchAllocMbPerSecond, TesselationStats.AllocMbPerSecond, serverMb,
                    Math.Max(0.0, FrameStats.AllocMbPerSecond - FrameStats.NetAllocMbPerSecond
                        - FrameStats.MainAllocMbPerSecond - FrameStats.PrefetchAllocMbPerSecond
                        - TesselationStats.AllocMbPerSecond - serverMb));
        }
        if (clientOn && clientMb + poolMb >= 1) Patches.ClientAllocPatches.Write(sb, ci);
        // The sample-based view over EVERY thread, brackets or not: the line that names what
        // "rest" is. Printed whenever it has data; its absence with a reason is data too.
        if (AllocSampler.Enabled && AllocSampler.Samples > 0) AllocSampler.Write(sb, ci);
        else if (AllocSampler.Failure != null)
            sb.Append("  alloc sampling: not available (").Append(AllocSampler.Failure).Append(")\n");
        if (serverMb >= 1 || (capi != null && capi.IsSinglePlayer && Patches.ServerAllocPatches.Entries.Count > 0))
            Patches.ServerAllocPatches.Write(sb, ci);
        // Single-block packets and who on the server sends them (03.09.: 7.000 ExchangeBlock
        // a second while streaming, a third of all dirty marks).
        if (capi != null && capi.IsSinglePlayer && Patches.PacketSourcePatches.StatExchange + Patches.PacketSourcePatches.StatSet > 0)
            Patches.PacketSourcePatches.Write(sb, ci);
        sb.AppendFormat(ci, "hitches: {0} ('.komet hitch' for details)\n", HitchLog.SummaryLine());
        sb.AppendFormat(ci, "cpu: {0:F1} of {1} cores busy ({2:F0} %)\n",
            FrameStats.CpuCoresBusy, Environment.ProcessorCount,
            100.0 * FrameStats.CpuCoresBusy / Environment.ProcessorCount);
        // The two attributions for the buckets that used to have no owner: "draussen" (the
        // main-thread task drain) and "tick" (its listeners). Printed whenever armed.
        if (Patches.MainThreadTaskPatches.Enabled) Patches.MainThreadTaskPatches.Write(sb, 5, ci);
        if (Patches.TickProfiler.Enabled) Patches.TickProfiler.Write(sb, 6, ci, FrameStats.GameTickMs);
        if (Patches.EntityAnimPatches.Enabled || Patches.EntityAnimPatches.StatAnimated > 0) Patches.EntityAnimPatches.Write(sb, ci);

        var shadows = FrameStats.ShadowMs;
        sb.AppendFormat(ci, "stages: opaque {0:F2} | shadow {1:F2} ({2:F0}%) | oit {3:F2} | "
            + "ortho {4:F2} | done {5:F2}\n",
            FrameStats.StageMs[(int)EnumRenderStage.Opaque], shadows, Pct(shadows),
            FrameStats.StageMs[(int)EnumRenderStage.OIT],
            FrameStats.StageMs[(int)EnumRenderStage.Ortho],
            FrameStats.StageMs[(int)EnumRenderStage.Done]);
        sb.AppendFormat(ci, "  post/compose {0:F2} | outside the stages {1:F2} (of which swap {2:F2})\n",
            FrameStats.PostComposeMs, FrameStats.OutsideStagesMs, FrameStats.AvgSwapMs);

        sb.AppendFormat(ci, "visibility {0:F2} ms ({1:F0}%), {2:N0} parts tested, "
            + "{3:N0} pools skipped entirely\n",
            FrameStats.AvgCullMs, Pct(FrameStats.AvgCullMs),
            partsPerFrame?.PerFrame ?? 0, FastCuller.StatPoolsSkipped);
        sb.AppendFormat(ci, "  of which {0:F2} ms cache rebuild ({1:N0}/frame), {2:N0} sweeps/frame "
            + "over {3:N0} pools, kernel {4}\n",
            RebuildMsPerFrame(), rebuildsPerFrame?.PerFrame ?? 0,
            sweepsPerFrame?.PerFrame ?? 0, hud?.PoolCount ?? 0, CullKernel());
        sb.AppendFormat(ci, "  incremental: {0:N0} inserts, {1:N0} removals without a rebuild\n",
            FastCuller.StatIncInserts, FastCuller.StatIncRemovals);

        // The share of the sweep that was waiting rather than culling. Near zero is the healthy
        // state and the reason this line exists: it used to be most of the sweep, invisibly,
        // because the batch ran on the ThreadPool behind the game's own chunk tesselation.
        // The pool shape, so the grid's cell target can be set from the real thing. The
        // benchmark's optimum moves with parts per pool and the value in use was tuned against
        // a modelled shape that was 5.8x off in pool count.
        if (FastCuller.StatPoolsLive > 0)
            sb.AppendFormat(ci, "  pool shape: {0:N0} parts in {1:N0} pools = {2:N0} per pool, "
                + "cell target {3}\n",
                FastCuller.StatPartsHeld, FastCuller.StatPoolsLive,
                FastCuller.StatPartsHeld / (double)FastCuller.StatPoolsLive,
                FastCuller.PartsPerCellTarget);

        var batches = FastCuller.Workers.StatRuns;
        if (batches > 0)
            sb.AppendFormat(ci, "  cull threads: {0:F3} ms wait per batch over {1:N0} batches"
                + "{3}, occlusion auf {2} threads\n",
                FastCuller.Workers.StatWaitTicks * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency / batches,
                batches,
                // Stated, not assumed: Thread.Priority is accepted and silently ignored for
                // ordinary threads on Linux, so "deprioritised" has to be something the OS
                // confirmed rather than something we asked for.
                (FastChunkCuller.Workers.ThreadCount + 1)
                    + (FastChunkCuller.Workers.PriorityLowered ? " (deprioritised)" : ""),
                // batches that ran inline because a helper had not woken up yet - the number
                // that says how often the machine was too loaded for the parallel path
                FastCuller.Workers.StatContendedInline > 0
                    ? " (" + FastCuller.Workers.StatContendedInline.ToString("N0", ci) + "x inline because of contention)"
                    : "");

        var diag = ActiveDiagnostics();
        if (diag != null)
            sb.AppendFormat(ci, "  DIAGNOSTICS ARE RUNNING: {0} - costs frame time, safemode does not turn this off\n", diag);
        if (CullVerifier.SampleEvery > 0 || CullVerifier.StatMismatches > 0)
            sb.AppendFormat(ci, "  sweep check: {0:N0} sweeps checked against vanilla, {1:N0} mismatches\n",
                CullVerifier.StatChecked, CullVerifier.StatMismatches);

        var raw = rawRangesPerFrame?.PerFrame ?? 0;
        var emitted = rangesPerFrame?.PerFrame ?? 0;
        var bridged = bridgedPerFrame?.PerFrame ?? 0;
        sb.AppendFormat(ci, "draw ranges {0:N0} of {1:N0} ({2:F1}x), draw calls {3:N0}/frame, "
            + "{4:N0} triangles\n",
            emitted, raw, emitted > 0 ? raw / emitted : 1.0,
            hud?.DrawCallsPerFrame ?? 0, hud?.RenderedTriangles ?? 0);
        if (bridged > 0)
            sb.AppendFormat(ci, "  gap merge: {0:N0} ranges/frame saved by bridging "
                + "frustum clips\n", bridged);

        // The shadow line only lived in the F7 overlay until 1.40.0, which meant every log the
        // shadow work was judged from arrived without a single shadow figure in it.
        if (Patches.ShadowPatches.ShadowDistance > 0)
            sb.AppendFormat(ci, "shadows: to {0:F0} blocks, box {1} ({2:F0} blocks wide), fade {3}, "
                + "distance x{4:0.##} | map {5}px = {6:F1} texels per block, lod3 {7}\n",
                Patches.ShadowPatches.ShadowDistance,
                Patches.ShadowPatches.SymmetricBox ? "sphere" : "vanilla wedge",
                Patches.ShadowPatches.ShadowBoxSpan,
                Patches.ShadowPatches.FadeFix ? "fix" : "vanilla",
                Patches.ShadowPatches.DistanceMultiplier,
                Patches.ShadowResPatches.EffectiveMapSize, ShadowTexelsPerBlock(),
                FastCuller.ShadowSkipRedundantLod ? "out" : "in");

        sb.AppendFormat(ci, "upload {0:F2} ms (max {1:F1}), throttle {2:P0}{3}, prio budget {4} "
            + "({5:N0} chunks, {6:N0}x spread) | occlusion {7:F1} ms on the worker, {8:N0} chunks\n",
            FrameStats.AvgUploadMs, FrameStats.MaxUploadMs, UploadBudget.Gain,
            UploadBudget.StatPressureCuts > 0
                ? string.Format(ci, " ({0:N0}x throttled by frame pressure)", UploadBudget.StatPressureCuts)
                : "",
            Patches.PrioUploadPatches.Enabled ? "an" : "AUS",
            Patches.PrioUploadPatches.StatUploadedChunks, Patches.PrioUploadPatches.StatDeferrals,
            FastChunkCuller.StatLastMs, FastChunkCuller.StatChunksSnapshotted);
        if (FrameStats.StatPoolAllocs > 0)
            sb.AppendFormat(ci, "  mesh pools allocated: {0:N0} since reset, {1:F1} ms total, longest {2:F1} ms "
                + "(GL buffers in that frame's upload bucket)\n",
                FrameStats.StatPoolAllocs, FrameStats.StatPoolAllocMs, FrameStats.MaxPoolAllocMs);

        var pipeTotal = WindowPrebuilder.StatHits + WindowPrebuilder.StatMisses;
        sb.AppendFormat(ci, "loading: {0:F0} chunks/s received, {1:F0}/s tesselated at {2:F2} ms "
            + "({3:F1} neighbours, {4:F1} light, {5:F0}% edge, {6:F0} MB/s: neighbours {7:F0}, "
            + "light {8:F0}, clones {9:F0}, shapes {10:F0}, rest {11:F0}), queued {12:N0}, "
            + "prefetch {13:N0} unpacked\n",
            TesselationStats.ReceivedPerSecond, TesselationStats.ChunksPerSecond,
            TesselationStats.MsPerChunk, TesselationStats.NeighbourMsPerChunk,
            TesselationStats.RelightMsPerChunk, TesselationStats.EdgeSharePercent,
            TesselationStats.AllocMbPerSecond,
            TesselationStats.NeighbourAllocMbPerSecond,
            TesselationStats.RelightAllocMbPerSecond,
            TesselationStats.PartsAllocMbPerSecond,
            TesselationStats.JsonAllocMbPerSecond,
            Math.Max(0, TesselationStats.AllocMbPerSecond - TesselationStats.NeighbourAllocMbPerSecond
                        - TesselationStats.RelightAllocMbPerSecond - TesselationStats.PartsAllocMbPerSecond
                        - TesselationStats.JsonAllocMbPerSecond),
            Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation,
            Patches.TesselationPatches.StatPrefetchedUnpacks);
        if (pipeTotal > 0)
            sb.AppendFormat(ci, "  window pipeline: {0:N0} of {1:N0} hits ({2:F0}%), {3:N0} stale, "
                + "{4:N0} validated ({5} to go)\n",
                WindowPrebuilder.StatHits, pipeTotal,
                100.0 * WindowPrebuilder.StatHits / pipeTotal, WindowPrebuilder.StatStale,
                WindowPrebuilder.StatValidated, WindowPrebuilder.ValidateRemaining);

        // Hit rate of the size-class mesh buffer pool. "frisch alloziert" is what still went
        // to the GC despite the pool - the number the whole patch exists to shrink; if it
        // stays high with the pool on, the loading allocation lives somewhere else and this
        // row is the disproof.
        var recyclerAsked = Patches.MeshRecyclerPatches.StatHits + Patches.MeshRecyclerPatches.StatMisses;
        if (Patches.MeshRecyclerPatches.Enabled && recyclerAsked > 0)
            sb.AppendFormat(ci, "  mesh recycler: {0:F0}% hits ({1:N0} requests), {2:N0} MB held, "
                + "{3:N0} MB freshly allocated, {4:N0} evicted\n",
                100.0 * Patches.MeshRecyclerPatches.StatHits / recyclerAsked, recyclerAsked,
                Patches.MeshRecyclerPatches.HeldBytes / 1048576.0,
                Patches.MeshRecyclerPatches.StatMissBytes / 1048576.0,
                Patches.MeshRecyclerPatches.StatEvicted);

        if (Patches.TightClonePatches.Enabled && Patches.TightClonePatches.StatClones > 0)
            sb.AppendFormat(ci, "  tight clone: {0:N0} clones, {1:N0} MB of capacity-sized copies saved\n",
                Patches.TightClonePatches.StatClones,
                Patches.TightClonePatches.StatBytesSaved / 1048576.0);
        // Printed whenever the pool is armed: hits at 0 with misses climbing means the return
        // path is not firing (the AddToPools postfix), which must not look like "no data yet".
        if (Patches.TightClonePatches.Enabled || Patches.TightClonePatches.StatClones > 0)
        {
            var hits = Patches.TightClonePatches.StatClones;
            var misses = Patches.TightClonePatches.StatExtrasMisses;
            var total = hits + misses;
            sb.AppendFormat(ci, "  extras pool: {0:F0}% hits ({1:N0} requests), {2:N0} MB held, {3:N0} dropped{4}\n",
                total > 0 ? 100.0 * hits / total : 0, total,
                Patches.TightClonePatches.PooledBytes / 1048576.0,
                Patches.TightClonePatches.StatExtrasDropped,
                Patches.TightClonePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        }
        if (Patches.AnimatableCullPatches.Enabled || Patches.AnimatableCullPatches.StatCalls > 0)
            sb.AppendFormat(ci, "animatable gate: {0:N0} of {1:N0} calls skipped{2}\n",
                Patches.AnimatableCullPatches.StatSkipped, Patches.AnimatableCullPatches.StatCalls,
                Patches.AnimatableCullPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        if (Patches.MinimapPatches.Enabled || Patches.MinimapPatches.StatTicks > 0)
            sb.AppendFormat(ci, "minimap: cap {0} tiles/tick, {1:F2} ms per upload tick over {2:N0} ticks, {3}{4}\n",
                Patches.MinimapPatches.Cap, Patches.MinimapPatches.AvgTickMs, Patches.MinimapPatches.StatTicks,
                Patches.MinimapPatches.DirectUpload
                    ? string.Format(ci, "direct upload ({0:N0} tiles into {1:N0} components)",
                        Patches.MinimapPatches.StatDirectPieces, Patches.MinimapPatches.StatDirectComponents)
                    : "FBO path (vanilla)",
                Patches.MinimapPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        // Printed whenever armed: "0 geladen, 0 offen" is correct idleness in a settled
        // scene, and must not be confused with a prefix that never ran.
        if (Patches.EntityLoadPatches.Enabled || Patches.EntityLoadPatches.StatLoaded > 0)
            sb.AppendFormat(ci, "entity load: {0:N0} loaded ({1:N0} promoted, {2:N0} dropped, {3:N0} updates onto held ones), "
                + "{4:N0} open, {5:N0} frames spread, slowest {6:F1} ms{7}{8}\n",
                Patches.EntityLoadPatches.StatLoaded, Patches.EntityLoadPatches.StatPromoted,
                Patches.EntityLoadPatches.StatDropped, Patches.EntityLoadPatches.StatUpdatedPending,
                Patches.EntityLoadPatches.PendingCount, Patches.EntityLoadPatches.StatDeferredFrames,
                Patches.EntityLoadPatches.StatWorstMs,
                Patches.EntityLoadPatches.StatWorstCode != null ? " (" + Patches.EntityLoadPatches.StatWorstCode + ")" : "",
                Patches.EntityLoadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        // Server half - in singleplayer the statics are shared with the integrated server, so
        // the counters are live here; on a remote server they stay at zero and say so.
        var posTotal = Patches.EntitySyncPatches.StatPositionsSent + Patches.EntitySyncPatches.StatPositionsSkipped;
        var attrTotal = Patches.EntitySyncPatches.StatAttrPathsSent + Patches.EntitySyncPatches.StatAttrPathsSkipped;
        if (capi != null && capi.IsSinglePlayer || posTotal + attrTotal > 0)
            sb.AppendFormat(ci, "entity sync (server): positions {0:N0} sent, {1:N0} saved ({2:F0} %), "
                + "{3:N0} hysteresis holds, {4:N0}x cap-sorted | attributes {5:N0} paths sent, {6:N0} saved ({7:F0} %), "
                + "{8:N0} packets suppressed{9}{10}\n",
                Patches.EntitySyncPatches.StatPositionsSent, Patches.EntitySyncPatches.StatPositionsSkipped,
                posTotal > 0 ? 100.0 * Patches.EntitySyncPatches.StatPositionsSkipped / posTotal : 0,
                Patches.EntitySyncPatches.StatHysteresisHolds, Patches.EntitySyncPatches.StatCapOrderings,
                Patches.EntitySyncPatches.StatAttrPathsSent, Patches.EntitySyncPatches.StatAttrPathsSkipped,
                attrTotal > 0 ? 100.0 * Patches.EntitySyncPatches.StatAttrPathsSkipped / attrTotal : 0,
                Patches.EntitySyncPatches.StatAttrPacketsSuppressed,
                Patches.EntitySyncPatches.DistanceSendRate ? "" : " (sync tuning OFF)",
                Patches.EntitySyncPatches.AttributeNoOpSkip ? "" : " (attr skip OFF)");

        sb.AppendFormat(ci, "vram: {0:N0} MB returned from {1:N0} empty pools, {2:N0} still empty\n",
            PoolReclaimer.StatBytesReclaimed / 1048576.0, PoolReclaimer.StatPoolsReclaimed,
            PoolReclaimer.StatEmptyPools);

        sb.AppendFormat(ci, "inflow brake: {0} % ({1} columns per {2} ms, base {3} per {4} ms), "
            + "{5:F0}s throttled\n",
            InflowBrake.Enabled ? InflowBrake.CurrentPercent.ToString(ci) : "aus",
            InflowBrake.CurrentColumns, InflowBrake.CurrentTickMs,
            InflowBrake.BaseColumns, InflowBrake.BaseTickMs, InflowBrake.SecondsBraking);

        sb.Append("upload path: ").Append(UploadPathDescription());
        return sb.ToString();
    }

    /// <summary>Which GL path chunk uploads take, and whether the bulk-copy patch matters on it.</summary>
    private static string UploadPathDescription()
    {
        var bulkCalls = Patches.MeshUploadPatches.StatBulkCalls;
        if (bulkCalls > 0) return $"persistent mapping, {bulkCalls:N0} bulk copies";
        if (Patches.MeshUploadPatches.StatFallbackCalls > 0) return "glBufferSubData, the bulk copy patch has no effect";
        return "glBufferSubData; the driver "
            + (Patches.PersistentMappingPatch.Available ? "can do" : "cannot do")
            + " persistent mapping, flag " + (Patches.PersistentMappingPatch.Enabled ? "an" : "aus");
    }
}
