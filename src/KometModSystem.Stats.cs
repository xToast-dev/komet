using System;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;
using Komet.Measure;

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
        sb.AppendFormat(ci, "umgebung: {0} logische kerne, .net {1}, {2}\n",
            Environment.ProcessorCount, Environment.Version, Environment.OSVersion.VersionString);
        sb.AppendFormat(ci, "gc: modus {0}, angefordert {1}, latenz {2} | laufzeit {3:F0} min\n",
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
            asked ?? "(nichts gesetzt)",
            System.Runtime.GCSettings.LatencyMode,
            uptime.Elapsed.TotalMinutes);
        // Physical cores decide every thread budget in the mod (cull, occlusion, worldgen);
        // the source says whether the OS answered or the rule of thumb did - a laptop's
        // 2c/4t reported as four cores was the whole thread oversubscription story of 02.09.
        sb.AppendFormat(ci, "kerne: {0} physisch von {1} logischen ({2})\n",
            CpuTopology.PhysicalCores, CpuTopology.LogicalCores, CpuTopology.Source);
        sb.AppendFormat(ci, "cull-threads {0}, occlusion-threads {1}, safemode {2}\n",
            FastCuller.Workers.ThreadCount + 1, FastChunkCuller.Workers.ThreadCount + 1,
            safeMode ? "AN - die messung sagt nichts ueber die mod aus!" : "aus");
        if (MeasurementPatches.SkippedBrackets.Count > 0)
            sb.AppendFormat(ci, "messung ohne: {0} (engine-build weicht ab, diese zeilen bleiben leer)\n",
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
            sb.AppendFormat(ci, "anzeige: vsync {0}, fps-limit {1:F0}, monitor {2} Hz\n",
                vsync == 1 ? "an" : vsync == 0 ? "aus" : "modus " + vsync,
                Vintagestory.Client.ScreenManager.Platform?.MaxFps ?? 0, refresh);
        }
        catch (Exception e)
        {
            sb.Append("anzeige: nicht abfragbar (").Append(e.GetType().Name).Append(")\n");
        }

        var delta = ConfigDelta(config);
        sb.Append("konfig: ").Append(ConfigFile).Append(" layout ").Append(KometConfig.Current)
          .Append(", abweichend vom standard: ").Append(delta ?? "keine").Append('\n');

        // ---- the frame ----
        sb.Append("\n---- frame ----\n").Append(BuildStats()).Append('\n');

        // ---- hitches ----
        sb.Append("\n---- ruckler ----\n").Append(HitchLog.BuildReport()).Append('\n');

        // ---- dirty marks, only when the sampler is on ----
        if (Patches.RetessSourcePatches.SampleSources)
            sb.Append("\n---- dirty-marks ----\n")
              .Append(Patches.RetessSourcePatches.BuildReport()).Append('\n');

        sb.Append("==================== ende ====================");
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
            DebugHud.Section(sb, "teuerste renderer");
            Patches.RendererProfiler.Write(sb, 8);
            // Against the stage totals this says whether the list above explains the frame.
            // The firepit renderer was found exactly because it did not.
            DebugHud.Row(sb, "= alle zusammen", null, DebugHud.Ms(Patches.RendererProfiler.TotalMs),
                Patches.RendererProfiler.Count + " namen");
            DebugHud.Row(sb, "davon gemessen",
                Patches.RendererProfiler.StatWrapped + "/" + Patches.RendererProfiler.StatTotal);
        }

        DebugHud.Section(sb, "komet");
        WriteKometWarnings(sb, frameMs);

        // -- the sweep --
        DebugHud.Row(sb, "sichtbarkeit", DebugHud.Pct(FrameStats.AvgCullMs, frameMs), DebugHud.Ms(FrameStats.AvgCullMs),
            CullKernelShort());
        DebugHud.Row(sb, "teile getest.", DebugHud.N(partsPerFrame?.PerFrame ?? 0), null,
            DebugHud.N(cellsSkippedPerFrame?.PerFrame ?? 0) + " zellen weg");
        DebugHud.Row(sb, "davon rebuild", DebugHud.N(rebuildsPerFrame?.PerFrame ?? 0), DebugHud.Ms(RebuildMsPerFrame()),
            FastCuller.StatIncInserts + FastCuller.StatIncRemovals > 0
                ? DebugHud.N(FastCuller.StatIncInserts) + " +/" + DebugHud.N(FastCuller.StatIncRemovals) + " - inkrementell"
                : null);
        // raw running totals: if these stay at zero the patch is not firing at all, which is a
        // different problem from the smoothed per-frame figures reading zero
        DebugHud.Row(sb, "sweeps/frame", DebugHud.N(sweepsPerFrame?.PerFrame ?? 0), null,
            DebugHud.N(batchesPerFrame?.PerFrame ?? 0) + " parallel-batches");
        if (CullVerifier.SampleEvery > 0 || CullVerifier.StatMismatches > 0)
            DebugHud.Row(sb, "sweep-check", DebugHud.N(CullVerifier.StatChecked), null,
                CullVerifier.StatMismatches > 0
                    ? "!! " + DebugHud.N(CullVerifier.StatMismatches) + " ABWEICHUNGEN (log)"
                    : "alle gleich vanilla");
        var raw = rawRangesPerFrame?.PerFrame ?? 0;
        var emitted = rangesPerFrame?.PerFrame ?? 0;
        DebugHud.Row(sb, "draw ranges", DebugHud.N(emitted), null,
            "von " + DebugHud.N(raw) + " (" + (emitted > 0 ? raw / emitted : 1).ToString("F1", CultureInfo.CurrentCulture) + "x)");
        DebugHud.Row(sb, "occlusion", null, DebugHud.Ms(FastChunkCuller.StatLastMs), "worker-thread");

        // -- shadows --
        var shadowFrames = Patches.ShadowThrottlePatches.FarRendered + Patches.ShadowThrottlePatches.FarSkipped;
        if (shadowFrames > 0)
            // "schatten-takt", not "schatten fern": the frame-aufteilung block above already
            // has a row named schatten fern that means milliseconds - one name, one meaning
            DebugHud.Row(sb, "schatten-takt",
                "1/" + Patches.ShadowThrottlePatches.FarInterval + "-1/" + Patches.ShadowThrottlePatches.FarMaxSkip, null,
                (100.0 * Patches.ShadowThrottlePatches.FarSkipped / shadowFrames).ToString("F0", CultureInfo.CurrentCulture)
                + " % ferne kaskaden gespart");
        if (Patches.ShadowPatches.ShadowDistance > 0)
        {
            DebugHud.Row(sb, "schatten bis", DebugHud.N(Patches.ShadowPatches.ShadowDistance), null,
                "blöcke · box " + (Patches.ShadowPatches.SymmetricBox ? "kugel" : "vanilla")
                + " · fade " + (Patches.ShadowPatches.FadeFix ? "fix" : "vanilla"));
            // texels per block is the number that decides whether thin geometry (foliage!)
            // still casts a shadow - map edge over the box's world size
            DebugHud.Row(sb, "schattenmap", Patches.ShadowResPatches.EffectiveMapSize + "px", null,
                ShadowTexelsPerBlock().ToString("F1", CultureInfo.CurrentCulture) + " texel je block");
        }

        // -- uploads and the loading pipeline --
        DebugHud.Row(sb, "upload gain", UploadBudget.Gain.ToString("P0", CultureInfo.CurrentCulture), null,
            "vom vanilla-budget"
            + (UploadBudget.StatPressureCuts > 0
                ? " · " + UploadBudget.StatPressureCuts + "x frame-druck"
                : ""));
        // Shown while the budget is armed even at 0 activity: "0 chunks" is correct idleness,
        // a missing row would be indistinguishable from a prefix that never ran (the edge-prio
        // lesson - idle and broken must not look the same).
        if (Patches.PrioUploadPatches.Enabled || Patches.PrioUploadPatches.StatUploadedChunks > 0)
            DebugHud.Row(sb, "prio-upload", DebugHud.N(Patches.PrioUploadPatches.StatUploadedChunks), null,
                "chunks, " + DebugHud.N(Patches.PrioUploadPatches.StatDeferrals) + "x verteilt"
                + (Patches.PrioUploadPatches.Enabled ? "" : " (AUS)"));
        if (InflowBrake.Enabled)
            DebugHud.Row(sb, "zufluss", InflowBrake.CurrentPercent + " %", null,
                InflowBrake.CurrentPercent < 100
                    ? InflowBrake.CurrentColumns + " spalten / " + InflowBrake.CurrentTickMs + " ms"
                    : "voll");
        if (Patches.TesselationPatches.StatPrefetchedUnpacks > 0)
            DebugHud.Row(sb, "prefetch", DebugHud.N(Patches.TesselationPatches.StatPrefetchedUnpacks), null,
                "chunks vorentpackt");
        var pipeTotal = WindowPrebuilder.StatHits + WindowPrebuilder.StatMisses;
        if (pipeTotal > 0)
        {
            var tail = DebugHud.N(WindowPrebuilder.StatHits) + "/" + DebugHud.N(pipeTotal) + " fenster";
            if (WindowPrebuilder.StatStale > 0) tail += ", " + DebugHud.N(WindowPrebuilder.StatStale) + " stale";
            if (WindowPrebuilder.ValidateRemaining > 0)
                tail += " (validiert " + DebugHud.N(WindowPrebuilder.StatValidated) + ")";
            DebugHud.Row(sb, "fenster-pipe",
                (100.0 * WindowPrebuilder.StatHits / pipeTotal).ToString("F0", CultureInfo.CurrentCulture) + " %",
                null, tail);
        }
        // Always shown while the feature is on: "0 vorgezogen über N sweeps" is the line
        // that separates "correctly idle" from "prefix never ran" - the first field report
        // could not tell the two apart, which is this project's oldest trap.
        if (Patches.EdgeRetessPriorityPatches.Enabled || Patches.EdgeRetessPriorityPatches.StatPromoted > 0)
            DebugHud.Row(sb, "edge-prio", DebugHud.N(Patches.EdgeRetessPriorityPatches.StatPromoted), null,
                "rand-reparaturen vorgezogen, " + DebugHud.N(Patches.EdgeRetessPriorityPatches.StatSweeps)
                + " sweeps"
                + (Patches.EdgeRetessPriorityPatches.StatBusySkips > 0
                    ? ", " + DebugHud.N(Patches.EdgeRetessPriorityPatches.StatBusySkips) + "x prio-voll"
                    : "")
                + (Patches.EdgeRetessPriorityPatches.Enabled ? "" : " (AUS)"));
        // Shown while the gate is armed even at 0 - idle must not look like broken.
        if (Patches.AnimatableCullPatches.Enabled || Patches.AnimatableCullPatches.StatCalls > 0)
            DebugHud.Row(sb, "animatable-gate", DebugHud.N(Patches.AnimatableCullPatches.StatSkipped), null,
                "von " + DebugHud.N(Patches.AnimatableCullPatches.StatCalls) + " aufrufen uebersprungen"
                + (Patches.AnimatableCullPatches.Enabled ? "" : " (AUS)"));
        if (Patches.EdgeCoalescePatches.StatAbsorbed + Patches.EdgeCoalescePatches.StatFlushed > 0)
            DebugHud.Row(sb, "edge-koalesz", DebugHud.N(Patches.EdgeCoalescePatches.StatAbsorbed), null,
                "gespart, " + DebugHud.N(Patches.EdgeCoalescePatches.StatFlushed) + " ausgegeben, "
                + DebugHud.N(Patches.EdgeCoalescePatches.PendingCount) + " offen"
                + (Patches.EdgeCoalescePatches.Enabled ? "" : " (AUS)"));
        if (Patches.RetessSourcePatches.MarksPerSecond > 0.5)
            DebugHud.Row(sb, "dirty-marks",
                Patches.RetessSourcePatches.MarksPerSecond.ToString("F0", CultureInfo.CurrentCulture) + "/s",
                null,
                Patches.RetessSourcePatches.EdgeMarksPerSecond.ToString("F0", CultureInfo.CurrentCulture)
                + "/s rand, '.komet retess'");

        // -- the rest --
        if (Patches.EntityTessPatches.StatAllowed + Patches.EntityTessPatches.StatDeferred > 0)
            DebugHud.Row(sb, "entity-tess", DebugHud.N(Patches.EntityTessPatches.StatAllowed), null,
                DebugHud.N(Patches.EntityTessPatches.StatDeferred) + " verschoben"
                // the budget's liveness gap made visible: the first call per frame is
                // uncapped, so ONE fat entity can still spike a frame - this names it
                + (Patches.EntityTessPatches.StatWorstMs >= 5
                    ? " · langsamster " + Patches.EntityTessPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)
                      + " ms" + (Patches.EntityTessPatches.StatWorstName != null
                          ? " (" + Patches.EntityTessPatches.StatWorstName + ")" : "")
                    : "")
                + (Patches.EntityTessPatches.Enabled ? "" : " (AUS)"));
        // Shown while armed even at 0 - a join flood is the only time it has work, and
        // "0 geladen" then has to read as correct idleness, not as a prefix that never ran.
        if (Patches.EntityLoadPatches.Enabled || Patches.EntityLoadPatches.StatLoaded > 0)
            DebugHud.Row(sb, "entity-laden", DebugHud.N(Patches.EntityLoadPatches.StatLoaded), null,
                DebugHud.N(Patches.EntityLoadPatches.PendingCount) + " offen, "
                + DebugHud.N(Patches.EntityLoadPatches.StatDeferredFrames) + " frames verteilt"
                + (Patches.EntityLoadPatches.StatWorstMs >= 5
                    ? " · langsamste " + Patches.EntityLoadPatches.StatWorstMs.ToString("F0", CultureInfo.CurrentCulture)
                      + " ms" + (Patches.EntityLoadPatches.StatWorstCode != null
                          ? " (" + Patches.EntityLoadPatches.StatWorstCode + ")" : "")
                    : "")
                + (Patches.EntityLoadPatches.Enabled ? "" : " (AUS)"));
        if (Patches.MinimapPatches.Enabled || Patches.MinimapPatches.StatTicks > 0)
            DebugHud.Row(sb, "minimap", Patches.MinimapPatches.Cap + "/tick", DebugHud.Ms(Patches.MinimapPatches.AvgTickMs),
                DebugHud.N(Patches.MinimapPatches.StatTicks) + " upload-ticks"
                + (Patches.MinimapPatches.DirectUpload ? ", direkt " + DebugHud.N(Patches.MinimapPatches.StatDirectPieces) + " kacheln" : ", FBO-pfad")
                + (Patches.MinimapPatches.Enabled ? "" : " (AUS)"));
        if (Patches.MainThreadTaskPatches.Enabled)
        {
            var topTasks = Patches.MainThreadTaskPatches.Top(1);
            DebugHud.Row(sb, "mt-tasks", null, DebugHud.Ms(FrameStats.AvgMainTaskMs),
                (topTasks.Count > 0
                    ? "meist " + topTasks[0].code + " " + topTasks[0].ms.ToString("F2", CultureInfo.CurrentCulture) + " ms"
                    : "keine tasks")
                + (Patches.MainThreadTaskPatches.StatBudgetCuts > 0
                    ? " · " + DebugHud.N(Patches.MainThreadTaskPatches.StatBudgetCuts) + " frames gekappt" : ""));
        }
        // Shown while armed even at 0 animated - an empty scene has nothing to animate,
        // and that has to read as idleness, not as a loop that never ran.
        if (Patches.EntityAnimPatches.Enabled || Patches.EntityAnimPatches.StatAnimated > 0)
            DebugHud.Row(sb, "entity-anim", DebugHud.N((long)Math.Round(Patches.EntityAnimPatches.AvgAnimated)) + "/frame",
                DebugHud.Ms(Patches.EntityAnimPatches.AvgAnimMs),
                "vor-render " + Patches.EntityAnimPatches.AvgBeforeMs.ToString("F2", CultureInfo.CurrentCulture) + " ms, "
                + DebugHud.N(Patches.EntityAnimPatches.StatSkipped) + " anim-frames gespart"
                + (Patches.EntityAnimPatches.LodEnabled ? "" : " (lod AUS)")
                + (Patches.EntityAnimPatches.Enabled ? "" : " (AUS)"));
        if (Patches.TickProfiler.Enabled && Patches.TickProfiler.StatWrapped > 0)
        {
            var topTick = Patches.TickProfiler.Top(1);
            DebugHud.Row(sb, "tick-listener", DebugHud.N(Patches.TickProfiler.StatWrapped), DebugHud.Ms(Patches.TickProfiler.TotalMs),
                topTick.Count > 0
                    ? "meist " + topTick[0].name + " " + topTick[0].ms.ToString("F2", CultureInfo.CurrentCulture) + " ms"
                    : "");
        }
        if (Patches.FirepitPatches.StatSkipped > 0 || Patches.FirepitPatches.StatFastPath > 0
            || Patches.FirepitPatches.StatNearVanilla > 0)
            DebugHud.Row(sb, "firepit-gate", DebugHud.N(Patches.FirepitPatches.StatSkipped), null,
                "weg, " + DebugHud.N(Patches.FirepitPatches.StatFastPath) + " cache, "
                + DebugHud.N(Patches.FirepitPatches.StatNearVanilla) + " vanilla"
                + (Patches.FirepitPatches.FastPathBroken ? " !! CACHE DEFEKT (log)" : ""));
        if (PoolReclaimer.StatPoolsReclaimed > 0)
            DebugHud.Row(sb, "vram frei", DebugHud.N(PoolReclaimer.StatBytesReclaimed / 1048576.0) + " MB", null,
                DebugHud.N(PoolReclaimer.StatPoolsReclaimed) + " pools zurückgegeben");
    }

    /// <summary>
    /// The !!-rows alone. Shared between the full view's komet section and the compact view:
    /// a safemode session, a running stress test or an armed diagnostic changes what every
    /// other number means, so no view may hide them.
    /// </summary>
    private void WriteKometWarnings(StringBuilder sb, double frameMs)
    {
        if (safeMode) DebugHud.Row(sb, "!! SAFEMODE", "AN", null, "alles vanilla, '.komet safemode'");
        if (StressTest.StatusLine != null) DebugHud.Row(sb, "!! STRESSTEST", null, null, StressTest.StatusLine);
        var diag = ActiveDiagnostics();
        if (diag != null) DebugHud.Row(sb, "!! DIAGNOSE", null, null, diag);
    }

    /// <summary>Which of the two bit-identical sweep kernels is running, and on how many threads.</summary>
    private static string CullKernel()
    {
        var kernel = !FastCuller.VectorAvailable ? "skalar (keine AVX-CPU)"
                      : FastCuller.VectorCulling ? "avx2 (4 teile je befehl)"
                      : "skalar (vektorkernel aus)";
        var helpers = FastCuller.Workers.ThreadCount;
        return helpers == 0
            ? kernel + ", 1 thread"
            : kernel + ", " + (helpers + 1) + " threads (eigene, nicht der threadpool)";
    }

    /// <summary>The same, but sized for a HUD tail - the long form was the widest line of the
    /// whole overlay and stretched every other row's whitespace with it.</summary>
    private static string CullKernelShort()
    {
        var kernel = FastCuller.VectorAvailable && FastCuller.VectorCulling ? "avx2" : "skalar";
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
            parts.Add("renderer-profiler (" + Patches.RendererProfiler.StatWrapped + " gewickelt, 'toggle profiler')");
        if (Patches.RetessSourcePatches.SampleSources) parts.Add("retess-quellen ('toggle retess')");
        if (CullVerifier.SampleEvery > 0) parts.Add("sweep-gegenprobe ('toggle cullcheck')");
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
        if (!FrameStats.HasData) return "komet: sammelt noch - die Zaehler brauchen ein paar hundert gerenderte Frames";

        var ci = CultureInfo.CurrentCulture;
        var frame = FrameStats.AvgFrameMs;
        var fps = frame > 0 ? 1000.0 / frame : 0;
        double Pct(double ms) => frame > 0 ? 100.0 * ms / frame : 0;

        var sb = new StringBuilder(1024);
        sb.AppendFormat(ci, "komet {0} - Mittel ueber 240 Frames (F7 = HUD)\n",
            KometVersion.Display(Mod.Info.Version));

        sb.AppendFormat(ci, "frame {0:F2} ms = {1:F0} fps, schlechtester {2:F1} ms",
            frame, fps, FrameStats.MaxFrameMs);
        var worst = DebugHud.WorstFrameTail();
        if (worst != null) sb.Append(" (davon ").Append(worst).Append(')');
        // The gpu figure carries its sample count: a frozen value and a live one look the
        // same, and a frozen one (the ring stall of 02.09.) was read as "GPU-bound" twice.
        // The span is not the load: GL_TIME_ELAPSED counts the GPU's idle gaps too, so the
        // driver's busy figure (sysfs, amdgpu) prints next to it where it exists.
        sb.AppendFormat(ci, " | game tick {0:F2} ms | gpu {1:F2} ms ({7} proben{8}) | gc {2:F1} ms/s pausen, "
            + "{3:F0} MB/s alloc, gen0 {4:F0}/s, gen2 {5:F1}/s, modus {6}\n",
            FrameStats.GameTickMs, GpuFrameTimer.GpuMs, FrameStats.GcPauseMsPerSecond,
            FrameStats.AllocMbPerSecond, FrameStats.Gen0PerSecond, FrameStats.Gen2PerSecond,
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation", GpuFrameTimer.StatSamples,
            GpuBusy.Available ? string.Format(ci, ", auslastung {0} % laut {1}", GpuBusy.Percent, GpuBusy.Source) : "");
        // Where the GPU's milliseconds go, stage by stage: the figure that decides whether the
        // shadow map step, the far cascade's LOD or a post-processing mod is the lever.
        if (GpuFrameTimer.StageSamples > 0)
            sb.AppendFormat(ci, "  gpu je stage: before {0:F1} | schatten {1:F1} (fern {2:F1}, nah {3:F1}) | opaque {4:F1} | "
                + "oit {5:F1} | post {6:F1} | ortho {7:F1} | done {8:F1} ms ({9} proben, GPU-spanne je stage)\n",
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
            sb.AppendFormat(ci, "  gc-details: gen1 {0:F1}/s, befoerdert {1:F0} MB/s = {2:F1} MB je sammlung "
                + "(bis zu {3:F0} % der allokation ueberlebt), letzte {4} {5:F1} ms pause, heap {6:F0} MB\n",
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
                sb.AppendFormat(ci, "  alloc-quellen: main {0:F0}, client-threads {1:F0}, threadpool {2:F0}, "
                    + "prefetch {3:F0}, server {4:F0}, rest {5:F0} MB/s (rest = ungemessen)\n",
                    FrameStats.MainAllocMbPerSecond, clientMb, poolMb,
                    FrameStats.PrefetchAllocMbPerSecond, serverMb,
                    Math.Max(0.0, FrameStats.AllocMbPerSecond - FrameStats.MainAllocMbPerSecond
                        - clientMb - poolMb - FrameStats.PrefetchAllocMbPerSecond - serverMb));
            else
                sb.AppendFormat(ci, "  alloc-quellen: netz {0:F0}, main {1:F0}, prefetch {2:F0}, "
                    + "tess {3:F0}, server {4:F0}, rest {5:F0} MB/s (rest = ungemessen)\n",
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
            sb.Append("  alloc-stichprobe: nicht verfuegbar (").Append(AllocSampler.Failure).Append(")\n");
        if (serverMb >= 1 || (capi != null && capi.IsSinglePlayer && Patches.ServerAllocPatches.Entries.Count > 0))
            Patches.ServerAllocPatches.Write(sb, ci);
        // Single-block packets and who on the server sends them (03.09.: 7.000 ExchangeBlock
        // a second while streaming, a third of all dirty marks).
        if (capi != null && capi.IsSinglePlayer && Patches.PacketSourcePatches.StatExchange + Patches.PacketSourcePatches.StatSet > 0)
            Patches.PacketSourcePatches.Write(sb, ci);
        sb.AppendFormat(ci, "ruckler: {0} ('.komet hitch' fuer details)\n", HitchLog.SummaryLine());
        sb.AppendFormat(ci, "cpu: {0:F1} von {1} kernen beschaeftigt ({2:F0} %)\n",
            FrameStats.CpuCoresBusy, Environment.ProcessorCount,
            100.0 * FrameStats.CpuCoresBusy / Environment.ProcessorCount);
        // The two attributions for the buckets that used to have no owner: "draussen" (the
        // main-thread task drain) and "tick" (its listeners). Printed whenever armed.
        if (Patches.MainThreadTaskPatches.Enabled) Patches.MainThreadTaskPatches.Write(sb, 5, ci);
        if (Patches.TickProfiler.Enabled) Patches.TickProfiler.Write(sb, 6, ci, FrameStats.GameTickMs);
        if (Patches.EntityAnimPatches.Enabled || Patches.EntityAnimPatches.StatAnimated > 0) Patches.EntityAnimPatches.Write(sb, ci);

        var shadows = FrameStats.ShadowMs;
        sb.AppendFormat(ci, "stages: opaque {0:F2} | schatten {1:F2} ({2:F0}%) | oit {3:F2} | "
            + "ortho {4:F2} | done {5:F2}\n",
            FrameStats.StageMs[(int)EnumRenderStage.Opaque], shadows, Pct(shadows),
            FrameStats.StageMs[(int)EnumRenderStage.OIT],
            FrameStats.StageMs[(int)EnumRenderStage.Ortho],
            FrameStats.StageMs[(int)EnumRenderStage.Done]);
        sb.AppendFormat(ci, "  post/compose {0:F2} | ausserhalb der stages {1:F2} (davon swap {2:F2})\n",
            FrameStats.PostComposeMs, FrameStats.OutsideStagesMs, FrameStats.AvgSwapMs);

        sb.AppendFormat(ci, "sichtbarkeit {0:F2} ms ({1:F0}%), {2:N0} teile getestet, "
            + "{3:N0} pools ganz uebersprungen\n",
            FrameStats.AvgCullMs, Pct(FrameStats.AvgCullMs),
            partsPerFrame?.PerFrame ?? 0, FastCuller.StatPoolsSkipped);
        sb.AppendFormat(ci, "  davon {0:F2} ms cache-rebuild ({1:N0}/frame), {2:N0} sweeps/frame "
            + "ueber {3:N0} pools, kernel {4}\n",
            RebuildMsPerFrame(), rebuildsPerFrame?.PerFrame ?? 0,
            sweepsPerFrame?.PerFrame ?? 0, hud?.PoolCount ?? 0, CullKernel());
        sb.AppendFormat(ci, "  inkrementell: {0:N0} einfuegungen, {1:N0} entfernungen ohne rebuild\n",
            FastCuller.StatIncInserts, FastCuller.StatIncRemovals);

        // The share of the sweep that was waiting rather than culling. Near zero is the healthy
        // state and the reason this line exists: it used to be most of the sweep, invisibly,
        // because the batch ran on the ThreadPool behind the game's own chunk tesselation.
        // The pool shape, so the grid's cell target can be set from the real thing. The
        // benchmark's optimum moves with parts per pool and the value in use was tuned against
        // a modelled shape that was 5.8x off in pool count.
        if (FastCuller.StatPoolsLive > 0)
            sb.AppendFormat(ci, "  poolform: {0:N0} teile in {1:N0} pools = {2:N0} je pool, "
                + "zellziel {3}\n",
                FastCuller.StatPartsHeld, FastCuller.StatPoolsLive,
                FastCuller.StatPartsHeld / (double)FastCuller.StatPoolsLive,
                FastCuller.PartsPerCellTarget);

        var batches = FastCuller.Workers.StatRuns;
        if (batches > 0)
            sb.AppendFormat(ci, "  cull-threads: {0:F3} ms warten je batch ueber {1:N0} batches"
                + "{3}, occlusion auf {2} threads\n",
                FastCuller.Workers.StatWaitTicks * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency / batches,
                batches,
                // Stated, not assumed: Thread.Priority is accepted and silently ignored for
                // ordinary threads on Linux, so "deprioritised" has to be something the OS
                // confirmed rather than something we asked for.
                (FastChunkCuller.Workers.ThreadCount + 1)
                    + (FastChunkCuller.Workers.PriorityLowered ? " (nachrangig)" : ""),
                // batches that ran inline because a helper had not woken up yet - the number
                // that says how often the machine was too loaded for the parallel path
                FastCuller.Workers.StatContendedInline > 0
                    ? " (" + FastCuller.Workers.StatContendedInline.ToString("N0", ci) + "x inline wegen kontention)"
                    : "");

        var diag = ActiveDiagnostics();
        if (diag != null)
            sb.AppendFormat(ci, "  DIAGNOSE LAEUFT MIT: {0} - kostet frame-zeit, safemode schaltet das nicht ab\n", diag);
        if (CullVerifier.SampleEvery > 0 || CullVerifier.StatMismatches > 0)
            sb.AppendFormat(ci, "  sweep-check: {0:N0} sweeps gegen vanilla geprueft, {1:N0} abweichungen\n",
                CullVerifier.StatChecked, CullVerifier.StatMismatches);

        var raw = rawRangesPerFrame?.PerFrame ?? 0;
        var emitted = rangesPerFrame?.PerFrame ?? 0;
        var bridged = bridgedPerFrame?.PerFrame ?? 0;
        sb.AppendFormat(ci, "draw ranges {0:N0} von {1:N0} ({2:F1}x), draw calls {3:N0}/frame, "
            + "{4:N0} dreiecke\n",
            emitted, raw, emitted > 0 ? raw / emitted : 1.0,
            hud?.DrawCallsPerFrame ?? 0, hud?.RenderedTriangles ?? 0);
        if (bridged > 0)
            sb.AppendFormat(ci, "  luecken-merge: {0:N0} ranges/frame durch ueberbrueckte "
                + "frustum-clips gespart\n", bridged);

        // The shadow line only lived in the F7 overlay until 1.40.0, which meant every log the
        // shadow work was judged from arrived without a single shadow figure in it.
        if (Patches.ShadowPatches.ShadowDistance > 0)
            sb.AppendFormat(ci, "schatten: bis {0:F0} blocks, box {1} ({2:F0} blocks breit), fade {3}, "
                + "weite x{4:0.##} | map {5}px = {6:F1} texel je block, lod3 {7}\n",
                Patches.ShadowPatches.ShadowDistance,
                Patches.ShadowPatches.SymmetricBox ? "kugel" : "vanilla-kegel",
                Patches.ShadowPatches.ShadowBoxSpan,
                Patches.ShadowPatches.FadeFix ? "fix" : "vanilla",
                Patches.ShadowPatches.DistanceMultiplier,
                Patches.ShadowResPatches.EffectiveMapSize, ShadowTexelsPerBlock(),
                FastCuller.ShadowSkipRedundantLod ? "raus" : "drin");

        sb.AppendFormat(ci, "upload {0:F2} ms (max {1:F1}), throttle {2:P0}{3}, prio-budget {4} "
            + "({5:N0} chunks, {6:N0}x verteilt) | occlusion {7:F1} ms auf worker, {8:N0} chunks\n",
            FrameStats.AvgUploadMs, FrameStats.MaxUploadMs, UploadBudget.Gain,
            UploadBudget.StatPressureCuts > 0
                ? string.Format(ci, " ({0:N0}x frame-druck gedrosselt)", UploadBudget.StatPressureCuts)
                : "",
            Patches.PrioUploadPatches.Enabled ? "an" : "AUS",
            Patches.PrioUploadPatches.StatUploadedChunks, Patches.PrioUploadPatches.StatDeferrals,
            FastChunkCuller.StatLastMs, FastChunkCuller.StatChunksSnapshotted);
        if (FrameStats.StatPoolAllocs > 0)
            sb.AppendFormat(ci, "  mesh-pools angelegt: {0:N0} seit reset, {1:F1} ms gesamt, laengste {2:F1} ms "
                + "(GL-puffer im upload-bucket des jeweiligen frames)\n",
                FrameStats.StatPoolAllocs, FrameStats.StatPoolAllocMs, FrameStats.MaxPoolAllocMs);

        var pipeTotal = WindowPrebuilder.StatHits + WindowPrebuilder.StatMisses;
        sb.AppendFormat(ci, "laden: {0:F0} chunks/s empfangen, {1:F0}/s tesseliert a {2:F2} ms "
            + "({3:F1} nachbarn, {4:F1} licht, {5:F0}% rand, {6:F0} MB/s: nachbarn {7:F0}, "
            + "licht {8:F0}, klone {9:F0}, shapes {10:F0}, rest {11:F0}), warteschl. {12:N0}, "
            + "prefetch {13:N0} entpackt\n",
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
            sb.AppendFormat(ci, "  fenster-pipeline: {0:N0} von {1:N0} treffer ({2:F0}%), {3:N0} stale, "
                + "{4:N0} validiert (noch {5})\n",
                WindowPrebuilder.StatHits, pipeTotal,
                100.0 * WindowPrebuilder.StatHits / pipeTotal, WindowPrebuilder.StatStale,
                WindowPrebuilder.StatValidated, WindowPrebuilder.ValidateRemaining);

        // Hit rate of the size-class mesh buffer pool. "frisch alloziert" is what still went
        // to the GC despite the pool - the number the whole patch exists to shrink; if it
        // stays high with the pool on, the loading allocation lives somewhere else and this
        // row is the disproof.
        var recyclerAsked = Patches.MeshRecyclerPatches.StatHits + Patches.MeshRecyclerPatches.StatMisses;
        if (Patches.MeshRecyclerPatches.Enabled && recyclerAsked > 0)
            sb.AppendFormat(ci, "  mesh-recycler: {0:F0}% treffer ({1:N0} anfragen), {2:N0} MB vorgehalten, "
                + "{3:N0} MB frisch alloziert, {4:N0} verdraengt\n",
                100.0 * Patches.MeshRecyclerPatches.StatHits / recyclerAsked, recyclerAsked,
                Patches.MeshRecyclerPatches.HeldBytes / 1048576.0,
                Patches.MeshRecyclerPatches.StatMissBytes / 1048576.0,
                Patches.MeshRecyclerPatches.StatEvicted);

        if (Patches.TightClonePatches.Enabled && Patches.TightClonePatches.StatClones > 0)
            sb.AppendFormat(ci, "  klon-kompakt: {0:N0} clones, {1:N0} MB kapazitaets-kopien gespart\n",
                Patches.TightClonePatches.StatClones,
                Patches.TightClonePatches.StatBytesSaved / 1048576.0);
        // Printed whenever the pool is armed: hits at 0 with misses climbing means the return
        // path is not firing (the AddToPools postfix), which must not look like "no data yet".
        if (Patches.TightClonePatches.Enabled || Patches.TightClonePatches.StatClones > 0)
        {
            var hits = Patches.TightClonePatches.StatClones;
            var misses = Patches.TightClonePatches.StatExtrasMisses;
            var total = hits + misses;
            sb.AppendFormat(ci, "  extras-pool: {0:F0}% treffer ({1:N0} anfragen), {2:N0} MB vorgehalten, {3:N0} verworfen{4}\n",
                total > 0 ? 100.0 * hits / total : 0, total,
                Patches.TightClonePatches.PooledBytes / 1048576.0,
                Patches.TightClonePatches.StatExtrasDropped,
                Patches.TightClonePatches.Enabled ? "" : " (AUS)");
        }
        if (Patches.AnimatableCullPatches.Enabled || Patches.AnimatableCullPatches.StatCalls > 0)
            sb.AppendFormat(ci, "animatable-gate: {0:N0} von {1:N0} aufrufen uebersprungen{2}\n",
                Patches.AnimatableCullPatches.StatSkipped, Patches.AnimatableCullPatches.StatCalls,
                Patches.AnimatableCullPatches.Enabled ? "" : " (AUS)");
        if (Patches.MinimapPatches.Enabled || Patches.MinimapPatches.StatTicks > 0)
            sb.AppendFormat(ci, "minimap: kappe {0} kacheln/tick, {1:F2} ms je upload-tick ueber {2:N0} ticks, {3}{4}\n",
                Patches.MinimapPatches.Cap, Patches.MinimapPatches.AvgTickMs, Patches.MinimapPatches.StatTicks,
                Patches.MinimapPatches.DirectUpload
                    ? string.Format(ci, "direkt-upload ({0:N0} kacheln in {1:N0} komponenten)",
                        Patches.MinimapPatches.StatDirectPieces, Patches.MinimapPatches.StatDirectComponents)
                    : "FBO-pfad (vanilla)",
                Patches.MinimapPatches.Enabled ? "" : " (AUS)");
        // Printed whenever armed: "0 geladen, 0 offen" is correct idleness in a settled
        // scene, and must not be confused with a prefix that never ran.
        if (Patches.EntityLoadPatches.Enabled || Patches.EntityLoadPatches.StatLoaded > 0)
            sb.AppendFormat(ci, "entity-laden: {0:N0} geladen ({1:N0} vorgezogen, {2:N0} verworfen, {3:N0} updates auf gehaltene), "
                + "{4:N0} offen, {5:N0} frames verteilt, langsamste {6:F1} ms{7}{8}\n",
                Patches.EntityLoadPatches.StatLoaded, Patches.EntityLoadPatches.StatPromoted,
                Patches.EntityLoadPatches.StatDropped, Patches.EntityLoadPatches.StatUpdatedPending,
                Patches.EntityLoadPatches.PendingCount, Patches.EntityLoadPatches.StatDeferredFrames,
                Patches.EntityLoadPatches.StatWorstMs,
                Patches.EntityLoadPatches.StatWorstCode != null ? " (" + Patches.EntityLoadPatches.StatWorstCode + ")" : "",
                Patches.EntityLoadPatches.Enabled ? "" : " (AUS)");
        // Server half - in singleplayer the statics are shared with the integrated server, so
        // the counters are live here; on a remote server they stay at zero and say so.
        var posTotal = Patches.EntitySyncPatches.StatPositionsSent + Patches.EntitySyncPatches.StatPositionsSkipped;
        var attrTotal = Patches.EntitySyncPatches.StatAttrPathsSent + Patches.EntitySyncPatches.StatAttrPathsSkipped;
        if (capi != null && capi.IsSinglePlayer || posTotal + attrTotal > 0)
            sb.AppendFormat(ci, "entity-sync (server): positionen {0:N0} gesendet, {1:N0} gespart ({2:F0} %), "
                + "{3:N0} hysterese-halte, {4:N0}x cap-sortiert | attribute {5:N0} pfade gesendet, {6:N0} gespart ({7:F0} %), "
                + "{8:N0} pakete unterdrueckt{9}{10}\n",
                Patches.EntitySyncPatches.StatPositionsSent, Patches.EntitySyncPatches.StatPositionsSkipped,
                posTotal > 0 ? 100.0 * Patches.EntitySyncPatches.StatPositionsSkipped / posTotal : 0,
                Patches.EntitySyncPatches.StatHysteresisHolds, Patches.EntitySyncPatches.StatCapOrderings,
                Patches.EntitySyncPatches.StatAttrPathsSent, Patches.EntitySyncPatches.StatAttrPathsSkipped,
                attrTotal > 0 ? 100.0 * Patches.EntitySyncPatches.StatAttrPathsSkipped / attrTotal : 0,
                Patches.EntitySyncPatches.StatAttrPacketsSuppressed,
                Patches.EntitySyncPatches.DistanceSendRate ? "" : " (sync-tuning AUS)",
                Patches.EntitySyncPatches.AttributeNoOpSkip ? "" : " (attr-skip AUS)");

        sb.AppendFormat(ci, "vram: {0:N0} MB aus {1:N0} leeren pools zurueckgegeben, {2:N0} noch leer\n",
            PoolReclaimer.StatBytesReclaimed / 1048576.0, PoolReclaimer.StatPoolsReclaimed,
            PoolReclaimer.StatEmptyPools);

        sb.AppendFormat(ci, "zufluss-bremse: {0} % ({1} spalten je {2} ms, basis {3} je {4} ms), "
            + "{5:F0}s gebremst\n",
            InflowBrake.Enabled ? InflowBrake.CurrentPercent.ToString(ci) : "aus",
            InflowBrake.CurrentColumns, InflowBrake.CurrentTickMs,
            InflowBrake.BaseColumns, InflowBrake.BaseTickMs, InflowBrake.SecondsBraking);

        sb.Append("upload-pfad: ").Append(UploadPathDescription());
        return sb.ToString();
    }

    /// <summary>Which GL path chunk uploads take, and whether the bulk-copy patch matters on it.</summary>
    private static string UploadPathDescription()
    {
        var bulkCalls = Patches.MeshUploadPatches.StatBulkCalls;
        if (bulkCalls > 0) return $"persistent mapping, {bulkCalls:N0} bulk copies";
        if (Patches.MeshUploadPatches.StatFallbackCalls > 0) return "glBufferSubData, bulk-copy-patch wirkungslos";
        return "glBufferSubData; treiber "
            + (Patches.PersistentMappingPatch.Available ? "kann" : "kann kein")
            + " persistent mapping, flag " + (Patches.PersistentMappingPatch.Enabled ? "an" : "aus");
    }
}
