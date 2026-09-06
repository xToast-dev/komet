using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Komet.Culling;
using Komet.Guard;
using Komet.Measure;
using Komet.Patches;
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
        sb.AppendFormat(ci, "worker pool {0} of {1} awake ({2} nice), safemode {3}\n",
            JobScheduler.ActiveWorkers, JobScheduler.WorkerCount,
            JobScheduler.PriorityLowered ? "some" : "none",
            safeMode ? "ON - these numbers say nothing about the mod!" : "aus");
        if (MeasurementPatches.SkippedBrackets.Count > 0)
            sb.AppendFormat(ci, "measurement without: {0} (the engine build differs, those lines stay empty)\n",
                string.Join(", ", MeasurementPatches.SkippedBrackets));
        // Is anybody else on komet's methods, and is this the engine komet was verified
        // against - the two questions a field report from a modified client has to answer
        // before any of its numbers mean anything.
        sb.Append(PatchGuard.ReportLines());
        // Optimum or OptiTime next to komet: two sides replacing the same engine code, each
        // undoing the other - a report from such a client has to say so before its numbers.
        if (ForeignClient.Findings.Count > 0)
        {
            sb.Append("client: ").Append(ForeignClient.Describe()).Append(" - INCOMPATIBLE with komet (");
            for (var i = 0; i < ForeignClient.Findings.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ForeignClient.Findings[i].How);
            }
            sb.Append("); the figures below do not describe komet alone\n");
        }

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
        if (RetessSourcePatches.SampleSources)
            sb.Append("\n---- dirty marks ----\n")
              .Append(RetessSourcePatches.BuildReport()).Append('\n');

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
        var parts = new List<string>(8);
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
        // Nine blocks in the order they have always been printed in, so the overlay's text is
        // unchanged to the byte. They are separate methods because the window reads them too,
        // one per view, and a window with its own copies of these rows would be a second place
        // to add the next counter to - and the one nobody remembers is always the one being
        // asked about.
        WriteProfilerRows(sb, frameMs);

        DebugHud.Section(sb, "komet");
        WriteKometWarnings(sb, frameMs);

        WriteSweepRows(sb, frameMs);
        WriteShadowRows(sb);
        WriteChunkRows(sb);
        WriteEntityBudgetRows(sb);
        WriteMinimapTaskRows(sb);
        WriteEntityAnimRows(sb);
        WriteTickFirepitRows(sb);
        WriteVramRow(sb);
    }

    /// <summary>The most expensive renderers, and what every other mod together costs.</summary>
    internal void WriteProfilerRows(StringBuilder sb, double frameMs)
    {
        if (RendererProfiler.Enabled)
        {
            DebugHud.Section(sb, Loc.Hud("most expensive renderers"));
            RendererProfiler.Write(sb, 8);
            // Against the stage totals this says whether the list above explains the frame.
            // The firepit renderer was found exactly because it did not.
            DebugHud.Row(sb, "= " + Loc.Hud("all together"), null, DebugHud.Ms(RendererProfiler.TotalMs),
                Loc.T("komet:hud-names", "{0} names", RendererProfiler.Count));
            DebugHud.Row(sb, Loc.Hud("of which measured"),
                RendererProfiler.StatWrapped + "/" + RendererProfiler.StatTotal);
        }

        // One row, not a section: what every OTHER mod together costs of this frame, and where
        // the list behind it is. The mod HUD is the instrument; this is the pointer to it, so a
        // player reading the performance HUD never has to guess whether mods are in these
        // numbers - they are, and this says how much.
        if (ModProfiler.Enabled && ModProfiler.Indexed && ModProfiler.TotalMs > 0.01)
            DebugHud.Row(sb, Loc.Hud("mods"), DebugHud.Pct(ModProfiler.TotalMs, frameMs),
                DebugHud.Ms(ModProfiler.TotalMs),
                Loc.T("komet:hud-mods-cmd", "{0} loaded · Shift+F7", ModProfiler.ModCount));
    }

    /// <summary>The visibility sweep: its cost, its throughput, its cache and its draw ranges.</summary>
    internal void WriteSweepRows(StringBuilder sb, double frameMs)
    {
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
    }

    /// <summary>The shadow cascades: cadence, distance, map sizes and texel density.</summary>
    internal void WriteShadowRows(StringBuilder sb)
    {
        // -- shadows --
        var shadowFrames = ShadowThrottlePatches.FarRendered + ShadowThrottlePatches.FarSkipped;
        if (shadowFrames > 0)
            // "schatten-takt", not "schatten fern": the frame-aufteilung block above already
            // has a row named schatten fern that means milliseconds - one name, one meaning
            DebugHud.Row(sb, Loc.Hud("shadow cadence"),
                "1/" + ShadowThrottlePatches.FarInterval + "-1/" + ShadowThrottlePatches.FarMaxSkip, null,
                (100.0 * ShadowThrottlePatches.FarSkipped / shadowFrames).ToString("F0", CultureInfo.CurrentCulture)
                + Loc.T("komet:hud-far-cascades-saved", " % far cascades saved")
                // The movement limit is what decides that percentage while anybody is moving,
                // so it belongs next to it rather than in a config file.
                + Loc.T("komet:hud-redraw-after", " · redraw after {0} blocks",
                    ShadowThrottlePatches.MoveLimit.ToString("0.##", CultureInfo.CurrentCulture)));
        if (ShadowPatches.ShadowDistance > 0)
        {
            DebugHud.Row(sb, Loc.Hud("shadows to"), DebugHud.N(ShadowPatches.ShadowDistance), null,
                Loc.T("komet:hud-blocks-box-fade", "blocks · box {0} · fade {1}",
                    ShadowPatches.SymmetricBox ? Loc.T("komet:hud-box-sphere", "sphere") : "vanilla",
                    ShadowPatches.FadeFix ? Loc.T("komet:hud-fade-fix", "fix") : "vanilla"));
            // texels per block is the number that decides whether thin geometry (foliage!)
            // still casts a shadow - map edge over the box's world size
            DebugHud.Row(sb, Loc.Hud("shadow map"), ShadowResPatches.EffectiveMapSize + "px", null,
                ShadowTexelsPerBlock().ToString("F1", CultureInfo.CurrentCulture) + Loc.T("komet:hud-texels-per-block", " texels per block"));
            // the near cascade's own map since 05.09. - the row that shows the fill the near
            // pass is really spending, which used to be hidden behind one shared size
            if (ShadowPatches.NearBoxSpan > 0)
                DebugHud.Row(sb, Loc.Hud("near map"), ShadowResPatches.EffectiveNearMapSize + "px", null,
                    NearShadowTexelsPerBlock().ToString("F1", CultureInfo.CurrentCulture) + Loc.T("komet:hud-texels-per-block", " texels per block"));
        }
    }

    /// <summary>Everything on the way from a received chunk to a drawn one: upload budget,
    /// inflow, the tesselation window pipeline, edge repairs, dirty marks.</summary>
    internal void WriteChunkRows(StringBuilder sb)
    {
        // -- uploads and the loading pipeline --
        DebugHud.Row(sb, Loc.Hud("upload gain"), UploadBudget.Gain.ToString("P0", CultureInfo.CurrentCulture), null,
            Loc.T("komet:hud-of-vanilla-budget", "of the vanilla budget")
            + (UploadBudget.StatPressureCuts > 0
                ? Loc.T("komet:hud-frame-pressure", " · {0}x frame pressure", UploadBudget.StatPressureCuts)
                : ""));
        // Shown while the budget is armed even at 0 activity: "0 chunks" is correct idleness,
        // a missing row would be indistinguishable from a prefix that never ran (the edge-prio
        // lesson - idle and broken must not look the same).
        if (PrioUploadPatches.Enabled || PrioUploadPatches.StatUploadedChunks > 0)
            DebugHud.Row(sb, Loc.Hud("prio upload"), DebugHud.N(PrioUploadPatches.StatUploadedChunks), null,
                Loc.T("komet:hud-chunks-spread", "chunks, {0}x spread", DebugHud.N(PrioUploadPatches.StatDeferrals))
                + (PrioUploadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (InflowBrake.Enabled)
            DebugHud.Row(sb, Loc.Hud("inflow"), InflowBrake.CurrentPercent + " %", null,
                InflowBrake.CurrentPercent < 100
                    ? Loc.T("komet:hud-columns-per-tick", "{0} columns / {1} ms", InflowBrake.CurrentColumns, InflowBrake.CurrentTickMs)
                    : Loc.T("komet:hud-full", "full"));
        if (TesselationPatches.StatPrefetchedUnpacks > 0)
            DebugHud.Row(sb, Loc.Hud("prefetch"), DebugHud.N(TesselationPatches.StatPrefetchedUnpacks), null,
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
        if (EdgeRetessPriorityPatches.Enabled || EdgeRetessPriorityPatches.StatPromoted > 0)
            DebugHud.Row(sb, Loc.Hud("edge prio"), DebugHud.N(EdgeRetessPriorityPatches.StatPromoted), null,
                Loc.T("komet:hud-edge-repairs", "edge repairs promoted, {0} sweeps", DebugHud.N(EdgeRetessPriorityPatches.StatSweeps))
                + (EdgeRetessPriorityPatches.StatBusySkips > 0
                    ? ", " + DebugHud.N(EdgeRetessPriorityPatches.StatBusySkips) + Loc.T("komet:hud-prio-full", "x prio full")
                    : "")
                + (EdgeRetessPriorityPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        // Shown while the gate is armed even at 0 - idle must not look like broken.
        if (AnimatableCullPatches.Enabled || AnimatableCullPatches.StatCalls > 0)
            DebugHud.Row(sb, Loc.Hud("animatable gate"), DebugHud.N(AnimatableCullPatches.StatSkipped), null,
                Loc.T("komet:hud-of-calls-skipped", "of {0} calls skipped", DebugHud.N(AnimatableCullPatches.StatCalls))
                + (AnimatableCullPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (EdgeCoalescePatches.StatAbsorbed + EdgeCoalescePatches.StatFlushed > 0)
            DebugHud.Row(sb, Loc.Hud("edge coalesce"), DebugHud.N(EdgeCoalescePatches.StatAbsorbed), null,
                Loc.T("komet:hud-saved-flushed-open", "saved, {0} flushed, {1} open",
                    DebugHud.N(EdgeCoalescePatches.StatFlushed), DebugHud.N(EdgeCoalescePatches.PendingCount))
                + (EdgeCoalescePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (RetessSourcePatches.MarksPerSecond > 0.5)
            DebugHud.Row(sb, Loc.Hud("dirty marks"),
                RetessSourcePatches.MarksPerSecond.ToString("F0", CultureInfo.CurrentCulture) + "/s",
                null,
                RetessSourcePatches.EdgeMarksPerSecond.ToString("F0", CultureInfo.CurrentCulture)
                + Loc.T("komet:hud-edge-marks", "/s edge, '.komet retess'"));
    }

    /// <summary>The two entity budgets and the animation warm-up that rides on one of them.</summary>
    internal void WriteEntityBudgetRows(StringBuilder sb)
    {
        // -- the rest --
        if (EntityTessPatches.StatAllowed + EntityTessPatches.StatDeferred > 0)
            DebugHud.Row(sb, Loc.Hud("entity tess"), DebugHud.N(EntityTessPatches.StatAllowed), null,
                Loc.T("komet:hud-deferred", "{0} deferred", DebugHud.N(EntityTessPatches.StatDeferred))
                // the budget's liveness gap made visible: the first call per frame is
                // uncapped, so ONE fat entity can still spike a frame - this names it
                + (EntityTessPatches.StatWorstMs >= 5
                    ? Loc.T("komet:hud-slowest", " · slowest {0} ms", EntityTessPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)) + (EntityTessPatches.StatWorstName != null
                          ? " (" + EntityTessPatches.StatWorstName + ")" : "")
                    : "")
                // a window that had to reopen itself means the frame boundary is not firing -
                // without this line that state is invisible and reads as "animals missing"
                + (EntityTessPatches.StatStaleResets > 0
                    ? Loc.T("komet:hud-no-frame-boundary", " · {0}x without frame boundary", DebugHud.N(EntityTessPatches.StatStaleResets)) : "")
                + (EntityTessPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        // Shown while armed even at 0 - a join flood is the only time it has work, and
        // "0 geladen" then has to read as correct idleness, not as a prefix that never ran.
        if (EntityLoadPatches.Enabled || EntityLoadPatches.StatLoaded > 0)
            DebugHud.Row(sb, Loc.Hud("entity load"), DebugHud.N(EntityLoadPatches.StatLoaded), null,
                Loc.T("komet:hud-open-frames", "{0} open, {1} frames spread",
                    DebugHud.N(EntityLoadPatches.PendingCount), DebugHud.N(EntityLoadPatches.StatDeferredFrames))
                + (EntityLoadPatches.StatWorstMs >= 5
                    ? Loc.T("komet:hud-slowest", " · slowest {0} ms", EntityLoadPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)) + (EntityLoadPatches.StatWorstCode != null
                          ? " (" + EntityLoadPatches.StatWorstCode + ")" : "")
                    : "")
                + (EntityLoadPatches.StatStaleFlushes > 0
                    ? Loc.T("komet:hud-no-frame-boundary", " · {0}x without frame boundary", DebugHud.N(EntityLoadPatches.StatStaleFlushes)) : "")
                + (EntityLoadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        // the frames a new creature type's animations used to cost the main thread, now on a worker
        if (AnimationWarmup.Enabled || AnimationWarmup.StatShapes > 0)
            DebugHud.Row(sb, Loc.Hud("anim prewarm"), DebugHud.N(AnimationWarmup.StatShapes), null,
                Loc.T("komet:hud-shapes-anims-workers", "{0} shapes, {1} animations, {2} ms on workers",
                    DebugHud.N(AnimationWarmup.StatShapes), DebugHud.N(AnimationWarmup.StatAnimations),
                    AnimationWarmup.StatWorkerMs.ToString("F0", CultureInfo.CurrentCulture))
                + (AnimationWarmup.StatWorstMs >= 1
                    ? Loc.T("komet:hud-slowest", " · slowest {0} ms", AnimationWarmup.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture))
                      + (AnimationWarmup.StatWorstShape != null ? " (" + AnimationWarmup.StatWorstShape + ")" : "")
                    : "")
                // Malformed shape data the engine would only have met if that animation ever
                // played. Shown, because a warm-up quietly skipping half a creature must not
                // look like one that warmed all of it.
                + (AnimationWarmup.StatBroken > 0
                    ? Loc.T("komet:hud-anim-broken", " · {0} malformed (log)", DebugHud.N(AnimationWarmup.StatBroken))
                    : "")
                + (AnimationWarmup.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
    }

    /// <summary>The minimap's tile budget and the main thread's task drain.</summary>
    internal void WriteMinimapTaskRows(StringBuilder sb)
    {
        if (MinimapPatches.Enabled || MinimapPatches.StatTicks > 0)
            DebugHud.Row(sb, Loc.Hud("minimap"), MinimapPatches.Cap + "/tick", DebugHud.Ms(MinimapPatches.AvgTickMs),
                Loc.T("komet:hud-upload-ticks", "{0} upload ticks", DebugHud.N(MinimapPatches.StatTicks))
                + (MinimapPatches.DirectUpload
                    ? Loc.T("komet:hud-direct-tiles", ", direct {0} tiles", DebugHud.N(MinimapPatches.StatDirectPieces))
                    : Loc.T("komet:hud-fbo-path", ", FBO path"))
                + (MinimapPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
        if (MainThreadTaskPatches.Enabled)
        {
            var topTasks = MainThreadTaskPatches.Top(1);
            DebugHud.Row(sb, Loc.Hud("mt tasks"), null, DebugHud.Ms(FrameStats.AvgMainTaskMs),
                (topTasks.Count > 0
                    ? Loc.T("komet:hud-mostly", "mostly {0} {1} ms", topTasks[0].code, topTasks[0].ms.ToString("F2", CultureInfo.CurrentCulture))
                    : Loc.T("komet:hud-no-tasks", "no tasks"))
                + (MainThreadTaskPatches.StatBudgetCuts > 0
                    ? Loc.T("komet:hud-frames-capped", " · {0} frames capped", DebugHud.N(MainThreadTaskPatches.StatBudgetCuts)) : ""));
        }
        // Shown while armed even at 0 animated - an empty scene has nothing to animate,
        // and that has to read as idleness, not as a loop that never ran.
    }

    /// <summary>What the entity pre-render and animation loop costs, and what the lod saves.</summary>
    internal void WriteEntityAnimRows(StringBuilder sb)
    {
        if (EntityAnimPatches.Enabled || EntityAnimPatches.StatAnimated > 0)
            DebugHud.Row(sb, Loc.Hud("entity anim"), DebugHud.N((long)Math.Round(EntityAnimPatches.AvgAnimated)) + "/frame",
                DebugHud.Ms(EntityAnimPatches.AvgAnimMs),
                Loc.T("komet:hud-pre-render", "pre-render {0} ms, {1} anim frames saved",
                    EntityAnimPatches.AvgBeforeMs.ToString("F2", CultureInfo.CurrentCulture),
                    DebugHud.N(EntityAnimPatches.StatSkipped))
                + (EntityAnimPatches.LodEnabled ? "" : Loc.T("komet:hud-lod-off", " (lod OFF)"))
                + (EntityAnimPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)")));
    }

    /// <summary>The game tick's listeners and the firepit gate.</summary>
    internal void WriteTickFirepitRows(StringBuilder sb)
    {
        if (TickProfiler.Enabled && TickProfiler.StatWrapped > 0)
        {
            var topTick = TickProfiler.Top(1);
            DebugHud.Row(sb, Loc.Hud("tick listener"), DebugHud.N(TickProfiler.StatWrapped), DebugHud.Ms(TickProfiler.TotalMs),
                topTick.Count > 0
                    ? Loc.T("komet:hud-mostly", "mostly {0} {1} ms", topTick[0].name, topTick[0].ms.ToString("F2", CultureInfo.CurrentCulture))
                    : "");
        }
        if (FirepitPatches.StatSkipped > 0 || FirepitPatches.StatFastPath > 0
            || FirepitPatches.StatNearVanilla > 0)
            DebugHud.Row(sb, Loc.Hud("firepit gate"), DebugHud.N(FirepitPatches.StatSkipped), null,
                Loc.T("komet:hud-skipped-cache-vanilla", "skipped, {0} cache, {1} vanilla",
                    DebugHud.N(FirepitPatches.StatFastPath), DebugHud.N(FirepitPatches.StatNearVanilla))
                + (FirepitPatches.FastPathBroken ? Loc.T("komet:hud-cache-broken", " !! CACHE BROKEN (log)") : ""));
    }

    /// <summary>What the reclaimer has handed back to the driver.</summary>
    internal void WriteVramRow(StringBuilder sb)
    {
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
        var helpers = JobScheduler.ActiveWorkers;
        return helpers == 0
            ? kernel + ", 1 thread"
            : kernel + ", " + (helpers + 1) + " threads (komet's own pool, not the thread pool)";
    }

    /// <summary>The same, but sized for a HUD tail - the long form was the widest line of the
    /// whole overlay and stretched every other row's whitespace with it.</summary>
    private static string CullKernelShort()
    {
        var kernel = FastCuller.VectorAvailable && FastCuller.VectorCulling ? "avx2" : "scalar";
        var threads = JobScheduler.ActiveWorkers + 1;
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
        var parts = new List<string>(4);
        if (RendererProfiler.Enabled)
            parts.Add("renderer profiler (" + RendererProfiler.StatWrapped + " wrapped, 'toggle profiler')");
        if (RetessSourcePatches.SampleSources) parts.Add("retess sources ('toggle retess')");
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
        var span = ShadowPatches.ShadowBoxSpan;
        if (span <= 0)
        {
            var distance = ShadowPatches.ShadowDistance;
            if (distance <= 0) return 0;
            span = ShadowPatches.SymmetricBox
                ? 2.0 * ShadowPatches.BoxRadiusFactor * distance
                : 0.78 * distance;
        }
        return ShadowResPatches.EffectiveMapSize / span;
    }

    /// <summary>Same for the near cascade: its own map over its own box.</summary>
    private static double NearShadowTexelsPerBlock()
    {
        var span = ShadowPatches.NearBoxSpan;
        return span <= 0 ? 0 : ShadowResPatches.EffectiveNearMapSize / span;
    }

    /// <summary>The sun's elevation, degrees above the horizon - the number that sets how long
    /// a strip of ground the near shadow box cuts out of the world (its light-space height over
    /// the sine of this).</summary>
    private double SunElevationDegrees()
    {
        var sun = capi?.World?.Calendar?.SunPositionNormalized;
        if (sun == null) return 0;
        var y = Math.Clamp((double)sun.Y, -1.0, 1.0);
        return Math.Asin(y) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Where the camera pass's triangles come from: by render pass and distance band, and by
    /// LOD level. Booked by the sweep per emitted part, so it is what was submitted.
    /// </summary>
    private static string TriangleHistogramText(CultureInfo ci)
    {
        string M(double v) => v >= 1e6 ? string.Format(ci, "{0:F1}M", v / 1e6) : string.Format(ci, "{0:N0}", v);
        string[] bandNames = { "0-64", "64-211", "211-640", "640+" };
        (int pass, string name)[] passes =
        {
            ((int)EnumChunkRenderPass.Opaque, "opaque"),
            ((int)EnumChunkRenderPass.TopSoil, "topsoil"),
            ((int)EnumChunkRenderPass.OpaqueNoCull, "leaves+plants (OpaqueNoCull)"),
            ((int)EnumChunkRenderPass.BlendNoCull, "BlendNoCull"),
            ((int)EnumChunkRenderPass.Transparent, "transparent"),
            ((int)EnumChunkRenderPass.Liquid, "liquid"),
        };

        var sb = new StringBuilder();
        double total = 0;
        for (var p = 0; p < FastCuller.HistPasses; p++)
            for (var b = 0; b < FastCuller.HistBands; b++) total += FastCuller.HistTris(p, b);
        if (total <= 0) return "";

        sb.Append("  camera pass by render pass: ");
        var first = true;
        double named = 0;
        foreach (var (pass, name) in passes)
        {
            double sum = 0;
            for (var b = 0; b < FastCuller.HistBands; b++) sum += FastCuller.HistTris(pass, b);
            named += sum;
            if (sum < total * 0.005) continue;
            if (!first) sb.Append(" | ");
            first = false;
            sb.Append(name).Append(' ').Append(M(sum));
            sb.AppendFormat(ci, " ({0:P0})", sum / total);
        }
        if (total - named > total * 0.005) sb.Append(" | other ").Append(M(total - named));
        sb.Append('\n');

        sb.Append("  camera pass by distance: ");
        for (var b = 0; b < FastCuller.HistBands; b++)
        {
            double all = 0, foliage = 0;
            for (var p = 0; p < FastCuller.HistPasses; p++)
            {
                var v = FastCuller.HistTris(p, b);
                all += v;
                if (FastCuller.IsFoliagePass(p)) foliage += v;
            }
            if (b > 0) sb.Append(" | ");
            sb.Append(bandNames[b]).Append(": ").Append(M(all));
            if (all > 0) sb.AppendFormat(ci, " (foliage {0:P0})", foliage / all);
        }
        sb.Append('\n');

        sb.AppendFormat(ci, "  camera pass by lod: lod0 {0}, lod1 {1}, lod2 {2}, lod3 {3}, engine within far lod {4}, far lod tier 1 {5}, tier 2 {6} | foliage range: {7}\n",
            M(FastCuller.HistTrisByLod(0)), M(FastCuller.HistTrisByLod(1)), M(FastCuller.HistTrisByLod(2)), M(FastCuller.HistTrisByLod(3)),
            M(FastCuller.HistTrisByLod(FarMesh.LodNear)),
            M(FastCuller.HistTrisByLod(FarMesh.LodFar) + FastCuller.HistTrisByLod(FarMesh.LodFarSolo)),
            M(FastCuller.HistTrisByLod(FarMesh.LodFar2)),
            FastCuller.FoliageRangeSq > 0
                ? string.Format(ci, "{0:F0} blocks (trees beyond it are trunks)", Math.Sqrt(FastCuller.FoliageRangeSq))
                : "to the view distance (vanilla)");
        sb.AppendFormat(ci, "  shadow foliage range: {0}\n",
            FastCuller.ShadowFoliageRangeSq > 0
                ? string.Format(ci, "{0:F0} blocks (leaves and plants cast no shadow past it)", Math.Sqrt(FastCuller.ShadowFoliageRangeSq))
                : "the cascade's own range (vanilla)");
        if (PoolPassPatches.StatUnknown > 0)
            sb.AppendFormat(ci, "  note: {0:N0} pool draws could not be attributed to a render pass - "
                + "those triangles are counted under 'unknown'\n", PoolPassPatches.StatUnknown);
        sb.Append(FarMeshText(ci));
        return sb.ToString();
    }

    /// <summary>
    /// The far LOD's row: what the build did to the chunks it saw, what it costs on the
    /// tesselation thread, and what is drawn as a picture right now. The triangle figures come
    /// from the histogram's extra LOD levels: "engine within" against "tier 1 / tier 2" is
    /// what the camera pass draws inside and beyond the distance.
    /// </summary>
    private static string FarMeshText(CultureInfo ci)
    {
        if (!FarMeshPatches.Installed) return "";
        string M(double v) => v >= 1e6 ? string.Format(ci, "{0:F1}M", v / 1e6) : string.Format(ci, "{0:N0}", v);
        var sb = new StringBuilder();
        sb.Append("  far lod: ");
        if (!FarMesh.Enabled) sb.Append("OFF ('.komet toggle farmesh')");
        else
        {
            sb.Append(FarMesh.Active ? "ON" : "ON but not drawn (sweep off)");
            var dist = FarMesh.LastEffectiveSq > 0 ? Math.Sqrt(FarMesh.LastEffectiveSq) : 0;
            sb.AppendFormat(ci, " | cells of 2 beyond {0} blocks{1}", dist > 0 ? dist.ToString("F0", ci) : "?",
                FarMesh.DistanceSq > 0 ? "" : " (default rule)");
            if (FarMesh.Tier2) sb.AppendFormat(ci, ", cells of 4 beyond {0}", dist > 0 ? (dist * FarMesh.Tier2Factor).ToString("F0", ci) : "?");
            else sb.Append(", tier 2 off ('.komet toggle farlod2')");
        }
        var builds = FarLod.StatBuilds;
        if (builds > 0)
        {
            var quadsIn = FarLod.StatQuadsIn;
            var outFaces = FarLod.StatCellFaces + FarLod.StatRestFaces;
            sb.AppendFormat(ci, " | {0:N0} builds ({1:N0} chunks): {2} faces in ({3:P0} unit faces), {4} out ({5} cell + {6} rest = {7:P0}), rest blocks {8} -> {9}",
                builds, FarMeshPatches.StatChunks, M(quadsIn), quadsIn > 0 ? FarLod.StatUnitQuads / (double)quadsIn : 0,
                M(outFaces), M(FarLod.StatCellFaces), M(FarLod.StatRestFaces), quadsIn > 0 ? outFaces / (double)quadsIn : 0,
                M(FarLod.StatRestBlocksIn), M(FarLod.StatRestBlocksOut));
            if (FarMeshPatches.StatChunks > 0)
                sb.AppendFormat(ci, " | build {0:F2} ms/chunk", FarLod.StatTicks * 1000.0 / Stopwatch.Frequency / FarMeshPatches.StatChunks);
            var poolReqs = FarLod.StatPoolHits + FarLod.StatPoolMisses;
            if (poolReqs > 0)
                sb.AppendFormat(ci, " | output arrays {0:P0} pooled ({1:N0} requests, {2} MB held)",
                    FarLod.StatPoolHits / (double)poolReqs, poolReqs, FarLod.PooledBytes >> 20);
            if (FarLod.StatRefused > 0) sb.AppendFormat(ci, " | {0:N0} meshes refused (not the quad layout)", FarLod.StatRefused);
            if (FarMeshPatches.StatTooSmall > 0)
                sb.AppendFormat(ci, " | {0:N0} parts too small for a picture (under {1} faces)",
                    FarMeshPatches.StatTooSmall, FarMeshPatches.MinFacesForPicture);
            if (FarLod.StatNoSource > 0) sb.AppendFormat(ci, " | {0:N0} cell faces without a source face", FarLod.StatNoSource);
        }
        sb.AppendFormat(ci, " | in the pools: {0:N0} pictures in {1:N0} lane pools, {2:N0} engine parts stopped at the distance",
            FarMeshPatches.TrackedFar, SpatialPools.LanePools(), FarMeshPatches.TrackedNear);
        var drawn1 = FastCuller.HistTrisByLod(FarMesh.LodFar) + FastCuller.HistTrisByLod(FarMesh.LodFarSolo);
        var drawn2 = FastCuller.HistTrisByLod(FarMesh.LodFar2);
        var drawnNear = FastCuller.HistTrisByLod(FarMesh.LodNear);
        if (drawn1 > 0 || drawn2 > 0 || drawnNear > 0)
            sb.AppendFormat(ci, " | drawn per frame: {0} triangles as tier 1, {1} as tier 2, {2} engine within", M(drawn1), M(drawn2), M(drawnNear));
        if (FarMeshPatches.Broken != null) sb.Append(" | SWITCHED OFF: ").Append(FarMeshPatches.Broken);
        if (FarMeshPatches.StatErrors > 0) sb.AppendFormat(ci, " | ERRORS {0} (client-main.log)", FarMeshPatches.StatErrors);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>The pass probe's row: elapsed GPU time and fragments per chunk pass.</summary>
    private static string PassProbeText(CultureInfo ci)
    {
        if (!GpuPassProbe.Enabled) return "  gpu per pass: probe AUS ('.komet toggle passprobe')\n";
        if (GpuPassProbe.PassSamples[(int)GpuPassProbe.Pass.NearSolid] == 0
            && GpuPassProbe.PassSamples[(int)GpuPassProbe.Pass.CameraOpaque] == 0)
            return "  gpu per pass: no probe result yet\n";

        string One(GpuPassProbe.Pass pass)
        {
            var i = (int)pass;
            if (GpuPassProbe.PassSamples[i] == 0) return "-";
            var ms = string.Format(ci, "{0:F1} ms", GpuPassProbe.PassMs[i]);
            if (!GpuPassProbe.FragmentsSupported) return ms;
            var frags = GpuPassProbe.PassFragments[i];
            return frags >= 1e6
                ? string.Format(ci, "{0} / {1:F0} Mfrag", ms, frags / 1e6)
                : string.Format(ci, "{0} / {1:N0} frag", ms, frags);
        }

        var sb = new StringBuilder();
        sb.AppendFormat(ci, "  gpu per pass (elapsed, every {0}. frame, {1:N0} samples): near solid {2}, near foliage {3} | camera opaque {4}",
            GpuPassProbe.Every, GpuPassProbe.PassSamples[(int)GpuPassProbe.Pass.NearSolid],
            One(GpuPassProbe.Pass.NearSolid), One(GpuPassProbe.Pass.NearFoliage), One(GpuPassProbe.Pass.CameraOpaque));
        if (GpuPassProbe.PassSamples[(int)GpuPassProbe.Pass.FarSolid] > 0)
            sb.AppendFormat(ci, " | far when drawn: solid {0}, foliage {1}",
                One(GpuPassProbe.Pass.FarSolid), One(GpuPassProbe.Pass.FarFoliage));
        if (!GpuPassProbe.FragmentsSupported && GpuPassProbe.FragmentsUnsupportedReason != null)
            sb.Append(" (no fragment counts: ").Append(GpuPassProbe.FragmentsUnsupportedReason).Append(')');
        if (ShadowCullPatches.SkipFoliage)
            sb.AppendFormat(ci, " | FOLIAGE SKIPPED in the shadow maps ({0:N0} pool draws)", ShadowCullPatches.StatFoliageSkipped);
        if (ChunkShaderSwap.Active)
            sb.Append(" | FLAT fragment shader on chunkopaque (diagnostic)");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>The near pass's footprint after the visible-receiver cull, as the report prints it.</summary>
    private static string NearFootprintText(CultureInfo ci)
    {
        if (!ShadowFootprintPatches.Installed) return "the whole box (patch not installed)";
        if (!ShadowFootprintPatches.Enabled) return "the whole box - visible-receiver cull AUS (vanilla)";
        if (ShadowThrottlePatches.NearInterval > 1)
            return string.Format(ci, "the whole box (near cascade retained across frames, the cull stepped aside {0:N0}x)",
                ShadowFootprintPatches.StatYielded);
        return string.Format(ci, "{0:P0} of the box (only casters that can reach a visible receiver; {1:N0} frames tightened)",
            ShadowFootprintPatches.FootprintFraction, ShadowFootprintPatches.StatTightened);
    }

    /// <summary>
    /// The near volume's depth, which is what the near pass really draws: the engine sizes the
    /// box and then projects it with an untranslated ortho, so vanilla's volume is the box
    /// LENGTH centred on the camera - half of the depth extend spent down-sun, where nothing
    /// can cast. Reports what the fit left of it, so the saving is a number and not a claim.
    /// </summary>
    private static string NearVolumeText(CultureInfo ci)
    {
        var vanilla = ShadowDepthPatches.VanillaLength;
        var fitted = ShadowDepthPatches.FittedLength;
        if (!ShadowDepthPatches.Installed || vanilla <= 0)
            return "as the engine builds it";
        if (!ShadowDepthPatches.Enabled)
            return string.Format(ci, "{0:F0} blocks deep - depth fit AUS (vanilla)", vanilla);
        if (fitted <= 0 || fitted >= vanilla)
            return string.Format(ci, "{0:F0} blocks deep (nothing to fit)", vanilla);
        return string.Format(ci, "{0:F0} of {1:F0} blocks deep ({2:P0} of it cut down-sun, {3:N0} fitted)",
            fitted, vanilla, 1.0 - fitted / vanilla, ShadowDepthPatches.StatFits);
    }

    /// <summary>
    /// Milliseconds a frame spent rebuilding pool caches, i.e. the share of "sichtbarkeit"
    /// that is not the sweep at all. The counter is ticks since start, so the per-frame value
    /// comes from the same smoothed delta the other counters use.
    /// </summary>
    private double RebuildMsPerFrame()
        => (rebuildTicksPerFrame?.PerFrame ?? 0) * 1000.0 / Stopwatch.Frequency;

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
        var sb = new StringBuilder(1024);
        WriteFrameAndGpuLines(sb, ci, frame);
        WriteAllocationLines(sb, ci);
        WriteAttributionLines(sb, ci);
        WriteSweepLines(sb, ci, frame);
        WriteShadowLines(sb, ci);
        WriteUploadLines(sb, ci);
        WriteEntityAndWorldLines(sb, ci);

        return sb.ToString();
    }

    /// <summary>Which GL path chunk uploads take, and whether the bulk-copy patch matters on it.</summary>
    private static string UploadPathDescription()
    {
        var bulkCalls = MeshUploadPatches.StatBulkCalls;
        if (bulkCalls > 0) return $"persistent mapping, {bulkCalls:N0} bulk copies";
        if (MeshUploadPatches.StatFallbackCalls > 0) return "glBufferSubData, the bulk copy patch has no effect";
        return "glBufferSubData; the driver "
            + (PersistentMappingPatch.Available ? "can do" : "cannot do")
            + " persistent mapping, flag " + (PersistentMappingPatch.Enabled ? "an" : "aus");
    }


    /// <summary>Frame, GPU, display pacing and the collector - the lines that say whether this
    /// is a CPU problem, a GPU problem or a garbage problem.</summary>
    private void WriteFrameAndGpuLines(StringBuilder sb, CultureInfo ci, double frame)
    {
        var fps = frame > 0 ? 1000.0 / frame : 0;
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
            sb.AppendFormat(ci, "  gpu per stage: before {0:F1} | shadow {1:F1} (far {2:F1}{10}, near {3:F1}) | opaque {4:F1} | "
                + "oit {5:F1} | post {6:F1} | ortho {7:F1} | done {8:F1} ms ({9} samples, GPU span per stage; frame by stamps {11:F1} ms)\n",
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Before],
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone, EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone),
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone),
                GpuFrameTimer.StageSum(EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone),
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Opaque],
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.OIT],
                GpuFrameTimer.StageSum(EnumRenderStage.AfterOIT, EnumRenderStage.AfterPostProcessing, EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit),
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Ortho],
                GpuFrameTimer.StageGpuMs[(int)EnumRenderStage.Done],
                GpuFrameTimer.StageSamples,
                // the average above counts the throttle's skipped frames as zero; this is the
                // pass's cost in the frames that actually drew it - the number a map size is
                // judged by
                GpuFrameTimer.FarDrawnSamples > 0
                    ? string.Format(ci, " = {0:F1} when drawn", GpuFrameTimer.FarDrawnGpuMs)
                    : "",
                GpuFrameTimer.StampSpanMs);
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
    }

    /// <summary>Where the bytes come from: the bracketed threads, the sampler over all of them,
    /// the integrated server's own split, and who sends the single-block packets.</summary>
    private void WriteAllocationLines(StringBuilder sb, CultureInfo ci)
    {
        var serverMb = ServerAllocPatches.ThreadMbPerSecond;
        var clientMb = ClientAllocPatches.ThreadMbPerSecond;
        var poolMb = ClientAllocPatches.PoolMbPerSecond;
        var clientOn = ClientAllocPatches.Enabled && ClientAllocPatches.Entries.Count > 0;
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
        if (clientOn && clientMb + poolMb >= 1) ClientAllocPatches.Write(sb, ci);
        // The sample-based view over EVERY thread, brackets or not: the line that names what
        // "rest" is. Printed whenever it has data; its absence with a reason is data too.
        if (AllocSampler.Enabled && AllocSampler.Samples > 0) AllocSampler.Write(sb, ci);
        else if (AllocSampler.Failure != null)
            sb.Append("  alloc sampling: not available (").Append(AllocSampler.Failure).Append(")\n");
        if (serverMb >= 1 || (capi != null && capi.IsSinglePlayer && ServerAllocPatches.Entries.Count > 0))
            ServerAllocPatches.Write(sb, ci);
        // Single-block packets and who on the server sends them (03.09.: 7.000 ExchangeBlock
        // a second while streaming, a third of all dirty marks).
        if (capi != null && capi.IsSinglePlayer && PacketSourcePatches.StatExchange + PacketSourcePatches.StatSet > 0)
            PacketSourcePatches.Write(sb, ci);
    }

    /// <summary>Hitches, CPU, and the attributions for the buckets that used to have no owner:
    /// the task drain, its tick listeners, the mods and the entity before-stage.</summary>
    private void WriteAttributionLines(StringBuilder sb, CultureInfo ci)
    {
        sb.AppendFormat(ci, "hitches: {0} ('.komet hitch' for details)\n", HitchLog.SummaryLine());
        sb.AppendFormat(ci, "cpu: {0:F1} of {1} cores busy ({2:F0} %)\n",
            FrameStats.CpuCoresBusy, Environment.ProcessorCount,
            100.0 * FrameStats.CpuCoresBusy / Environment.ProcessorCount);
        // The two attributions for the buckets that used to have no owner: "draussen" (the
        // main-thread task drain) and "tick" (its listeners). Printed whenever armed.
        if (MainThreadTaskPatches.Enabled) MainThreadTaskPatches.Write(sb, 5, ci);
        if (TickProfiler.Enabled) TickProfiler.Write(sb, 6, ci, FrameStats.GameTickMs);
        // The same measurements, attributed to the mod they came out of, plus what each mod
        // does and what it cost to load. '.komet mods' prints this block on its own.
        if (ModProfiler.Enabled && ModProfiler.Indexed)
            ModProfiler.Write(sb, ci, FrameStats.AvgFrameMs, RendererProfiler.Enabled);
        if (EntityAnimPatches.Enabled || EntityAnimPatches.StatAnimated > 0) EntityAnimPatches.Write(sb, ci);
    }

    /// <summary>Render stages, the visibility sweep and the draw ranges it emits.</summary>
    private void WriteSweepLines(StringBuilder sb, CultureInfo ci, double frame)
    {
        double Pct(double ms) => frame > 0 ? 100.0 * ms / frame : 0;

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

        // The pool's own bill. Every CPU-heavy job this mod owns runs here, so a loading
        // complaint that is really "the workers never got a core" is one line rather than an
        // inference from three other rows.
        sb.AppendFormat(ci, "worker pool: {0} of {1} awake, {2:F0} % utilised, {3:N0} queued, {4:F0} jobs/s"
            + " ({5:N0} done, {6:N0} cancelled, {7:N0} duplicates dropped), handoff {8:N0} waiting\n",
            JobScheduler.ActiveWorkers, JobScheduler.WorkerCount, 100.0 * JobScheduler.Utilisation,
            JobScheduler.PendingJobs, JobScheduler.JobsPerSecond, JobScheduler.StatCompleted,
            JobScheduler.StatCancelled, JobScheduler.StatDuplicates, JobScheduler.HandoffDepth);
        foreach (var kind in new[] { JobKind.Cull, JobKind.MeshPrep, JobKind.ChunkPrep,
                                     JobKind.Occlusion, JobKind.Hud, JobKind.Warmup })
        {
            var done = JobScheduler.JobsOf(kind);
            if (done == 0) continue;
            sb.AppendFormat(ci, "  {0,-11} {1,8:N0} done, {2,4:N0} queued, {3:F3} ms avg, {4:F2} ms longest\n",
                kind.ToString().ToLowerInvariant(), done, JobScheduler.QueuedOf(kind),
                JobScheduler.AvgMsOf(kind), JobScheduler.PeakMsOf(kind));
        }

        var batches = JobScheduler.StatBatches;
        if (batches > 0)
            sb.AppendFormat(ci, "  worker pool: {0:F3} ms caller wait per batch over {1:N0} batches"
                + "{3}, {2} workers awake\n",
                JobScheduler.StatWaitTicks * 1000.0 / Stopwatch.Frequency / batches,
                batches,
                // Stated, not assumed: Thread.Priority is accepted and silently ignored for
                // ordinary threads on Linux, so "deprioritised" has to be something the OS
                // confirmed rather than something we asked for.
                JobScheduler.ActiveWorkers + (JobScheduler.PriorityLowered ? " (some deprioritised)" : ""),
                // batches that ran inline because a ticket holder had not woken up yet - the
                // number that says how often the machine was too loaded for the parallel path
                JobScheduler.StatContendedInline > 0
                    ? " (" + JobScheduler.StatContendedInline.ToString("N0", ci) + "x inline because of contention)"
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
        if (FastCuller.HistSamples > 0)
            sb.Append(TriangleHistogramText(ci));
        // The order the camera pass is drawn in, and what the pools have become.
        SpatialPools.Count(out var regions, out var regionPools);
        sb.AppendFormat(ci, "  draw order: {0} | pools: {1}\n",
            FastCuller.FrontToBack
                ? string.Format(ci, "nearest first ({0:N0} pool sorts, {1:N0} sorted sweeps since reset)",
                    FastCuller.StatPoolSorts, FastCuller.StatSortedSweeps)
                : "index order (vanilla)",
            SpatialPools.Enabled
                ? string.Format(ci, "routed by {0}-block region, {1:N0} regions holding {2:N0} pools, {3:N0} models routed, {4:N0} pools created, {5:N0} handed to vanilla",
                    SpatialPools.RegionBlocks, regions, regionPools, SpatialPools.StatRouted, SpatialPools.StatNewPools, SpatialPools.StatFallbacks)
                : "first-fit (vanilla)");
        if (bridged > 0)
            sb.AppendFormat(ci, "  gap merge: {0:N0} ranges/frame saved by bridging "
                + "frustum clips\n", bridged);

        // The shadow line only lived in the F7 overlay until 1.40.0, which meant every log the
    }

    /// <summary>The shadow cascades: distance, box, map size, throttle and what the near pass submits.</summary>
    private void WriteShadowLines(StringBuilder sb, CultureInfo ci)
    {
        // shadow work was judged from arrived without a single shadow figure in it.
        if (ShadowPatches.ShadowDistance > 0)
        {
            sb.AppendFormat(ci, "shadows: to {0:F0} blocks, box {1} ({2:F0} blocks wide), fade {3}, "
                + "distance x{4:0.##} | map {5}px = {6:F1} texels per block, lod3 {7}, solid backfaces {8}, "
                + "texel snapping {9}\n",
                ShadowPatches.ShadowDistance,
                ShadowPatches.SymmetricBox ? "sphere" : "vanilla wedge",
                ShadowPatches.ShadowBoxSpan,
                ShadowPatches.FadeFix ? "fix" : "vanilla",
                ShadowPatches.DistanceMultiplier,
                ShadowResPatches.EffectiveMapSize, ShadowTexelsPerBlock(),
                FastCuller.ShadowSkipRedundantLod ? "out" : "in",
                (ShadowCullPatches.Enabled
                    ? string.Format(ci, "culled ({0:N0} passes)", ShadowCullPatches.StatCulledPasses)
                    : "drawn (vanilla)")
                + (ShadowCullPatches.DepthOnly
                    ? ShadowCullPatches.DepthOnlyLive
                        ? string.Format(ci, ", depth-only shader ({0:N0} passes)", ShadowCullPatches.StatDepthOnlyPasses)
                        : ", depth-only shader NOT live (" + (ShadowCullPatches.DepthOnlyState ?? "?") + ")"
                    : ", engine shader"),
                !ShadowStabilityPatches.Installed ? "not installed"
                    : !ShadowStabilityPatches.Enabled ? "off (vanilla)"
                    : string.Format(ci, "on ({0:N0} snaps)", ShadowStabilityPatches.StatSnaps));
            // What the far cascade actually costs is frames drawn, not milliseconds per draw -
            // and that is decided by the coverage margin and the movement limit it buys.
            var farFrames = ShadowThrottlePatches.FarRendered + ShadowThrottlePatches.FarSkipped;
            if (farFrames > 0)
                sb.AppendFormat(ci, "  far cadence: {0:N0} of {1:N0} frames drawn ({2:F0} % saved), "
                    + "every {3}-{4} frames, redraw after {5:0.##} blocks of camera movement "
                    + "(coverage margin {6:0.#})\n",
                    ShadowThrottlePatches.FarRendered, farFrames,
                    100.0 * ShadowThrottlePatches.FarSkipped / farFrames,
                    ShadowThrottlePatches.FarInterval, ShadowThrottlePatches.FarMaxSkip,
                    ShadowThrottlePatches.MoveLimit,
                    ShadowPatches.EffectiveFarBoxMargin);

            // The near cascade's own line: its box and its map, since the two maps can differ.
            if (ShadowPatches.NearBoxSpan > 0)
                sb.AppendFormat(ci, "  near cascade: to {0:F0} blocks ({1:F0} blocks wide) | map {2}px = {3:F1} texels per block"
                    + "{4} | sun {5:F0} deg up\n",
                    ShadowPatches.NearShadowDistance, ShadowPatches.NearBoxSpan,
                    ShadowResPatches.EffectiveNearMapSize, NearShadowTexelsPerBlock(),
                    ShadowResPatches.NearMapSize > 0 && ShadowResPatches.NearMapSizeApplied == 0
                        ? " (configured " + ShadowResPatches.NearMapSize + "px not applied yet"
                          + (ShadowResPatches.LastNearError != null ? ": " + ShadowResPatches.LastNearError : "") + ")"
                        : "",
                    SunElevationDegrees());

            // What the near pass draws, against what the camera pass draws: the pair that says
            // whether its GPU milliseconds are geometry or fill. Counted by the sweep per cull
            // mode, so it is what was submitted, not what lies in the pools.
            if ((nearTrisPerFrame?.PerFrame ?? 0) > 0)
                sb.AppendFormat(ci, "  near pass: {0:N0} triangles in {1:N0} ranges per frame | far pass {4:N0} when drawn"
                    + " (camera pass {2:N0} triangles) | footprint {3}\n",
                    nearTrisPerFrame.PerFrame, nearRangesPerFrame?.PerFrame ?? 0, cameraTrisPerFrame?.PerFrame ?? 0,
                    NearFootprintText(ci), farTrisPerFrame?.PerFrame ?? 0);

            // What the near map's SIZE costs, as arithmetic rather than a measurement: an
            // orthographic map shades a caster over the same texels wherever it stands, so its
            // fragments scale with the map's area. This is the line that says a resolution is
            // a fill decision, not only a sharpness one - and 4096 px over a 51 block box is
            // eighty shadow texels per block, against a block texture of thirty-two pixels.
            {
                var nearFrag = GpuPassProbe.PassFragments[(int)GpuPassProbe.Pass.NearFoliage];
                var nearMs = GpuPassProbe.PassMs[(int)GpuPassProbe.Pass.NearFoliage];
                var px = ShadowResPatches.EffectiveNearMapSize;
                if (nearFrag > 0 && px > 1024)
                    sb.AppendFormat(ci, "  near map size: {0} px shades {1:F0} Mfrag of foliage in {2:F1} ms;"
                                        + " at {3} px that is {4:F0} Mfrag and about {5:F1} ms (area, exact for an ortho map)\n",
                        px, nearFrag / 1e6, nearMs, px / 2, nearFrag / 4e6, nearMs / 4);
            }

            // The passes measured bottom-of-pipe: what each one really took, and what it
            // shaded. The stage row above it is timestamps and inherits whatever was in flight.
            if (GpuPassProbe.Enabled || GpuPassProbe.PassSamples[0] > 0)
                sb.Append(PassProbeText(ci));

            // The near volume's depth: the number that decides how much of the world the near
            // pass draws, since it culls against the projection's own planes.
            if (ShadowPatches.NearExtendVanilla > 0)
                sb.AppendFormat(ci, "  near depth: {0:F0} blocks{1}, volume {2}\n",
                    ShadowPatches.NearExtendUsed,
                    ShadowPatches.NearExtendUsed < ShadowPatches.NearExtendVanilla
                        ? string.Format(ci, " (capped, the engine wants {0:F0})", ShadowPatches.NearExtendVanilla)
                        : " (the engine's)",
                    NearVolumeText(ci));

            // What the near pass is asked to draw, against what vanilla would have asked for.
            // The band is the thing that decides how much foliage the pass transforms, and it
            // is the one number that says whether the tighter cull is doing anything here.
            if (ShadowPatches.TightRangeX > 0)
                sb.AppendFormat(ci, "  near cull band: {0:F0} x {1:F0} blocks{2} (vanilla {3:F0} x {4:F0})\n",
                    ShadowPatches.TightRangeX, ShadowPatches.TightRangeZ,
                    ShadowPatches.TightCullBox ? "" : " - tighter cull AUS",
                    ShadowPatches.VanillaRangeX, ShadowPatches.VanillaRangeZ);
        }
    }

    /// <summary>Chunk upload, the loading pipeline and the two mesh buffer pools.</summary>
    private void WriteUploadLines(StringBuilder sb, CultureInfo ci)
    {
        sb.AppendFormat(ci, "upload {0:F2} ms (max {1:F1}), throttle {2:P0}{3}, prio budget {4} "
            + "({5:N0} chunks, {6:N0}x spread) | occlusion {7:F1} ms on the worker, {8:N0} chunks\n",
            FrameStats.AvgUploadMs, FrameStats.MaxUploadMs, UploadBudget.Gain,
            UploadBudget.StatPressureCuts > 0
                ? string.Format(ci, " ({0:N0}x throttled by frame pressure)", UploadBudget.StatPressureCuts)
                : "",
            PrioUploadPatches.Enabled ? "an" : "AUS",
            PrioUploadPatches.StatUploadedChunks, PrioUploadPatches.StatDeferrals,
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
            TesselationPatches.StatPrefetchedUnpacks);
        if (pipeTotal > 0)
            sb.AppendFormat(ci, "  window pipeline: {0:N0} of {1:N0} hits ({2:F0}%), {3:N0} stale, "
                + "{4:N0} validated ({5} to go), {6:N0} queue entries stepped over\n",
                WindowPrebuilder.StatHits, pipeTotal,
                100.0 * WindowPrebuilder.StatHits / pipeTotal, WindowPrebuilder.StatStale,
                WindowPrebuilder.StatValidated, WindowPrebuilder.ValidateRemaining,
                WindowPrebuilder.StatPredictSkips);

        // Hit rate of the size-class mesh buffer pool. "frisch alloziert" is what still went
        // to the GC despite the pool - the number the whole patch exists to shrink; if it
        // stays high with the pool on, the loading allocation lives somewhere else and this
        // row is the disproof.
        var recyclerAsked = MeshRecyclerPatches.StatHits + MeshRecyclerPatches.StatMisses;
        if (MeshRecyclerPatches.Enabled && recyclerAsked > 0)
            sb.AppendFormat(ci, "  mesh recycler: {0:F0}% hits ({1:N0} requests), {2:N0} MB held, "
                + "{3:N0} MB freshly allocated, {4:N0} evicted\n",
                100.0 * MeshRecyclerPatches.StatHits / recyclerAsked, recyclerAsked,
                MeshRecyclerPatches.HeldBytes / 1048576.0,
                MeshRecyclerPatches.StatMissBytes / 1048576.0,
                MeshRecyclerPatches.StatEvicted);

        if (TightClonePatches.Enabled && TightClonePatches.StatClones > 0)
            sb.AppendFormat(ci, "  tight clone: {0:N0} clones, {1:N0} MB of capacity-sized copies saved\n",
                TightClonePatches.StatClones,
                TightClonePatches.StatBytesSaved / 1048576.0);
        // Printed whenever the pool is armed: hits at 0 with misses climbing means the return
        // path is not firing (the AddToPools postfix), which must not look like "no data yet".
        if (TightClonePatches.Enabled || TightClonePatches.StatClones > 0)
        {
            var hits = TightClonePatches.StatClones;
            var misses = TightClonePatches.StatExtrasMisses;
            var total = hits + misses;
            sb.AppendFormat(ci, "  extras pool: {0:F0}% hits ({1:N0} requests), {2:N0} MB held, {3:N0} dropped{4}\n",
                total > 0 ? 100.0 * hits / total : 0, total,
                TightClonePatches.PooledBytes / 1048576.0,
                TightClonePatches.StatExtrasDropped,
                TightClonePatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        }
        // Particles: what the render thread pays for them, apart from what their own thread did.
        if (ParticlePatches.Enabled)
            sb.AppendFormat(ci, "particles: {0:N0} alive on the main pools ({1:F2} ms/frame: physics {5:F2} + upload {6:F2}{7}), "
                + "{2:N0} off-thread ({3:F2} ms/frame pickup){4}\n",
                ParticlePatches.AliveMainThread, ParticlePatches.MainThreadMs,
                ParticlePatches.AliveOffThread, ParticlePatches.OffThreadPickupMs,
                ParticlePatches.StatCalls == 0 ? " - the bracket is not running" : "",
                Math.Max(0, ParticlePatches.MainThreadMs - ParticlePatches.UploadMs),
                ParticlePatches.UploadMs,
                ParticlePatches.Orphan
                    ? ", buffers renamed"
                    : ParticlePatches.OrphanSupported || !ParticlePatches.ConfiguredOrphan
                        ? ", overwritten in place ('.komet toggle particleorphan')"
                        : ", overwritten in place - the driver has no GL_ARB_invalidate_subdata");

        if (AnimatableCullPatches.Enabled || AnimatableCullPatches.StatCalls > 0)
            sb.AppendFormat(ci, "animatable gate: {0:N0} of {1:N0} calls skipped{2}\n",
                AnimatableCullPatches.StatSkipped, AnimatableCullPatches.StatCalls,
                AnimatableCullPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
    }

    /// <summary>Entities, the minimap, the server's sync and what VRAM came back.</summary>
    private void WriteEntityAndWorldLines(StringBuilder sb, CultureInfo ci)
    {
        if (MinimapPatches.Enabled || MinimapPatches.StatTicks > 0)
            sb.AppendFormat(ci, "minimap: cap {0} tiles/tick, {1:F2} ms per upload tick over {2:N0} ticks, {3}{4}\n",
                MinimapPatches.Cap, MinimapPatches.AvgTickMs, MinimapPatches.StatTicks,
                MinimapPatches.DirectUpload
                    ? string.Format(ci, "direct upload ({0:N0} tiles into {1:N0} components)",
                        MinimapPatches.StatDirectPieces, MinimapPatches.StatDirectComponents)
                    : "FBO path (vanilla)",
                MinimapPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        // Printed whenever armed: "0 geladen, 0 offen" is correct idleness in a settled
        // scene, and must not be confused with a prefix that never ran.
        if (EntityLoadPatches.Enabled || EntityLoadPatches.StatLoaded > 0)
            sb.AppendFormat(ci, "entity load: {0:N0} loaded ({1:N0} promoted, {2:N0} dropped, {3:N0} updates onto held ones), "
                + "{4:N0} open, {5:N0} frames spread, slowest {6:F1} ms{7}{8}\n",
                EntityLoadPatches.StatLoaded, EntityLoadPatches.StatPromoted,
                EntityLoadPatches.StatDropped, EntityLoadPatches.StatUpdatedPending,
                EntityLoadPatches.PendingCount, EntityLoadPatches.StatDeferredFrames,
                EntityLoadPatches.StatWorstMs,
                EntityLoadPatches.StatWorstCode != null ? " (" + EntityLoadPatches.StatWorstCode + ")" : "",
                EntityLoadPatches.Enabled ? "" : Loc.T("komet:hud-off", " (OFF)"));
        if (AnimationWarmup.Enabled || AnimationWarmup.StatShapes > 0)
            sb.AppendFormat(ci, "  anim prewarm: {0:N0} shapes, {1:N0} animations, {2:F0} ms on workers, slowest {3:F1} ms{4}, "
                + "{5:N0} skipped (shape in use), {6:N0} waits ({7:F1} ms), {8:N0} drain holds{9}\n",
                AnimationWarmup.StatShapes, AnimationWarmup.StatAnimations, AnimationWarmup.StatWorkerMs,
                AnimationWarmup.StatWorstMs,
                AnimationWarmup.StatWorstShape != null ? " (" + AnimationWarmup.StatWorstShape + ")" : "",
                AnimationWarmup.StatSkippedInUse, AnimationWarmup.StatWaits, AnimationWarmup.StatWaitMs,
                EntityLoadPatches.StatWarmupHolds,
                AnimationWarmup.Enabled ? "" : " (OFF)");
        // Server half - in singleplayer the statics are shared with the integrated server, so
        // the counters are live here; on a remote server they stay at zero and say so.
        var posTotal = EntitySyncPatches.StatPositionsSent + EntitySyncPatches.StatPositionsSkipped;
        var attrTotal = EntitySyncPatches.StatAttrPathsSent + EntitySyncPatches.StatAttrPathsSkipped;
        if (capi != null && capi.IsSinglePlayer || posTotal + attrTotal > 0)
            sb.AppendFormat(ci, "entity sync (server): positions {0:N0} sent, {1:N0} saved ({2:F0} %), "
                + "{3:N0} hysteresis holds, {4:N0}x cap-sorted | attributes {5:N0} paths sent, {6:N0} saved ({7:F0} %), "
                + "{8:N0} packets suppressed{9}{10}\n",
                EntitySyncPatches.StatPositionsSent, EntitySyncPatches.StatPositionsSkipped,
                posTotal > 0 ? 100.0 * EntitySyncPatches.StatPositionsSkipped / posTotal : 0,
                EntitySyncPatches.StatHysteresisHolds, EntitySyncPatches.StatCapOrderings,
                EntitySyncPatches.StatAttrPathsSent, EntitySyncPatches.StatAttrPathsSkipped,
                attrTotal > 0 ? 100.0 * EntitySyncPatches.StatAttrPathsSkipped / attrTotal : 0,
                EntitySyncPatches.StatAttrPacketsSuppressed,
                EntitySyncPatches.DistanceSendRate ? "" : " (sync tuning OFF)",
                EntitySyncPatches.AttributeNoOpSkip ? "" : " (attr skip OFF)");

        sb.AppendFormat(ci, "vram: {0:N0} MB returned from {1:N0} empty pools, {2:N0} still empty\n",
            PoolReclaimer.StatBytesReclaimed / 1048576.0, PoolReclaimer.StatPoolsReclaimed,
            PoolReclaimer.StatEmptyPools);

        sb.AppendFormat(ci, "inflow brake: {0} % ({1} columns per {2} ms, base {3} per {4} ms), "
            + "{5:F0}s throttled\n",
            InflowBrake.Enabled ? InflowBrake.CurrentPercent.ToString(ci) : "aus",
            InflowBrake.CurrentColumns, InflowBrake.CurrentTickMs,
            InflowBrake.BaseColumns, InflowBrake.BaseTickMs, InflowBrake.SecondsBraking);

        sb.Append("upload path: ").Append(UploadPathDescription());
    }
}