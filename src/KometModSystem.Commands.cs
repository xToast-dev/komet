using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Komet.Measure;

namespace Komet;

/// <summary>
/// The .komet chat command and the runtime toggles behind it. These exist because every
/// visual complaint used to cost a restart per guess; now "is it the mod?" (safemode) and
/// "which part?" (toggle) are answerable while the artefact is on screen.
/// </summary>
public partial class KometModSystem
{
    private bool safeMode;
    private int savedSunInterval = 1;
    private bool savedGlErrorSkip;
    private bool savedFirepitGate = true;
    private bool savedEntityTess = true;
    private bool savedEdgeCoalesce = true;
    private bool savedEdgePriority = true;
    private readonly Stopwatch uptime = Stopwatch.StartNew();

    /// <summary>What the mod ships with, so the stress phase can name the other side.</summary>
    private const int DefaultCellTarget = 32;

    /// <summary>
    /// Changes the grid's cell size and drops every cached grid, because the existing ones were
    /// built at the old size. Without the invalidation the setting would only take effect on
    /// pools that happened to change, and a stress phase would measure a mixture of both.
    /// </summary>
    private static void SetCellTarget(int target)
    {
        if (FastCuller.PartsPerCellTarget == target) return;
        FastCuller.PartsPerCellTarget = target;
        FastCuller.InvalidateAll();
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        api.ChatCommands.Create("komet")
            .WithDescription("Vintage Story performance patches: status and counters")
            .BeginSubCommand("hud")
                .WithDescription("Toggle the on-screen performance overlay (same as F7)")
                .HandleWith(_ =>
                {
                    hud.Visible = !hud.Visible;
                    return TextCommandResult.Success(hud.Visible ? "HUD an (F7 schaltet um)" : "HUD aus");
                })
            .EndSubCommand()
            .BeginSubCommand("stats")
                .WithDescription("Show what the culling patch has been doing since the last reset")
                .HandleWith(_ => TextCommandResult.Success(LoggedStats()))
            .EndSubCommand()
            .BeginSubCommand("hitch")
                .WithDescription("Ruckler-Protokoll: jeder Frame ueber der Schwelle mit Ursache und Kamerabewegung. 'reset' leert es")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args =>
                {
                    if (string.Equals(args[0] as string, "reset", System.StringComparison.OrdinalIgnoreCase))
                    {
                        HitchLog.Reset();
                        return TextCommandResult.Success("hitch-log geleert.");
                    }
                    string report = HitchLog.BuildReport();
                    Mod.Logger.Notification("hitch report:\n{0}", report);
                    return TextCommandResult.Success(report);
                })
            .EndSubCommand()
            .BeginSubCommand("report")
                .WithDescription("Alles auf einmal: umgebung, abweichende einstellungen, frame-aufteilung, ruckler-protokoll. Landet als ein Block im client-main.log")
                .HandleWith(_ =>
                {
                    string report = BuildFullReport();
                    // The log, not the chat: this is several hundred characters wide by design
                    // and the chat window wraps it into something nobody can copy back out.
                    Mod.Logger.Notification("full report:\n{0}", report);
                    return TextCommandResult.Success(
                        "report steht im client-main.log (zwischen '==== komet report ====' und '==== ende ===='). "
                        + "Kompletten Block kopieren.");
                })
            .EndSubCommand()
            .BeginSubCommand("toggle")
                .WithDescription("Ein einzelnes System an/aus: cull, occlusion, reclaim, sunquery, glerror, prebuild, firepit - zur Bisektion oder zum A/B-Messen")
                .WithArgs(api.ChatCommands.Parsers.Word("system"))
                .HandleWith(args => TextCommandResult.Success(ToggleSystem(args[0] as string)))
            .EndSubCommand()
            .BeginSubCommand("stress")
                .WithDescription("Automatische Messfahrt, drift-fest mit Baselines verschraenkt - Bewegung/Fliegen ist ok. Optional: Sekunden pro Scheibe (default 2) oder 'stop'")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args => TextCommandResult.Success(HandleStress(args[0] as string)))
            .EndSubCommand()
            .BeginSubCommand("retess")
                .WithDescription("Wer markiert Chunks dirty? Zaehler und gesampelte Quellen-Rangliste. 'reset' leert")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args =>
                {
                    if (string.Equals(args[0] as string, "reset", System.StringComparison.OrdinalIgnoreCase))
                    {
                        Patches.RetessSourcePatches.Reset();
                        return TextCommandResult.Success("dirty-mark-zaehler geleert.");
                    }
                    string report = Patches.RetessSourcePatches.BuildReport();
                    Mod.Logger.Notification("retess report:\n{0}", report);
                    return TextCommandResult.Success(report);
                })
            .EndSubCommand()
            .BeginSubCommand("safemode")
                .WithDescription("Alle darstellungsrelevanten Optimierungen sofort an/aus - trennt in Sekunden, ob ein Bildfehler von komet kommt")
                .HandleWith(_ => TextCommandResult.Success(ToggleSafeMode()))
            .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription("Reset the counters")
                .HandleWith(_ => { ResetStats(); return TextCommandResult.Success("komet counters reset."); })
            .EndSubCommand()
            .HandleWith(_ => TextCommandResult.Success(LoggedStats()));
    }

    private string HandleStress(string arg)
    {
        if (string.Equals(arg, "stop", System.StringComparison.OrdinalIgnoreCase))
            return StressTest.Stop("auf wunsch");
        if (safeMode)
            return "Safemode ist an - erst '.komet safemode' zuruecknehmen, dann testen.";

        double sliceSeconds = 2;
        if (arg != null && double.TryParse(arg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            sliceSeconds = parsed;

        return StressTest.Start(BuildStressPhases(), sliceSeconds, roundCount: 3, report =>
        {
            Mod.Logger.Notification("stresstest:\n{0}", report);
            capi?.ShowChatMessage(report);
        });
    }

    /// <summary>
    /// Every system with a runtime switch, each restoring exactly what komet.json
    /// configured. Baselines are not listed - the scheduler interleaves one before and
    /// after every test slice, which is what makes the deltas drift-proof.
    /// </summary>
    private System.Collections.Generic.List<StressTest.Phase> BuildStressPhases() => new()
    {
        new StressTest.Phase { Name = "sweep aus (vanilla)",
            Enter = () => FastCuller.Enabled = false,
            Exit = () => FastCuller.Enabled = config.FastFrustumCulling },
        new StressTest.Phase { Name = "occlusion aus (vanilla)",
            Enter = () => FastChunkCuller.Enabled = false,
            Exit = () => FastChunkCuller.Enabled = config.FastOcclusionCulling },
        // Default is vanilla's window build since 1.42.2, so the phase turns the pipeline ON.
        // It measured -0,05 +-0,10 here, i.e. nothing - the throughput it buys is chunk LOADING,
        // which a frame-time delta cannot see.
        new StressTest.Phase { Name = "fenster-pipe an",
            Enter = () => WindowPrebuilder.Enabled = !WindowPrebuilder.HardDisabled,
            // never resurrect a feature that disabled itself (validation limit, worker crash)
            Exit = () => WindowPrebuilder.Enabled = config.TesselationWindowPipelining && !WindowPrebuilder.HardDisabled },
        new StressTest.Phase { Name = "firepit-gate aus",
            Enter = () => Patches.FirepitPatches.Enabled = false,
            Exit = () => Patches.FirepitPatches.Enabled = true },
        new StressTest.Phase { Name = "glerror-skip an",
            Enter = () => Patches.GlErrorPatches.SkipEnabled = true,
            Exit = () => Patches.GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck },
        new StressTest.Phase { Name = "sonnen-query jeder frame",
            Enter = () => Patches.SunQueryPatches.Interval = 1,
            Exit = () => Patches.SunQueryPatches.Interval = config.SunOcclusionQueryInterval },
        new StressTest.Phase { Name = "entity-tess-budget aus",
            Enter = () => Patches.EntityTessPatches.Enabled = false,
            Exit = () => Patches.EntityTessPatches.Enabled = config.EntityTesselationBudgetMs > 0 },
        new StressTest.Phase { Name = "kanten-koalesz aus",
            Enter = () => { Patches.EdgeCoalescePatches.Enabled = false; Patches.EdgeCoalescePatches.FlushAll(); },
            Exit = () => Patches.EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0 },
        // The shadow group. Until 1.40.0 the plan had no phase for any of it, which is why
        // "safemode is faster" could not be attributed: the symmetric box is by far the largest
        // change this mod makes to what the GPU is asked to draw - it replaces vanilla's
        // 0.78 R x 0.44 R wedge with a 2R cube, and at ShadowDistanceMultiplier 1.5 that was
        // 765 blocks of ground on the same shadow map. Every one of these four is a different
        // way to give some of that back.
        // The sphere box is default-on again since 1.43.0 (far cascade only, after the user
        // photographed vanilla's hard shadow edge), so the phase turns it OFF and the delta
        // reads as its remaining cost. The 1.42.x both-cascades version measured +0,72 +-0,08;
        // far-only must come in under that - this phase is what checks it.
        new StressTest.Phase { Name = "schattenbox aus (vanilla-kegel)",
            Enter = () => Patches.ShadowPatches.SymmetricBox = false,
            Exit = () => Patches.ShadowPatches.SymmetricBox = config.SymmetricShadowBox },
        // Default-on since 1.43.0; the phase switches it off, so the delta reads as what the
        // throttle SAVES in this scene. While moving it saves nothing by design (movement
        // forces a redraw) - run the stress test standing still to see its real share.
        new StressTest.Phase { Name = "schatten-drossel aus (jeder frame)",
            Enter = () => Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1),
            Exit = () => Patches.ShadowThrottlePatches.SetIntervals(
                config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip) },
        // No phase for ShadowDistanceMultiplier any more: it has been 1.0 (vanilla) since
        // 1.40.0, so the phase set it to the value it already had and measured pure noise.
        // The grid's cell size, measured in the player's own scene rather than in the harness.
        //
        // The harness has now been wrong about this twice: it modelled 96 pools where the game
        // has 600, and it drew part positions uniformly at random while claiming tesselation
        // order - and this constant is exactly the one both of those decide. The benchmark
        // prefers 32 at the measured pool shape, but the benchmark is a model of the scene and
        // this phase is the scene. Interleaved against neighbour baselines like every other
        // phase, so the answer does not depend on which minute it was measured in.
        new StressTest.Phase { Name = "zellziel 160 statt " + DefaultCellTarget,
            Enter = () => SetCellTarget(160),
            Exit = () => SetCellTarget(config.PartsPerCellTarget) },
        // Default-on; the phase switches it OFF, so the delta reads as what bridging draw
        // ranges across frustum-clipped parts saves in this scene. The mechanism trades CPU
        // submission cost (fewer glMultiDrawElements ranges) for GPU vertex work on clipped,
        // pixel-identical geometry - measurable only where the frame is CPU-bound, which is
        // exactly what the 1.47/1.48 reports showed (gpu ~2,5 ms of ~13 ms).
        new StressTest.Phase { Name = "luecken-merge aus",
            Enter = () => FastCuller.GapMergeDrawRanges = false,
            Exit = () => FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges },
        // Default-on; the phase hands the recycler's storage back to vanilla, so the delta
        // reads as what the size-class pool saves. Its effect is GC pressure, not per-frame
        // CPU - expect it to show only in streaming scenes (fly over fresh terrain), and
        // read it together with the gc column of the hitch log.
        new StressTest.Phase { Name = "mesh-recycler aus (vanilla-ablage)",
            Enter = () => Patches.MeshRecyclerPatches.SetEnabled(false),
            Exit = () => Patches.MeshRecyclerPatches.SetEnabled(config.FastMeshRecycler) },
        new StressTest.Phase { Name = "lod3 raus aus schattenpass",
            Enter = () => FastCuller.ShadowSkipRedundantLod = true,
            Exit = () => FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod },
        // The diagnostics group. These do not draw anything and safemode does not switch them
        // off, so they were invisible to every previous stress run - and "safemode is faster"
        // was reported again after the drawing systems had all been cleared by measurement.
        // Instrumentation the mod carries is on the same side of the ledger as the work it
        // removes; these three phases are what makes that testable rather than argued.
        new StressTest.Phase { Name = "renderer-profiler an (diagnose)",
            Enter = () => { Patches.RendererProfiler.Enabled = true; WrapRenderers(); },
            Exit = () => { Patches.RendererProfiler.Enabled = config.ProfileRenderers;
                           if (config.ProfileRenderers) WrapRenderers(); else UnwrapRenderers(); } },
        new StressTest.Phase { Name = "retess-quellensampling an (diagnose)",
            Enter = () => Patches.RetessSourcePatches.SampleSources = true,
            Exit = () => Patches.RetessSourcePatches.SampleSources = config.SampleRetessSources },
        new StressTest.Phase { Name = "sweep-gegenprobe an (diagnose)",
            Enter = () => { CullVerifier.SampleEvery = 512; CullVerifier.Reset(); },
            Exit = () => CullVerifier.SampleEvery = config.VerifyCullSweepEvery },
        new StressTest.Phase { Name = "sweep-vektorkernel aus (skalar)",
            Enter = () => FastCuller.VectorCulling = false,
            Exit = () => FastCuller.VectorCulling = config.VectorCulling && FastCuller.VectorAvailable },
        new StressTest.Phase { Name = "alles vanilla (= safemode)",
            Enter = AllVanilla,
            Exit = AllConfigured },
    };

    /// <summary>
    /// Flips exactly one drawing-relevant system, so a visual artefact can be bisected while
    /// it is on screen. Every toggle logs the world's loading state alongside, because the
    /// strongest confounder so far has been time itself - artefacts reported during streaming
    /// were gone once the queue drained, whatever was toggled in between.
    /// </summary>
    private string ToggleSystem(string system)
    {
        string state;
        switch (system?.ToLowerInvariant())
        {
            case "cull":
                FastCuller.Enabled = !FastCuller.Enabled;
                state = "sichtbarkeits-sweep " + (FastCuller.Enabled ? "AN" : "AUS (vanilla)");
                break;
            case "occlusion":
                FastChunkCuller.Enabled = !FastChunkCuller.Enabled;
                state = "occlusion-culling " + (FastChunkCuller.Enabled ? "AN" : "AUS (vanilla)");
                break;
            case "reclaim":
                PoolReclaimer.Enabled = !PoolReclaimer.Enabled;
                state = "vram-reclaimer " + (PoolReclaimer.Enabled ? "AN" : "AUS");
                break;
            case "sunquery":
                Patches.SunQueryPatches.Interval = Patches.SunQueryPatches.Interval > 1 ? 1 : config.SunOcclusionQueryInterval;
                state = "sonnen-query-drossel " + (Patches.SunQueryPatches.Interval > 1 ? "AN" : "AUS (jeder frame)");
                break;
            case "firepit":
                Patches.FirepitPatches.Enabled = !Patches.FirepitPatches.Enabled;
                state = "firepit-gate " + (Patches.FirepitPatches.Enabled ? "AN" : "AUS (vanilla)");
                break;
            case "prebuild":
                WindowPrebuilder.Enabled = !WindowPrebuilder.Enabled;
                if (WindowPrebuilder.Enabled) WindowPrebuilder.HardDisabled = false; // explicit user intent overrides a self-disable
                state = "fenster-pipeline " + (WindowPrebuilder.Enabled ? "AN" : "AUS (vanilla-fensterbau)");
                break;
            case "glerror":
                Patches.GlErrorPatches.SkipEnabled = !Patches.GlErrorPatches.SkipEnabled;
                state = "glGetError-skip " + (Patches.GlErrorPatches.SkipEnabled
                    ? "AN (2 treiber-syncs/frame gespart, VRAM-warnung aus)"
                    : "AUS (vanilla)");
                break;
            case "enttess":
                Patches.EntityTessPatches.Enabled = !Patches.EntityTessPatches.Enabled;
                state = "entity-tesselation-budget " + (Patches.EntityTessPatches.Enabled ? "AN" : "AUS (vanilla)");
                break;
            case "shadowbox":
                Patches.ShadowPatches.SymmetricBox = !Patches.ShadowPatches.SymmetricBox;
                state = "symmetrische schattenbox " + (Patches.ShadowPatches.SymmetricBox
                    ? "AN (wuerfel um die kamera)" : "AUS (vanilla-kegel)");
                break;
            case "simd":
                if (!FastCuller.VectorAvailable) return "diese CPU hat kein AVX - der Sweep laeuft ohnehin skalar.";
                FastCuller.VectorCulling = !FastCuller.VectorCulling;
                state = "sweep-vektorkernel " + (FastCuller.VectorCulling
                    ? "AN (4 teile je befehl)" : "AUS (skalar, ein teil je befehl)");
                break;
            case "profiler":
                // Wrapping/unwrapping needs the event manager, which only exists in a world.
                Patches.RendererProfiler.Enabled = !Patches.RendererProfiler.Enabled;
                if (Patches.RendererProfiler.Enabled) WrapRenderers(); else UnwrapRenderers();
                state = "renderer-profiling " + (Patches.RendererProfiler.Enabled
                    ? "AN (" + Patches.RendererProfiler.StatWrapped + " renderer gewickelt - kostet frame-zeit)"
                    : "AUS (vanilla-dispatch)");
                break;
            case "retess":
                Patches.RetessSourcePatches.SampleSources = !Patches.RetessSourcePatches.SampleSources;
                state = "dirty-mark-quellensampling " + (Patches.RetessSourcePatches.SampleSources
                    ? "UNGEDECKELT (jede 8. markierung mit stack - '.komet retess' zeigt die rangliste)"
                    : "GEDECKELT (weiter aktiv, max. 25 captures/s)");
                break;
            case "cullcheck":
                CullVerifier.SampleEvery = CullVerifier.SampleEvery > 0 ? 0 : Math.Max(1, config.VerifyCullSweepEvery);
                CullVerifier.Reset();
                state = "sweep-gegenprobe " + (CullVerifier.SampleEvery > 0
                    ? "AN (jeder " + CullVerifier.SampleEvery + ". sweep gegen vanilla)" : "AUS");
                break;
            case "cellsize":
                SetCellTarget(FastCuller.PartsPerCellTarget == DefaultCellTarget ? 160 : DefaultCellTarget);
                state = "gitter-zellziel jetzt " + FastCuller.PartsPerCellTarget + " teile je zelle";
                break;
            case "gapmerge":
                FastCuller.GapMergeDrawRanges = !FastCuller.GapMergeDrawRanges;
                state = "luecken-merging " + (FastCuller.GapMergeDrawRanges
                    ? "AN (ranges ueberspannen frustum-geclippte teile)"
                    : "AUS (nur noch nahtlos benachbarte ranges)");
                break;
            case "recycler":
                Patches.MeshRecyclerPatches.SetEnabled(!Patches.MeshRecyclerPatches.Enabled);
                state = "mesh-recycler-pool " + (Patches.MeshRecyclerPatches.Enabled
                    ? "AN (groessenklassen, budget " + Patches.MeshRecyclerPatches.BudgetMb + " MB)"
                    : "AUS (vanilla-ablage, eigener vorrat freigegeben)");
                break;
            case "tightclone":
                Patches.TightClonePatches.Enabled = !Patches.TightClonePatches.Enabled;
                state = "klon-kompakt " + (Patches.TightClonePatches.Enabled
                    ? "AN (custom-parts werden inhaltsgross kopiert)"
                    : "AUS (vanilla: kapazitaetsgrosse kopien)");
                break;
            case "shadowlod":
                FastCuller.ShadowSkipRedundantLod = !FastCuller.ShadowSkipRedundantLod;
                state = "lod3-stellvertreter im schattenpass " + (FastCuller.ShadowSkipRedundantLod
                    ? "WEG (nur noch die detaillierte version)" : "DRIN (vanilla, beide versionen)");
                break;
            case "shadowstab":
                Patches.ShadowStabilityPatches.Enabled = !Patches.ShadowStabilityPatches.Enabled;
                state = "schatten-texel-snapping " + (Patches.ShadowStabilityPatches.Enabled
                    ? "AN" : "AUS (vanilla)")
                    + (Patches.ShadowStabilityPatches.StatSnaps == 0 ? " - patch nicht installiert, komet.json" : "");
                break;
            case "shadowthrottle":
                if (Patches.ShadowThrottlePatches.Throttling)
                {
                    Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1);
                    state = "schatten-drossel AUS (ferne kaskade jeden frame, vanilla)";
                }
                else
                {
                    // the config pair when it throttles, else the tested 2/4 - so the toggle
                    // works even on a config that has throttling off
                    int far = Math.Max(2, config.ShadowFarUpdateInterval);
                    int skip = Math.Max(4, config.ShadowFarMaxSkip);
                    Patches.ShadowThrottlePatches.SetIntervals(far, config.ShadowNearUpdateInterval, skip);
                    state = $"schatten-drossel AN (ferne kaskade alle {far}-{skip} frames, bewegung erzwingt sofort)";
                }
                break;
            case "shadowfade":
                Patches.ShadowPatches.FadeFix = !Patches.ShadowPatches.FadeFix;
                state = "schatten-fade-fix " + (Patches.ShadowPatches.FadeFix ? "AN" : "AUS (vanilla)");
                break;
            case "shadowdist":
                Patches.ShadowPatches.DistanceMultiplier =
                    Patches.ShadowPatches.DistanceMultiplier != 1.0 ? 1.0 : Patches.ShadowPatches.ConfiguredMultiplier;
                state = "schattenweite x" + Patches.ShadowPatches.DistanceMultiplier.ToString("0.##",
                    System.Globalization.CultureInfo.CurrentCulture)
                    + (Patches.ShadowPatches.DistanceMultiplier == 1.0 ? " (vanilla)" : "");
                break;
            case "edgecoal":
                if (Patches.EdgeCoalescePatches.Enabled)
                {
                    // never strand a held mark: everything pending goes out before vanilla takes over
                    Patches.EdgeCoalescePatches.Enabled = false;
                    Patches.EdgeCoalescePatches.FlushAll();
                    state = "kanten-koaleszenz AUS (vanilla, alles ausgegeben)";
                }
                else
                {
                    // the patch is always applied and runtime-gated, so the toggle can
                    // enable the experiment even with the config default of 0/off
                    Patches.EdgeCoalescePatches.Enabled = true;
                    state = "kanten-koaleszenz AN (experiment; default ist aus)";
                }
                break;
            case "edgeprio":
                if (Patches.EdgeRetessPriorityPatches.Enabled)
                {
                    Patches.EdgeRetessPriorityPatches.Enabled = false;
                    state = "edge-retess-prio AUS (vanilla-reihenfolge, sichtbare rand-reparaturen warten wieder)";
                }
                else
                {
                    Patches.EdgeRetessPriorityPatches.Enabled = true;
                    // explicit user intent overrides a self-disable
                    Patches.EdgeRetessPriorityPatches.HardDisabled = false;
                    state = "edge-retess-prio AN (sichtbare rand-reparaturen ueberholen die warteschlange)";
                }
                break;
            default:
                return "unbekannt. Systeme: cull, simd, gapmerge, occlusion, reclaim, recycler, sunquery, glerror, "
                     + "prebuild, firepit, enttess, edgecoal, edgeprio, profiler, retess, cullcheck, cellsize, "
                     + "shadowbox, shadowfade, shadowdist, shadowlod, shadowstab, shadowthrottle";
        }

        string world = $"chunks {Vintagestory.Client.RuntimeStats.chunksReceived:N0} empfangen, "
                     + $"warteschl. {Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation:N0}, "
                     + $"uptime {uptime.Elapsed.TotalSeconds:F0}s";
        Mod.Logger.Notification("toggle: {0} | weltzustand: {1}", state, world);
        return state + " | " + world;
    }

    /// <summary>
    /// Flips everything that changes WHAT is drawn, in one place, at runtime. Measurement
    /// patches stay on - they only observe - and so do the loading-pipeline patches, which
    /// affect when chunks arrive but never how they are drawn.
    /// </summary>
    private string ToggleSafeMode()
    {
        safeMode = !safeMode;
        if (safeMode)
        {
            if (StressTest.Running) StressTest.Stop("safemode uebernimmt");
            savedSunInterval = Patches.SunQueryPatches.Interval;
            savedGlErrorSkip = Patches.GlErrorPatches.SkipEnabled;
            savedFirepitGate = Patches.FirepitPatches.Enabled;
            savedEntityTess = Patches.EntityTessPatches.Enabled;
            savedEdgeCoalesce = Patches.EdgeCoalescePatches.Enabled;
            savedEdgePriority = Patches.EdgeRetessPriorityPatches.Enabled;
            AllVanilla();
            Mod.Logger.Notification("safemode AN | warteschl. {0:N0}, uptime {1:F0}s",
                Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
            return "SAFEMODE AN - komet zeichnet nichts mehr anders als vanilla. "
                 + "Bildfehler noch da? Dann ist es nicht diese Mod. '.komet safemode' schaltet zurueck.";
        }

        AllConfigured();
        // a live toggle the user made before entering safemode survives it; only what safemode
        // itself flipped comes back from config
        Patches.SunQueryPatches.Interval = savedSunInterval;
        Patches.GlErrorPatches.SkipEnabled = savedGlErrorSkip;
        Patches.FirepitPatches.Enabled = savedFirepitGate;
        Patches.EntityTessPatches.Enabled = savedEntityTess;
        Patches.EdgeCoalescePatches.Enabled = savedEdgeCoalesce;
        Patches.EdgeRetessPriorityPatches.Enabled =
            savedEdgePriority && !Patches.EdgeRetessPriorityPatches.HardDisabled;
        Mod.Logger.Notification("safemode AUS | warteschl. {0:N0}, uptime {1:F0}s",
            Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
        return "Safemode aus - Optimierungen laufen wieder gemaess komet.json.";
    }

    /// <summary>
    /// Everything that changes WHAT is drawn, handed back to vanilla. Shared by safemode and by
    /// the stress test's combined phase - the user reported the whole mod measuring slower than
    /// safemode, and a plan that can only flip systems one at a time cannot reproduce that
    /// observation, let alone check whether the parts add up to the whole.
    /// </summary>
    private void AllVanilla()
    {
        FastCuller.Enabled = false;                 // sweep, spatial index, batching, merging -> vanilla
        FastCuller.ShadowSkipRedundantLod = false;  // both LOD versions into the shadow map again
        FastChunkCuller.Enabled = false;            // occlusion walk -> vanilla
        PoolReclaimer.Enabled = false;              // stop reclaiming; already-empty pools stay empty
        Patches.SunQueryPatches.Interval = 1;       // sun occlusion query every frame again
        Patches.GlErrorPatches.SkipEnabled = false; // vanilla error detection back on
        Patches.FirepitPatches.Enabled = false;     // draw every firepit again
        Patches.EntityTessPatches.Enabled = false;  // tesselate entity shapes immediately again
        Patches.EdgeCoalescePatches.Enabled = false;
        Patches.EdgeCoalescePatches.FlushAll();     // held edge marks go out, nothing strands
        Patches.EdgeRetessPriorityPatches.Enabled = false; // vanilla queue order again
        // shadows too: box shape, fade range, distance and update cadence all back to
        // vanilla, so "is a shadow artefact ours?" is answerable with one command
        Patches.ShadowPatches.ToVanilla();
        Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1);
        Patches.ShadowStabilityPatches.Enabled = false;
    }

    /// <summary>The exact inverse: everything back to what komet.json asked for.</summary>
    private void AllConfigured()
    {
        FastCuller.Enabled = config.FastFrustumCulling;
        FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges;
        FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod;
        FastChunkCuller.Enabled = config.FastOcclusionCulling;
        PoolReclaimer.Enabled = config.ReclaimEmptyPools;
        Patches.SunQueryPatches.Interval = config.SunOcclusionQueryInterval;
        Patches.GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck;
        Patches.FirepitPatches.Enabled = true;
        Patches.EntityTessPatches.Enabled = config.EntityTesselationBudgetMs > 0;
        Patches.EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0;
        Patches.EdgeRetessPriorityPatches.Enabled =
            config.EdgeRetessPriority && !Patches.EdgeRetessPriorityPatches.HardDisabled;
        Patches.ShadowPatches.ToConfigured(config.SymmetricShadowBox, config.FixShadowFadeCutoff);
        Patches.ShadowThrottlePatches.SetIntervals(
            config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip);
        Patches.ShadowStabilityPatches.Enabled = config.StabiliseShadowTexels;
        SetCellTarget(config.PartsPerCellTarget);
    }

    private static void ResetStats()
    {
        FastCuller.StatSweeps = 0;
        FastCuller.StatPartsTested = 0;
        FastCuller.StatPoolsSkipped = 0;
        FastCuller.StatRebuilds = 0;
        FastCuller.StatRangesRaw = 0;
        FastCuller.StatRangesEmitted = 0;
        FastCuller.StatRangesBridged = 0;
        FastCuller.StatPartsBridged = 0;
        FastCuller.StatTrisBridged = 0;
        FastCuller.Workers.StatWaitTicks = 0;
        FastCuller.Workers.StatRuns = 0;
        FastChunkCuller.StatPasses = 0;
        FastChunkCuller.StatPeakMs = 0;
        Patches.MeshUploadPatches.StatBytesCopied = 0;
        Patches.MeshUploadPatches.StatBulkCalls = 0;
        Patches.MeshUploadPatches.StatFallbackCalls = 0;
        UploadBudget.Reset();
        Patches.MeshRecyclerPatches.ResetStats();
        Patches.TightClonePatches.ResetStats();
        Patches.EdgeRetessPriorityPatches.StatPromoted = 0;
        Patches.EdgeRetessPriorityPatches.StatSweeps = 0;
        Patches.EdgeRetessPriorityPatches.StatBusySkips = 0;
        PoolReclaimer.Reset();
        Patches.RendererProfiler.Reset();
        FrameStats.Reset();
        HitchLog.Reset();
    }
}
