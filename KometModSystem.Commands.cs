using System;
using System.Diagnostics;
using Komet.Culling;
using Komet.Guard;
using Komet.Measure;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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
            .WithDescription(Loc.T("komet:cmd-root", "Vintage Story performance patches: status and counters"))
            .BeginSubCommand("hud")
                .WithDescription(Loc.T("komet:cmd-hud", "Toggle the on-screen performance overlay (same as F7)"))
                .HandleWith(_ =>
                {
                    hud.Visible = !hud.Visible;
                    return TextCommandResult.Success(hud.Visible
                        ? Loc.T("komet:msg-hud-on", "HUD on (F7 toggles)")
                        : Loc.T("komet:msg-hud-off", "HUD off"));
                })
            .EndSubCommand()
            .BeginSubCommand("stats")
                .WithDescription(Loc.T("komet:cmd-stats", "Show what the culling patch has been doing since the last reset"))
                .HandleWith(_ => TextCommandResult.Success(LoggedStats()))
            .EndSubCommand()
            .BeginSubCommand("hitch")
                .WithDescription(Loc.T("komet:cmd-hitch", "Hitch log: every frame over the threshold with its cause and the camera movement. 'reset' clears it"))
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args =>
                {
                    if (string.Equals(args[0] as string, "reset", System.StringComparison.OrdinalIgnoreCase))
                    {
                        HitchLog.Reset();
                        return TextCommandResult.Success(Loc.T("komet:msg-hitch-cleared", "hitch log cleared."));
                    }
                    var report = HitchLog.BuildReport();
                    Mod.Logger.Notification("hitch report:\n{0}", report);
                    return TextCommandResult.Success(report);
                })
            .EndSubCommand()
            .BeginSubCommand("report")
                .WithDescription(Loc.T("komet:cmd-report", "Everything at once: environment, settings that differ from the default, frame breakdown, hitch log. Lands as one block in client-main.log"))
                .HandleWith(_ =>
                {
                    var report = BuildFullReport();
                    // The log, not the chat: this is several hundred characters wide by design
                    // and the chat window wraps it into something nobody can copy back out.
                    Mod.Logger.Notification("full report:\n{0}", report);
                    return TextCommandResult.Success(
                        Loc.T("komet:msg-report-written",
                            "the report is in client-main.log (between '==== komet report ====' and "
                            + "'==== end ===='). Copy the whole block."));
                })
            .EndSubCommand()
            .BeginSubCommand("toggle")
                .WithDescription(Loc.T("komet:cmd-toggle", "Turn a single system on or off: cull, occlusion, reclaim, sunquery, glerror, prebuild, firepit, entload, entsync ... - for bisecting or A/B measuring"))
                .WithArgs(api.ChatCommands.Parsers.Word("system"))
                .HandleWith(args => TextCommandResult.Success(ToggleSystem(args[0] as string)))
            .EndSubCommand()
            .BeginSubCommand("shadownear")
                .WithDescription(Loc.T("komet:cmd-shadownear", "Size of the near shadow cascade's map in pixels (e.g. 4096), 'off' for the engine's; rebuilds the framebuffers live. No argument: show both maps"))
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args => TextCommandResult.Success(HandleShadowNear(args[0] as string)))
            .EndSubCommand()
            .BeginSubCommand("stress")
                .WithDescription(Loc.T("komet:cmd-stress", "Automatic measurement run, drift-proof by interleaving baselines - moving or flying is fine. Optional: seconds per slice (default 2), or 'stop'"))
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args => TextCommandResult.Success(HandleStress(args[0] as string)))
            .EndSubCommand()
            .BeginSubCommand("retess")
                .WithDescription(Loc.T("komet:cmd-retess", "Who marks chunks dirty? Counters and a sampled ranking of the sources. 'reset' clears it"))
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args =>
                {
                    if (string.Equals(args[0] as string, "reset", System.StringComparison.OrdinalIgnoreCase))
                    {
                        Patches.RetessSourcePatches.Reset();
                        return TextCommandResult.Success(Loc.T("komet:msg-retess-cleared", "dirty mark counters cleared."));
                    }
                    var report = Patches.RetessSourcePatches.BuildReport();
                    Mod.Logger.Notification("retess report:\n{0}", report);
                    return TextCommandResult.Success(report);
                })
            .EndSubCommand()
            .BeginSubCommand("conflicts")
                .WithDescription(Loc.T("komet:cmd-conflicts", "Who patches komet's methods or komet's own code, and does the engine differ from the verified build? Rescans immediately"))
                .HandleWith(_ =>
                {
                    if (!PatchGuard.EngineChecked)
                        PatchGuard.CheckEngine(Vintagestory.API.Config.GameVersion.LongGameVersion);
                    PatchGuard.Scan();
                    var text = PatchGuard.ReportLines();
                    Mod.Logger.Notification("patch guard:\n{0}", text);
                    return TextCommandResult.Success(text);
                })
            .EndSubCommand()
            .BeginSubCommand("mods")
                .WithDescription(Loc.T("komet:cmd-mods", "What the other mods cost per frame and at load, and what they do (patches, registered classes). 'hud' cycles the overlay (same as Shift+F7), 'reset' clears the per-frame figures"))
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("arg"))
                .HandleWith(args =>
                {
                    var arg = args[0] as string;
                    if (string.Equals(arg, "hud", StringComparison.OrdinalIgnoreCase))
                        return TextCommandResult.Success(CycleModHud());
                    if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
                    {
                        ModProfiler.Reset();
                        return TextCommandResult.Success(Loc.T("komet:msg-mods-reset", "per-mod counters cleared."));
                    }
                    if (!ModProfiler.Enabled)
                        return TextCommandResult.Success(Loc.T("komet:msg-mods-off",
                            "mod profiling is off (ProfileMods in komet.json)."));

                    // A report is only worth reading with a fresh inventory behind it.
                    ScanMods();
                    var sb = new System.Text.StringBuilder(1200);
                    ModProfiler.Write(sb, System.Globalization.CultureInfo.CurrentCulture, FrameStats.AvgFrameMs,
                        Patches.RendererProfiler.Enabled, 10);
                    var text = sb.ToString().TrimEnd('\n');
                    Mod.Logger.Notification("mod profile:\n{0}", text);
                    return TextCommandResult.Success(text);
                })
            .EndSubCommand()
            .BeginSubCommand("safemode")
                .WithDescription(Loc.T("komet:cmd-safemode", "Every optimisation that changes what is drawn, on or off at once - settles in seconds whether a visual glitch comes from komet"))
                .HandleWith(_ => TextCommandResult.Success(ToggleSafeMode()))
            .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription(Loc.T("komet:cmd-reset", "Reset the counters"))
                .HandleWith(_ => { ResetStats(); return TextCommandResult.Success(Loc.T("komet:msg-counters-reset", "komet counters reset.")); })
            .EndSubCommand()
            .HandleWith(_ => TextCommandResult.Success(LoggedStats()));
    }

    /// <summary>
    /// '.komet shadownear [px|off]': the near cascade's map size, live. A resize rebuilds
    /// every framebuffer (what the graphics menu does on a change), so this is one hitch per
    /// call and then the new size - the way to compare 7168 against 4096 against 3072 on the
    /// GPU stage line within a minute instead of a restart per candidate.
    /// </summary>
    private string HandleShadowNear(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return Loc.T("komet:msg-shadow-maps", "shadow maps: far {0}px, near {1}px ({2}px configured, 0 = as far)",
                Patches.ShadowResPatches.EffectiveMapSize, Patches.ShadowResPatches.EffectiveNearMapSize,
                Patches.ShadowResPatches.NearMapSize);

        int size;
        if (string.Equals(arg, "off", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "far", System.StringComparison.OrdinalIgnoreCase))
            size = 0;
        else if (!int.TryParse(arg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out size)
                 || size < Patches.ShadowResPatches.NearMapMin || size > Patches.ShadowResPatches.NearMapMax)
            return Loc.T("komet:msg-shadownear-arg", "give a size in pixels (512-16384) or 'off'");

        var platform = capi?.World is Vintagestory.Client.NoObf.ClientMain game
            ? game.Platform as Vintagestory.Client.NoObf.ClientPlatformWindows
            : null;
        var result = Patches.ShadowResPatches.TryResizeNear(platform, size);
        Mod.Logger.Notification("shadownear {0}: {1}", arg, result);
        return result;
    }

    private string HandleStress(string arg)
    {
        if (string.Equals(arg, "stop", System.StringComparison.OrdinalIgnoreCase))
            return StressTest.Stop("on request");
        if (safeMode)
            return Loc.T("komet:msg-safemode-blocks", "Safemode is on - take it back with '.komet safemode' first, then test.");

        double sliceSeconds = 2;
        if (arg != null && double.TryParse(arg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
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
        new StressTest.Phase { Name = "sweep off (vanilla)",
            Enter = () => FastCuller.Enabled = false,
            Exit = () => FastCuller.Enabled = config.FastFrustumCulling },
        new StressTest.Phase { Name = "occlusion off (vanilla)",
            Enter = () => FastChunkCuller.Enabled = false,
            Exit = () => FastChunkCuller.Enabled = config.FastOcclusionCulling },
        // Default is vanilla's window build since 1.42.2, so the phase turns the pipeline ON.
        // It measured -0,05 +-0,10 here, i.e. nothing - the throughput it buys is chunk LOADING,
        // which a frame-time delta cannot see.
        new StressTest.Phase { Name = "window pipe on",
            Enter = () => WindowPrebuilder.Enabled = !WindowPrebuilder.HardDisabled,
            // never resurrect a feature that disabled itself (validation limit, worker crash)
            Exit = () => WindowPrebuilder.Enabled = config.TesselationWindowPipelining && !WindowPrebuilder.HardDisabled },
        new StressTest.Phase { Name = "firepit gate off",
            Enter = () => Patches.FirepitPatches.Enabled = false,
            Exit = () => Patches.FirepitPatches.Enabled = true },
        new StressTest.Phase { Name = "glerror skip on",
            Enter = () => Patches.GlErrorPatches.SkipEnabled = true,
            Exit = () => Patches.GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck },
        new StressTest.Phase { Name = "sun query every frame",
            Enter = () => Patches.SunQueryPatches.Interval = 1,
            Exit = () => Patches.SunQueryPatches.Interval = config.SunOcclusionQueryInterval },
        new StressTest.Phase { Name = "entity tess budget off",
            Enter = () => Patches.EntityTessPatches.Enabled = false,
            Exit = () => Patches.EntityTessPatches.Enabled = config.EntityTesselationBudgetMs > 0 },
        // Off = everything held finishes at once and every packet goes straight to vanilla.
        // Only streaming scenes (join flood, flying) have entity loads to measure.
        // Off = vanilla's 200 pieces per tick; only measurable while the minimap fills.
        new StressTest.Phase { Name = "minimap budget off (200/tick)",
            Enter = () => Patches.MinimapPatches.Enabled = false,
            Exit = () => Patches.MinimapPatches.Enabled = config.MinimapPieceBudgetMs > 0 },
        new StressTest.Phase { Name = "minimap direct upload off (FBO)",
            Enter = () => Patches.MinimapPatches.DirectUpload = false,
            Exit = () => Patches.MinimapPatches.DirectUpload = config.MinimapDirectUpload },
        // Off = vanilla's whole-queue drain; only a streaming scene has bursts to cut.
        new StressTest.Phase { Name = "task budget off",
            Enter = () => Patches.MainThreadTaskPatches.BudgetMs = 0,
            Exit = () => Patches.MainThreadTaskPatches.BudgetMs = Math.Max(0, config.MainThreadTaskBudgetMs) },
        // Off = every entity animates every frame, as vanilla; measurable wherever many
        // entities are loaded (a farm, a join flood).
        new StressTest.Phase { Name = "anim lod off",
            Enter = () => Patches.EntityAnimPatches.LodEnabled = false,
            Exit = () => Patches.EntityAnimPatches.LodEnabled = config.EntityAnimationLod },
        new StressTest.Phase { Name = "entity load budget off",
            Enter = () => { Patches.EntityLoadPatches.Enabled = false; Patches.EntityLoadPatches.FlushAll(); },
            Exit = () => Patches.EntityLoadPatches.Enabled = config.EntityLoadBudgetMs > 0 },
        // Server side, singleplayer only: fewer position/attribute packets means less for the
        // integrated server to build and the shared GC to collect - a GC-column effect, like
        // the recycler, not a per-frame CPU one.
        new StressTest.Phase { Name = "entity sync tuning off (server)",
            Enter = () => { Patches.EntitySyncPatches.DistanceSendRate = false; Patches.EntitySyncPatches.TrackingHysteresis = false; },
            Exit = () => { Patches.EntitySyncPatches.DistanceSendRate = config.ServerEntitySyncTuning;
                           Patches.EntitySyncPatches.TrackingHysteresis = config.ServerEntitySyncTuning; } },
        new StressTest.Phase { Name = "attribute no-op skip off (server)",
            Enter = () => Patches.EntitySyncPatches.AttributeNoOpSkip = false,
            Exit = () => Patches.EntitySyncPatches.AttributeNoOpSkip = config.ServerAttributeNoOpSkip },
        new StressTest.Phase { Name = "edge coalescing off",
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
        new StressTest.Phase { Name = "shadow box off (vanilla wedge)",
            Enter = () => Patches.ShadowPatches.SymmetricBox = false,
            Exit = () => Patches.ShadowPatches.SymmetricBox = config.SymmetricShadowBox },
        // The coverage margin is what makes the throttle work while MOVING, so this phase is
        // the one to read during a flight: without it the far cascade is redrawn on almost
        // every frame, with it at the staleness cap. Standing still it costs a few texels of
        // density and saves nothing - the throttle was already skipping.
        new StressTest.Phase { Name = "far shadow coverage margin off (redraw on every step)",
            Enter = () => { Patches.ShadowPatches.FarBoxMargin = 0; Patches.ShadowThrottlePatches.Invalidate(); },
            Exit = () => { Patches.ShadowPatches.FarBoxMargin = config.ShadowFarBoxMargin;
                           Patches.ShadowThrottlePatches.Invalidate(); } },
        // Default-on since 1.43.0; the phase switches it off, so the delta reads as what the
        // throttle SAVES in this scene. It used to save nothing while moving - the movement
        // rule forced a redraw almost every frame - which is what the coverage margin above
        // fixed; with the margin on, this phase reads the same standing still and flying.
        new StressTest.Phase { Name = "shadow throttle off (every frame)",
            Enter = () => Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1),
            Exit = () => Patches.ShadowThrottlePatches.SetIntervals(
                config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip) },
        // Default-on since 05.09.; the phase draws every face again, so the delta is what the
        // culled back faces cost in this scene. GPU work - visible only in a GPU-bound frame,
        // which the report's "gpu" figure against the frame time tells apart.
        new StressTest.Phase { Name = "shadow backface cull off (every face)",
            Enter = () => Patches.ShadowCullPatches.Enabled = false,
            Exit = () => Patches.ShadowCullPatches.Enabled = config.ShadowCullBackfaces },
        new StressTest.Phase { Name = "shadow depth-only shader off (alpha test everywhere)",
            Enter = () => Patches.ShadowCullPatches.DepthOnly = false,
            Exit = () => Patches.ShadowCullPatches.DepthOnly = config.ShadowDepthOnlySolidPasses },
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
        new StressTest.Phase { Name = "cell target 160 instead of " + DefaultCellTarget,
            Enter = () => SetCellTarget(160),
            Exit = () => SetCellTarget(config.PartsPerCellTarget) },
        // Default-on; the phase switches it OFF, so the delta reads as what bridging draw
        // ranges across frustum-clipped parts saves in this scene. The mechanism trades CPU
        // submission cost (fewer glMultiDrawElements ranges) for GPU vertex work on clipped,
        // pixel-identical geometry - measurable only where the frame is CPU-bound, which is
        // exactly what the 1.47/1.48 reports showed (gpu ~2,5 ms of ~13 ms).
        new StressTest.Phase { Name = "gap merge off",
            Enter = () => FastCuller.GapMergeDrawRanges = false,
            Exit = () => FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges },
        // Default-on; the phase hands the recycler's storage back to vanilla, so the delta
        // reads as what the size-class pool saves. Its effect is GC pressure, not per-frame
        // CPU - expect it to show only in streaming scenes (fly over fresh terrain), and
        // read it together with the gc column of the hitch log.
        new StressTest.Phase { Name = "mesh recycler off (vanilla store)",
            Enter = () => Patches.MeshRecyclerPatches.SetEnabled(false),
            Exit = () => Patches.MeshRecyclerPatches.SetEnabled(config.FastMeshRecycler) },
        new StressTest.Phase { Name = "extras pool off (fresh arrays)",
            Enter = () => { Patches.TightClonePatches.PoolExtras = false; Patches.TightClonePatches.ClearPools(); },
            Exit = () => Patches.TightClonePatches.PoolExtras = config.PoolMeshExtras },
        new StressTest.Phase { Name = "animatable gate off (vanilla)",
            Enter = () => Patches.AnimatableCullPatches.Enabled = false,
            Exit = () => Patches.AnimatableCullPatches.Enabled = config.CullAnimatableRenderers },
        new StressTest.Phase { Name = "lod3 out of the shadow pass",
            Enter = () => FastCuller.ShadowSkipRedundantLod = true,
            Exit = () => FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod },
        // The diagnostics group. These do not draw anything and safemode does not switch them
        // off, so they were invisible to every previous stress run - and "safemode is faster"
        // was reported again after the drawing systems had all been cleared by measurement.
        // Instrumentation the mod carries is on the same side of the ledger as the work it
        // removes; these three phases are what makes that testable rather than argued.
        new StressTest.Phase { Name = "renderer profiler on (diagnostic)",
            Enter = () => { Patches.RendererProfiler.Enabled = true; WrapRenderers(); },
            Exit = () => { Patches.RendererProfiler.Enabled = config.ProfileRenderers;
                           if (config.ProfileRenderers) WrapRenderers(); else UnwrapRenderers(); } },
        new StressTest.Phase { Name = "retess source sampling on (diagnostic)",
            Enter = () => Patches.RetessSourcePatches.SampleSources = true,
            Exit = () => Patches.RetessSourcePatches.SampleSources = config.SampleRetessSources },
        new StressTest.Phase { Name = "sweep cross-check on (diagnostic)",
            Enter = () => { CullVerifier.SampleEvery = 512; CullVerifier.Reset(); },
            Exit = () => CullVerifier.SampleEvery = config.VerifyCullSweepEvery },
        // The two always-on attributions, priced like the before-stage attribution: a few
        // Stopwatch reads per frame, but measured rather than assumed.
        new StressTest.Phase { Name = "task attribution off (vanilla drain)",
            Enter = () => Patches.MainThreadTaskPatches.Enabled = false,
            Exit = () => Patches.MainThreadTaskPatches.Enabled = config.AttributeMainThreadTasks },
        new StressTest.Phase { Name = "tick profiler off",
            Enter = () => { Patches.TickProfiler.Enabled = false; WrapTickListeners(); },
            Exit = () => { Patches.TickProfiler.Enabled = config.ProfileTickListeners; WrapTickListeners(); } },
        new StressTest.Phase { Name = "sweep vector kernel off (scalar)",
            Enter = () => FastCuller.VectorCulling = false,
            Exit = () => FastCuller.VectorCulling = config.VectorCulling && FastCuller.VectorAvailable },
        new StressTest.Phase { Name = "everything vanilla (= safemode)",
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
                state = "visibility sweep " + (FastCuller.Enabled ? "ON" : "OFF (vanilla)");
                break;
            case "occlusion":
                FastChunkCuller.Enabled = !FastChunkCuller.Enabled;
                state = "occlusion culling " + (FastChunkCuller.Enabled ? "ON" : "OFF (vanilla)");
                break;
            case "reclaim":
                PoolReclaimer.Enabled = !PoolReclaimer.Enabled;
                state = "vram reclaimer " + (PoolReclaimer.Enabled ? "ON" : "OFF");
                break;
            case "sunquery":
                Patches.SunQueryPatches.Interval = Patches.SunQueryPatches.Interval > 1 ? 1 : config.SunOcclusionQueryInterval;
                state = "sun query throttle " + (Patches.SunQueryPatches.Interval > 1 ? "ON" : "OFF (every frame)");
                break;
            case "firepit":
                Patches.FirepitPatches.Enabled = !Patches.FirepitPatches.Enabled;
                state = "firepit gate " + (Patches.FirepitPatches.Enabled ? "ON" : "OFF (vanilla)");
                break;
            case "prebuild":
                WindowPrebuilder.Enabled = !WindowPrebuilder.Enabled;
                if (WindowPrebuilder.Enabled) WindowPrebuilder.HardDisabled = false; // explicit user intent overrides a self-disable
                state = "window pipeline " + (WindowPrebuilder.Enabled ? "ON" : "OFF (vanilla window build)");
                break;
            case "glerror":
                Patches.GlErrorPatches.SkipEnabled = !Patches.GlErrorPatches.SkipEnabled;
                state = "glGetError skip " + (Patches.GlErrorPatches.SkipEnabled
                    ? "ON (2 driver syncs/frame saved, VRAM warning off)"
                    : "OFF (vanilla)");
                break;
            case "enttess":
                Patches.EntityTessPatches.Enabled = !Patches.EntityTessPatches.Enabled;
                state = "entity tesselation budget " + (Patches.EntityTessPatches.Enabled ? "ON" : "OFF (vanilla)");
                break;
            case "shadowmargin":
                // Off means the retained far map covers only what the fade needs, and the
                // throttle is back to redrawing on the first step anybody takes.
                Patches.ShadowPatches.FarBoxMargin =
                    Patches.ShadowPatches.FarBoxMargin > 0 ? 0.0 : Math.Max(0.0, config.ShadowFarBoxMargin);
                Patches.ShadowThrottlePatches.Invalidate();
                state = "far shadow coverage margin " + (Patches.ShadowPatches.EffectiveFarBoxMargin > 0
                    ? "ON (" + Patches.ShadowPatches.EffectiveFarBoxMargin.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                      + " blocks, redraw after "
                      + Patches.ShadowThrottlePatches.MoveLimit.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                      + " blocks of camera movement)"
                    : "OFF (redraw after " + Patches.ShadowThrottlePatches.MoveLimit.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
                      + " blocks - i.e. on nearly every frame while moving)"
                      + (Patches.ShadowPatches.SymmetricBox ? "" : "; needs the symmetric box"));
                break;
            case "shadowbox":
                Patches.ShadowPatches.SymmetricBox = !Patches.ShadowPatches.SymmetricBox;
                state = "symmetric shadow box " + (Patches.ShadowPatches.SymmetricBox
                    ? "ON (cube around the camera)" : "OFF (vanilla wedge)");
                break;
            case "simd":
                if (!FastCuller.VectorAvailable) return Loc.T("komet:msg-no-avx", "this CPU has no AVX - the sweep runs scalar anyway.");
                FastCuller.VectorCulling = !FastCuller.VectorCulling;
                state = "sweep vector kernel " + (FastCuller.VectorCulling
                    ? "ON (4 parts per instruction)" : "OFF (scalar, one part per instruction)");
                break;
            case "profiler":
                // Wrapping/unwrapping needs the event manager, which only exists in a world.
                Patches.RendererProfiler.Enabled = !Patches.RendererProfiler.Enabled;
                if (Patches.RendererProfiler.Enabled) WrapRenderers(); else UnwrapRenderers();
                state = "renderer profiling " + (Patches.RendererProfiler.Enabled
                    ? "ON (" + Patches.RendererProfiler.StatWrapped + " renderers wrapped - costs frame time)"
                    : "OFF (vanilla dispatch)");
                break;
            case "prioupload":
                Patches.PrioUploadPatches.Enabled = !Patches.PrioUploadPatches.Enabled;
                state = "prio upload budget " + (Patches.PrioUploadPatches.Enabled
                    ? "ON (bursts spread over several frames)"
                    : "OFF (vanilla: the whole prio queue in one frame)");
                break;
            case "beforeattr":
                Patches.RendererProfiler.AttributeBeforeStage = !Patches.RendererProfiler.AttributeBeforeStage;
                if (Patches.RendererProfiler.AttributeBeforeStage) WrapRenderers();
                else if (!Patches.RendererProfiler.Enabled) UnwrapRenderers();
                state = "before stage attribution " + (Patches.RendererProfiler.AttributeBeforeStage
                    ? "ON (hitch lines can name the before renderer)"
                    : "OFF (vanilla dispatch in the before stage)");
                break;
            case "uploaddruck":
                UploadBudget.FramePressureInput = !UploadBudget.FramePressureInput;
                state = "upload frame pressure " + (UploadBudget.FramePressureInput
                    ? "ON (hot frames with uploads in flight push the budget down)"
                    : "OFF (the throttle only sees the upload clock, as before 01.09.)");
                break;
            case "hudraster":
                DebugHud.BackgroundRaster = !DebugHud.BackgroundRaster;
                state = "hud raster " + (DebugHud.BackgroundRaster
                    ? "IN THE WORKER (the frame only pays sampling + upload)"
                    : "SYNCHRONOUS (full rebuild inside the frame, like vanilla overlays)");
                break;
            case "retess":
                Patches.RetessSourcePatches.SampleSources = !Patches.RetessSourcePatches.SampleSources;
                state = "dirty mark source sampling " + (Patches.RetessSourcePatches.SampleSources
                    ? "UNCAPPED (every 8th mark with a stack - '.komet retess' shows the ranking)"
                    : "CAPPED (still active, at most 25 captures/s)");
                break;
            case "cullcheck":
                CullVerifier.SampleEvery = CullVerifier.SampleEvery > 0 ? 0 : Math.Max(1, config.VerifyCullSweepEvery);
                CullVerifier.Reset();
                state = "sweep cross-check " + (CullVerifier.SampleEvery > 0
                    ? "ON (every " + CullVerifier.SampleEvery + "th sweep against vanilla)" : "OFF");
                break;
            case "cellsize":
                SetCellTarget(FastCuller.PartsPerCellTarget == DefaultCellTarget ? 160 : DefaultCellTarget);
                state = "grid cell target now " + FastCuller.PartsPerCellTarget + " parts per cell";
                break;
            case "gapmerge":
                FastCuller.GapMergeDrawRanges = !FastCuller.GapMergeDrawRanges;
                state = "gap merging " + (FastCuller.GapMergeDrawRanges
                    ? "ON (ranges span frustum-clipped parts)"
                    : "OFF (only seamlessly adjacent ranges)");
                break;
            case "recycler":
                Patches.MeshRecyclerPatches.SetEnabled(!Patches.MeshRecyclerPatches.Enabled);
                state = "mesh recycler pool " + (Patches.MeshRecyclerPatches.Enabled
                    ? "ON (size classes, budget " + Patches.MeshRecyclerPatches.BudgetMb + " MB)"
                    : "OFF (vanilla store, own stock released)");
                break;
            case "tightclone":
                Patches.TightClonePatches.Enabled = !Patches.TightClonePatches.Enabled;
                state = "tight clone " + (Patches.TightClonePatches.Enabled
                    ? "ON (custom parts are copied at content size)"
                    : "OFF (vanilla: capacity-sized copies)");
                break;
            case "extrapool":
                Patches.TightClonePatches.PoolExtras = !Patches.TightClonePatches.PoolExtras;
                if (!Patches.TightClonePatches.PoolExtras) Patches.TightClonePatches.ClearPools();
                state = "extras pool " + (Patches.TightClonePatches.PoolExtras
                    ? "ON (per-face and custom arrays of the chunk parts are recycled)"
                    : "OFF (vanilla: fresh arrays per part, stock released)");
                break;
            case "animcull":
                Patches.AnimatableCullPatches.Enabled = !Patches.AnimatableCullPatches.Enabled;
                state = "animatable frustum gate " + (Patches.AnimatableCullPatches.Enabled
                    ? "ON (animated block entities outside the frustum are skipped)"
                    : "OFF (vanilla: every instance draws in every stage)");
                break;
            case "shadowlod":
                FastCuller.ShadowSkipRedundantLod = !FastCuller.ShadowSkipRedundantLod;
                state = "lod3 stand-ins in the shadow pass " + (FastCuller.ShadowSkipRedundantLod
                    ? "GONE (only the detailed version left)" : "IN (vanilla, both versions)");
                break;
            case "shadowcull":
                Patches.ShadowCullPatches.Enabled = !Patches.ShadowCullPatches.Enabled;
                state = "shadow pass back-face culling " + (Patches.ShadowCullPatches.Enabled
                    ? "ON (solid passes draw front faces only into the shadow maps)"
                    : "OFF (vanilla: every face of every pass)");
                break;
            case "shadowdepth":
                Patches.ShadowCullPatches.DepthOnly = !Patches.ShadowCullPatches.DepthOnly;
                state = "shadow pass depth-only shader for the solid passes " + (Patches.ShadowCullPatches.DepthOnly
                    ? "ON" + (Patches.ShadowCullPatches.DepthOnlyState != null ? " (" + Patches.ShadowCullPatches.DepthOnlyState + ")" : "")
                    : "OFF (vanilla: chunkshadowmap with alpha test for every pass)");
                break;
            case "animwarm":
                Runtime.AnimationWarmup.Enabled = !Runtime.AnimationWarmup.Enabled && Patches.EntityLoadPatches.Enabled;
                state = "animation frame warm-up " + (Runtime.AnimationWarmup.Enabled
                    ? "ON (a worker generates a new shape's frames while its first entity is held)"
                    : Patches.EntityLoadPatches.Enabled ? "OFF (vanilla: generated on the main thread when an animation first plays)"
                                                        : "OFF - needs the entity load hold ('.komet toggle entload')");
                break;
            case "shadowstab":
                Patches.ShadowStabilityPatches.Enabled = !Patches.ShadowStabilityPatches.Enabled;
                state = "shadow texel snapping " + (Patches.ShadowStabilityPatches.Enabled
                    ? "ON" : "OFF (vanilla)")
                    + (Patches.ShadowStabilityPatches.StatSnaps == 0 ? " - patch not installed, komet.json" : "");
                break;
            case "shadowthrottle":
                if (Patches.ShadowThrottlePatches.Throttling)
                {
                    Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1);
                    state = "shadow throttle OFF (far cascade every frame, vanilla)";
                }
                else
                {
                    // the config pair when it throttles, else the tested 2/4 - so the toggle
                    // works even on a config that has throttling off
                    var far = Math.Max(2, config.ShadowFarUpdateInterval);
                    var skip = Math.Max(4, config.ShadowFarMaxSkip);
                    Patches.ShadowThrottlePatches.SetIntervals(far, config.ShadowNearUpdateInterval, skip);
                    state = $"shadow throttle ON (far cascade every {far}-{skip} frames, movement forces it immediately)";
                }
                break;
            case "shadowfade":
                Patches.ShadowPatches.FadeFix = !Patches.ShadowPatches.FadeFix;
                state = "shadow fade fix " + (Patches.ShadowPatches.FadeFix ? "ON" : "OFF (vanilla)");
                break;
            case "shadowdist":
                Patches.ShadowPatches.DistanceMultiplier =
                    Patches.ShadowPatches.DistanceMultiplier != 1.0 ? 1.0 : Patches.ShadowPatches.ConfiguredMultiplier;
                state = "shadow distance x" + Patches.ShadowPatches.DistanceMultiplier.ToString("0.##",
                    System.Globalization.CultureInfo.CurrentCulture)
                    + (Patches.ShadowPatches.DistanceMultiplier == 1.0 ? " (vanilla)" : "");
                break;
            case "edgecoal":
                if (Patches.EdgeCoalescePatches.Enabled)
                {
                    // never strand a held mark: everything pending goes out before vanilla takes over
                    Patches.EdgeCoalescePatches.Enabled = false;
                    Patches.EdgeCoalescePatches.FlushAll();
                    state = "edge coalescing OFF (vanilla, everything flushed)";
                }
                else
                {
                    // the patch is always applied and runtime-gated, so the toggle can
                    // enable the experiment even with the config default of 0/off
                    Patches.EdgeCoalescePatches.Enabled = true;
                    state = "edge coalescing ON (experimental; the default is off)";
                }
                break;
            case "entload":
                if (Patches.EntityLoadPatches.Enabled)
                {
                    // never strand a held entity: everything pending finishes before vanilla takes over
                    Patches.EntityLoadPatches.Enabled = false;
                    Patches.EntityLoadPatches.FlushAll();
                    state = "entity load budget OFF (vanilla: every entity finishes in its packet task; everything held is loaded now)";
                }
                else
                {
                    Patches.EntityLoadPatches.Enabled = true;
                    state = "entity load budget ON (" + Patches.EntityLoadPatches.BudgetMs.ToString("0.#",
                        System.Globalization.CultureInfo.CurrentCulture) + " ms/frame, nearest entity first)";
                }
                break;
            case "minimap":
                Patches.MinimapPatches.Enabled = !Patches.MinimapPatches.Enabled;
                state = "minimap budget " + (Patches.MinimapPatches.Enabled
                    ? "ON (" + Patches.MinimapPatches.TargetMs.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                      + " ms per tick, the cap adapts)"
                    : "OFF (vanilla: up to 200 tiles per tick)");
                break;
            case "minimapdirect":
                Patches.MinimapPatches.DirectUpload = !Patches.MinimapPatches.DirectUpload;
                state = "minimap direct upload " + (Patches.MinimapPatches.DirectUpload
                    ? "ON (tiles via glTexSubImage2D into the component texture)"
                    : "OFF (vanilla: a framebuffer draw per tile)");
                break;
            case "taskbudget":
                Patches.MainThreadTaskPatches.BudgetMs = Patches.MainThreadTaskPatches.BudgetMs > 0
                    ? 0 : (config.MainThreadTaskBudgetMs > 0 ? config.MainThreadTaskBudgetMs : 3.0);
                state = "task drain budget " + (Patches.MainThreadTaskPatches.BudgetMs > 0
                    ? "ON (" + Patches.MainThreadTaskPatches.BudgetMs.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                      + " ms per frame, the remainder goes to the next frame in order)"
                    : "OFF (vanilla: everything queued runs in this frame)")
                    + (Patches.MainThreadTaskPatches.Enabled ? "" : " - only takes effect with 'mtt' ON");
                break;
            case "animlod":
                Patches.EntityAnimPatches.LodEnabled = !Patches.EntityAnimPatches.LodEnabled;
                state = "anim lod " + (Patches.EntityAnimPatches.LodEnabled
                    ? "ON (shadow-only entities every 3rd, beyond " + Patches.EntityAnimPatches.FarBlocks.ToString("0", System.Globalization.CultureInfo.CurrentCulture)
                      + " blocks every 2nd frame)"
                    : "OFF (vanilla: every entity every frame)")
                    + (Patches.EntityAnimPatches.Enabled ? "" : " - only takes effect with 'entbefore' ON");
                break;
            case "entbefore":
                Patches.EntityAnimPatches.Enabled = !Patches.EntityAnimPatches.Enabled;
                state = "entity before attribution " + (Patches.EntityAnimPatches.Enabled
                    ? "ON (pre-render and anim clocked separately, hitch lines name the entity)"
                    : "OFF (vanilla loop, and therefore no anim lod either)");
                break;
            case "clientalloc":
                Patches.ClientAllocPatches.Enabled = !Patches.ClientAllocPatches.Enabled;
                state = "client alloc attribution " + (Patches.ClientAllocPatches.Enabled
                    ? "ON (worker threads and thread pool per caller)" : "OFF");
                break;
            case "allocsample":
                if (AllocSampler.Enabled)
                {
                    Measure.FrameStats.PeriodicSample -= AllocSampler.Sample;
                    AllocSampler.Stop();
                    state = "alloc sampling OFF";
                }
                else
                {
                    AllocSampler.Start();
                    if (AllocSampler.Enabled) Measure.FrameStats.PeriodicSample += AllocSampler.Sample;
                    state = AllocSampler.Enabled
                        ? "alloc sampling ON (runtime events, all threads, by type)"
                        : "alloc sampling could not start: " + AllocSampler.Failure;
                }
                break;
            case "packetsrc":
                Patches.PacketSourcePatches.Enabled = !Patches.PacketSourcePatches.Enabled;
                state = "block packet sources (server) " + (Patches.PacketSourcePatches.Enabled ? "ON" : "OFF")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only measures the integrated server");
                break;
            case "serveralloc":
                Patches.ServerAllocPatches.Enabled = !Patches.ServerAllocPatches.Enabled;
                state = "server alloc attribution " + (Patches.ServerAllocPatches.Enabled ? "ON" : "OFF")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only measures the integrated server");
                break;
            case "mtt":
                Patches.MainThreadTaskPatches.Enabled = !Patches.MainThreadTaskPatches.Enabled;
                state = "main thread task attribution " + (Patches.MainThreadTaskPatches.Enabled
                    ? "ON (every task is clocked, hitch lines name the packet type)"
                    : "OFF (vanilla drain)");
                break;
            case "tickprofiler":
                Patches.TickProfiler.Enabled = !Patches.TickProfiler.Enabled;
                WrapTickListeners();
                state = "tick profiler " + (Patches.TickProfiler.Enabled
                    ? "ON (" + Patches.TickProfiler.StatWrapped + " listeners wrapped)"
                    : "OFF (vanilla delegates)");
                break;
            case "entsync":
                Patches.EntitySyncPatches.DistanceSendRate = !Patches.EntitySyncPatches.DistanceSendRate;
                Patches.EntitySyncPatches.TrackingHysteresis = Patches.EntitySyncPatches.DistanceSendRate;
                state = "entity sync tuning (server) " + (Patches.EntitySyncPatches.DistanceSendRate
                    ? "ON (positions by distance, tracking with hysteresis)"
                    : "OFF (vanilla: 30 Hz for everything, hard tracking band)")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only has an effect on a server that runs komet");
                break;
            case "attrskip":
                Patches.EntitySyncPatches.AttributeNoOpSkip = !Patches.EntitySyncPatches.AttributeNoOpSkip;
                state = "attribute no-op skip (server) " + (Patches.EntitySyncPatches.AttributeNoOpSkip
                    ? "ON (unchanged attribute paths are not sent)"
                    : "OFF (vanilla: every dirty path goes out)")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only has an effect on a server that runs komet");
                break;
            case "edgeprio":
                if (Patches.EdgeRetessPriorityPatches.Enabled)
                {
                    Patches.EdgeRetessPriorityPatches.Enabled = false;
                    state = "edge retess prio OFF (vanilla order, visible edge repairs wait again)";
                }
                else
                {
                    Patches.EdgeRetessPriorityPatches.Enabled = true;
                    // explicit user intent overrides a self-disable
                    Patches.EdgeRetessPriorityPatches.HardDisabled = false;
                    state = "edge retess prio ON (visible edge repairs overtake the queue)";
                }
                break;
            default:
                return Loc.T("komet:msg-unknown-system", "unknown. Systems: ")
                     + "cull, simd, gapmerge, occlusion, reclaim, recycler, sunquery, glerror, "
                     + "prebuild, firepit, enttess, entload, minimap, minimapdirect, edgecoal, edgeprio, prioupload, uploaddruck, profiler, "
                     + "beforeattr, tickprofiler, mtt, taskbudget, entbefore, animlod, serveralloc, clientalloc, allocsample, packetsrc, retess, hudraster, cullcheck, cellsize, shadowbox, shadowmargin, shadowfade, "
                     + "shadowdist, shadowlod, shadowstab, shadowthrottle, shadowcull, shadowdepth, animwarm, entsync, attrskip";
        }

        var world = $"chunks {Vintagestory.Client.RuntimeStats.chunksReceived:N0} received, "
                    + $"queued {Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation:N0}, "
                    + $"uptime {uptime.Elapsed.TotalSeconds:F0}s";
        Mod.Logger.Notification("toggle: {0} | world: {1}", state, world);
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
            if (StressTest.Running) StressTest.Stop("safemode takes over");
            savedSunInterval = Patches.SunQueryPatches.Interval;
            savedGlErrorSkip = Patches.GlErrorPatches.SkipEnabled;
            savedFirepitGate = Patches.FirepitPatches.Enabled;
            savedEntityTess = Patches.EntityTessPatches.Enabled;
            savedEdgeCoalesce = Patches.EdgeCoalescePatches.Enabled;
            savedEdgePriority = Patches.EdgeRetessPriorityPatches.Enabled;
            AllVanilla();
            Mod.Logger.Notification("safemode ON | queued {0:N0}, uptime {1:F0}s",
                Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
            return Loc.T("komet:msg-safemode-on",
                "SAFEMODE ON - komet no longer draws anything differently from vanilla. "
                + "Glitch still there? Then it is not this mod. '.komet safemode' switches back.");
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
        Mod.Logger.Notification("safemode OFF | queued {0:N0}, uptime {1:F0}s",
            Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
        return Loc.T("komet:msg-safemode-off", "Safemode off - the optimisations run according to komet.json again.");
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
        Patches.AnimatableCullPatches.Enabled = false; // every animated block entity draws in every stage again
        Patches.EdgeCoalescePatches.Enabled = false;
        Patches.EdgeCoalescePatches.FlushAll();     // held edge marks go out, nothing strands
        Patches.EdgeRetessPriorityPatches.Enabled = false; // vanilla queue order again
        // shadows too: box shape, fade range, distance and update cadence all back to
        // vanilla, so "is a shadow artefact ours?" is answerable with one command
        Patches.ShadowPatches.ToVanilla();
        Patches.ShadowThrottlePatches.SetIntervals(1, 1, 1);
        Patches.ShadowStabilityPatches.Enabled = false;
        Patches.ShadowCullPatches.Enabled = false;      // every face into the shadow maps again
        Patches.ShadowCullPatches.DepthOnly = false;    // the engine's shader for every pass again
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
        Patches.AnimatableCullPatches.Enabled = config.CullAnimatableRenderers;
        Patches.EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0;
        Patches.EdgeRetessPriorityPatches.Enabled =
            config.EdgeRetessPriority && !Patches.EdgeRetessPriorityPatches.HardDisabled;
        Patches.ShadowPatches.ToConfigured(config.SymmetricShadowBox, config.FixShadowFadeCutoff);
        Patches.ShadowThrottlePatches.SetIntervals(
            config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip);
        Patches.ShadowStabilityPatches.Enabled = config.StabiliseShadowTexels;
        Patches.ShadowCullPatches.Enabled = config.ShadowCullBackfaces;
        Patches.ShadowCullPatches.DepthOnly = config.ShadowDepthOnlySolidPasses;
        SetCellTarget(config.PartsPerCellTarget);
    }

    /// <summary>
    /// The mod HUD's three steps: off -> compact -> full -> off. The same rule the performance
    /// HUD's F7 uses (DebugHud.CycleF7, pure and pinned by verify), driven by Shift+F7 and by
    /// '.komet mods hud' - two boxes that cycle differently would be the real surprise.
    /// </summary>
    private string CycleModHud()
    {
        (var visible, var compact) = DebugHud.CycleF7(modHud.Visible, modHud.Compact);
        modHud.Compact = compact;
        modHud.Visible = visible;
        if (!visible) return Loc.T("komet:msg-modhud-off", "mod HUD off");
        return compact
            ? Loc.T("komet:msg-modhud-on", "mod HUD on (Shift+F7 cycles compact / full / off)")
            : Loc.T("komet:msg-modhud-full", "mod HUD: full view (Shift+F7 again turns it off)");
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
        Patches.PrioUploadPatches.ResetStats();
        Patches.EntityTessPatches.ResetStats();
        Patches.EntityLoadPatches.ResetStats();
        Patches.MinimapPatches.ResetStats();
        Patches.MainThreadTaskPatches.Reset();
        Patches.TickProfiler.Reset();
        Patches.EntityAnimPatches.ResetStats();
        Patches.ServerAllocPatches.ResetStats();
        Patches.ClientAllocPatches.ResetStats();
        AllocSampler.ResetStats();
        Patches.PacketSourcePatches.ResetStats();
        Measure.GpuBusy.Reset();
        Patches.EntitySyncPatches.ResetStats();
        FastCuller.Workers.StatContendedInline = 0;
        Patches.MeshRecyclerPatches.ResetStats();
        Patches.TightClonePatches.ResetStats();
        Patches.AnimatableCullPatches.ResetStats();
        FastCuller.StatIncInserts = 0;
        FastCuller.StatIncRemovals = 0;
        Patches.EdgeRetessPriorityPatches.StatPromoted = 0;
        Patches.EdgeRetessPriorityPatches.StatSweeps = 0;
        Patches.EdgeRetessPriorityPatches.StatBusySkips = 0;
        PoolReclaimer.Reset();
        Patches.RendererProfiler.Reset();
        ModProfiler.Reset();
        FrameStats.Reset();
        HitchLog.Reset();
    }
}
