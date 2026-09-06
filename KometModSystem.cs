using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;
using HarmonyLib;
using Komet.Culling;
using Komet.Guard;
using Komet.Measure;
using Komet.Patches;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Komet;

/// <summary>
/// Client side CPU optimisations for Vintage Story 1.22.
///
/// The client runs the whole game tick plus every render stage on the main thread, so main
/// thread milliseconds are the frame rate. The dominant fixed cost per frame is the
/// visibility sweep over every tesselated chunk mesh part, which the engine performs about
/// three times per frame (opaque, shadow far, shadow near) over all geometry in memory - not
/// just what is on screen. That is what this mod rewrites.
///
/// This file is the lifecycle: which patches go in, in what order, and how everything comes
/// out again. The chat commands live in KometModSystem.Commands.cs, the HUD section and the
/// .komet text in KometModSystem.Stats.cs, config handling in KometModSystem.Config.cs.
/// </summary>
public partial class KometModSystem : ModSystem
{
    private Harmony harmony;
    private ICoreClientAPI capi;
    private KometConfig config;
    private long statsListenerId = -1;
    private GCLatencyMode previousLatencyMode;
    private bool gcLatencyChanged;
    private DebugHud hud;
    private ModHud modHud;
    /// <summary>The '.komet' window, created the first time somebody asks for it.</summary>
    private Gui.KometDialog window;
    private long modScanListenerId = -1;
    private SmoothedCounter partsPerFrame, rawRangesPerFrame, rangesPerFrame, bridgedPerFrame, sweepsPerFrame, batchesPerFrame, rebuildsPerFrame, cellsSkippedPerFrame, rebuildTicksPerFrame;
    private SmoothedCounter nearTrisPerFrame, nearRangesPerFrame, cameraTrisPerFrame, farTrisPerFrame;
    private PoolReclaimer.Renderer reclaimer;
    private GpuFrameTimer.BeginRenderer gpuBegin;
    private GpuFrameTimer.EndRenderer gpuEnd;
    private long inflowListenerId = -1;
    private long rewrapListenerId = -1;
    private long edgeFlushListenerId = -1;
    private long fbRebuildListenerId = -1;
    private int fbRebuildTries;
    private long guardListenerId = -1;
    private Action guardFinalize;
    private Action foreignFinalize;
    private long foreignCallbackId = -1;
    private ForeignClientDialog foreignDialog;
    /// <summary>
    /// Everything this session subscribed to the frame boundary, so leaving the world can take
    /// exactly those handlers off again.
    ///
    /// A handler left on stacks a second copy on the next join, and the doubling is silent:
    /// RendererProfiler.EndFrame folds every entry twice per frame (halving every average), the
    /// stress test sees half-length frames, the upload budget squares its own correction. This
    /// used to be nine 'bool xHooked' fields, nine subscribe sites and nine matching if-blocks
    /// in Dispose, where the two halves could name different handlers and nothing would say so.
    /// Now the subscribe records the delegate it actually added.
    /// </summary>
    private readonly List<Action> frameBoundaryHooks = new();

    /// <summary>The per-frame counters this session registered - same bookkeeping, same
    /// reason: <see cref="FrameStats"/>'s list is static and survives the world.</summary>
    private readonly List<SmoothedCounter> trackedCounters = new();
    private string fbBlockedBy;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    /// <summary>Patch before anything renders, and before other mods start pooling meshes.</summary>
    public override double ExecuteOrder() => 0.05;

    public override void StartPre(ICoreAPI api)
    {
        config = LoadConfig(api);
    }

    public override void Start(ICoreAPI api)
    {
        FastCuller.PoolLevelCulling = config.PoolLevelCulling;
        FastCuller.Parallel = config.ParallelCulling;
        FastCuller.MergeDrawRanges = config.MergeDrawRanges;
        FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges;
        FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod;
        FastChunkCuller.MinIntervalMs = config.OcclusionMinIntervalMs;
        UploadBudget.TargetMs = config.UploadBudgetTargetMs;
        UploadBudget.Enabled = config.AdaptiveUploadBudget;
        UploadBudget.FramePressureInput = config.UploadFramePressure;
        FastCuller.MeasureTime = config.MeasureCullTime;
        FastCuller.VectorCulling = config.VectorCulling && FastCuller.VectorAvailable;
        FastCuller.Log = msg => Mod.Logger.Notification(msg);

        // One pool for every CPU-heavy job this mod owns - the two sweeps, the window prebuild,
        // the neighbour unpack, the animation prewarm and the HUD raster - rather than a thread
        // set per workload. Four sets used to size themselves against the core count in
        // ignorance of each other and put eleven threads on six physical cores; a worker here
        // takes whichever queued job is worth most, so the sweep on the frame's deadline is
        // never behind an occlusion walk that happens to hold every thread. Started before the
        // first frame, so no frame pays for the thread creation.
        FastCuller.PartsPerCellTarget = config.PartsPerCellTarget;
        FastChunkCuller.Niceness = config.OcclusionThreadNiceness;
        JobScheduler.Start(config.WorkerThreads, config.OcclusionThreadNiceness);
        // The overlay's cairo raster is background work like any other; the baseline keeps its
        // own synchronous path, which is why this is injected rather than referenced.
        DebugHud.RasterDispatch = job => JobScheduler.Submit(JobKind.Hud, long.MinValue, job);
        RetessSourcePatches.SampleSources = config.SampleRetessSources;
        CullVerifier.SampleEvery = config.VerifyCullSweepEvery;
        // Warning, not Notification: a disagreement here means the screen is wrong, and it has
        // to stand out in a log that scrolls past at a hundred lines a second.
        CullVerifier.Log = msg => Mod.Logger.Warning(msg);

        ApplyGcLatencyMode();

        harmony = new Harmony(Mod.Info.ModID);
        ApplyPatches();
    }

    /// <summary>
    /// A blocking gen2 collection is long enough to be a dropped frame, and the client makes
    /// plenty of garbage while chunks stream in. SustainedLowLatency tells the collector to
    /// stay out of the way until it really has to act; it is a request, not a guarantee, and
    /// it can be refused outright (server GC without concurrent mode), so failure is fine.
    /// </summary>
    private void ApplyGcLatencyMode()
    {
        if (!config.LowLatencyGC) return;
        try
        {
            previousLatencyMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            gcLatencyChanged = GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency;
            Mod.Logger.Notification(gcLatencyChanged
                ? "enabled: sustained low latency GC"
                : "GC stayed in {0} mode - the runtime refused the request, which is harmless",
                GCSettings.LatencyMode);
        }
        catch (Exception e)
        {
            Mod.Logger.Notification("could not set the GC latency mode ({0}), continuing without it", e.GetType().Name);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        // The depth-only shadow program needs the client (shadow MVP matrix, atlas padding)
        // and has to follow every shader reload; both belong to this world.
        ShadowCullPatches.Game = api.World as Vintagestory.Client.NoObf.ClientMain;
        api.Event.ReloadShader += OnShadersReloaded;

        // reading and flipping allowPStorage needs the platform, which only exists client side
        Patch(() => PersistentMappingPatch.Probe(Mod.Logger), "persistent mapping probe");
        if (config.ExperimentalPersistentMapping)
            Patch(() => PersistentMappingPatch.Enable(Mod.Logger), "experimental persistent mapping");

        if (config.MeasureGpuTime)
            Patch(() =>
            {
                GpuFrameTimer.Enabled = true;
                // the far cascade's per-drawn-frame cost needs to know which frames drew it
                GpuFrameTimer.FarCascadeDrawn = () => ShadowThrottlePatches.FarDrawnThisFrame;
                gpuBegin = new GpuFrameTimer.BeginRenderer();
                gpuEnd = new GpuFrameTimer.EndRenderer();
                api.Event.RegisterRenderer(gpuBegin, EnumRenderStage.Before, "kometgpu0");
                api.Event.RegisterRenderer(gpuEnd, EnumRenderStage.Done, "kometgpu1");
                // the driver's busy figure next to the span, where the OS publishes one (amdgpu sysfs)
                FrameStats.PeriodicSample += GpuBusy.Sample;
            }, "gpu frame timing");

        if (config.ReclaimEmptyPools)
            Patch(() =>
            {
                PoolReclaimer.EnsureReady();
                PoolReclaimer.Enabled = true;
                PoolReclaimer.AfterSeconds = config.ReclaimEmptyPoolsAfterSeconds;
                reclaimer = new PoolReclaimer.Renderer(api);
                api.Event.RegisterRenderer(reclaimer, EnumRenderStage.Done, "kometreclaim");
            }, $"reclaim empty mesh pools (after {config.ReclaimEmptyPoolsAfterSeconds:0}s)");

        if (config.AdaptiveChunkInflow && api.IsSinglePlayer)
            Patch(() =>
            {
                InflowBrake.LowWater = config.InflowLowWaterChunks;
                InflowBrake.HighWater = config.InflowHighWaterChunks;
                // whatever the server system settled on, before the brake ever touches it
                InflowBrake.Capture(Vintagestory.Server.MagicNum.ChunksColumnsToRequestPerTick,
                                    Vintagestory.Server.MagicNum.ChunkRequestTickTime);
                InflowBrake.Enabled = true;
                inflowListenerId = api.Event.RegisterGameTickListener(InflowBrakeTick, 500);
            }, $"adaptive chunk inflow (full rate below {config.InflowLowWaterChunks} queued, minimum at {config.InflowHighWaterChunks})");

        // Always wired, gated at runtime: nothing is wrapped while the profiler is off, and
        // '.komet toggle profiler' can arm it mid-session without a restart. Default is off -
        // ten thousand timing decorators are not something to carry for a question nobody
        // currently has (see KometConfig.ProfileRenderers).
        Patch(() =>
        {
            // Wrapping substitutes the objects in the dispatch list, so the engine's
            // by-reference UnregisterRenderer must learn to find them - without this,
            // every block entity that unloads leaves a ghost renderer behind.
            RendererProfiler.ApplyUnregisterFix(harmony);

            // Wrapping needs the event manager, which only exists once the client is up.
            // Renderers register throughout a session, so this is repeated periodically -
            // a renderer registered in the gap simply goes unmeasured until the next pass.
            HookFrameBoundary(RendererProfiler.EndFrame);
            rewrapListenerId = api.Event.RegisterGameTickListener(RewrapRenderersTick, 250);

            // The hitch log asks for this at detection time - inside the frame boundary,
            // before EndFrame folds and clears the per-frame ticks - so a hitch frame can
            // name its most expensive renderer, not just its stage.
            HitchLog.TopRendererProvider = RendererProfiler.TopOfCurrentFrame;

            RendererProfiler.Enabled = config.ProfileRenderers;
            // The Before stage (a handful of system renderers: entities, chunk uploads,
            // liquid depth, camera ...) stays attributed even with the profiler off - it is
            // where the unnamed world-join bursts live, and naming a hitch must not depend
            // on having armed the profiler before it happened.
            RendererProfiler.AttributeBeforeStage = config.AttributeBeforeStage;

            // The game tick's listeners get the same treatment as the Before stage: always
            // wrapped (a few dozen delegates, two Stopwatch reads each), so a "tick 12,7"
            // hitch names its listener instead of a bucket with a hundred owners.
            TickProfiler.Enabled = config.ProfileTickListeners;
            HitchLog.TopTickListenerProvider = TickProfiler.TopOfCurrentFrame;
            HookFrameBoundary(TickProfiler.EndFrame);
            WrapRenderers();
        }, config.ProfileRenderers
            ? "per renderer profiling"
            : "per renderer profiling off (vanilla dispatch), before-stage attribution "
              + (config.AttributeBeforeStage ? "on" : "off") + "; '.komet toggle profiler' enables the full set live");

        // Hitch log: every frame over the threshold is booked with its bucket breakdown and
        // the camera's turn/move rate for exactly that frame, so a stutter complaint can be
        // split into "beim drehen" / "in bewegung" / "im stand" and attributed - the smoothed
        // averages hide a rare spike by construction.
        HitchLog.MinMs = config.HitchMinMs;
        HitchLog.Factor = config.HitchFrameFactor;
        HitchLog.Log = msg => Mod.Logger.Notification(msg);
        HitchLog.CommandHint = "'.komet hitch'";
        HookFrameBoundary(SampleCameraForHitchLog);
        // the tesselation rates fold with the GC rates at the frame boundary - not in the
        // HUD, whose visibility must not decide whether a report has numbers in it
        FrameStats.PeriodicSample += TesselationStats.Sample;

        // Both halves, because they can disagree: DOTNET_gcServer only reaches the process
        // through the desktop icon or vs-launch.sh, so a request that never arrived looks
        // exactly like one that was never made. Which mode is the right one is an open
        // measurement - see HitchLog.GcModeVerdict.
        Mod.Logger.Notification("gc-modus: {0} (angefordert: {1}), latenz {2}",
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
            Environment.GetEnvironmentVariable("DOTNET_gcServer") ?? "nichts gesetzt",
            System.Runtime.GCSettings.LatencyMode);

        partsPerFrame = Track(() => FastCuller.StatPartsTested);
        cellsSkippedPerFrame = Track(() => FastCuller.StatCellsSkipped);
        rebuildTicksPerFrame = Track(() => FastCuller.StatRebuildTicks);
        rebuildsPerFrame = Track(() => FastCuller.StatRebuilds);
        rawRangesPerFrame = Track(() => FastCuller.StatRangesRaw);
        rangesPerFrame = Track(() => FastCuller.StatRangesEmitted);
        bridgedPerFrame = Track(() => FastCuller.StatRangesBridged);
        sweepsPerFrame = Track(() => FastCuller.StatSweeps);
        batchesPerFrame = Track(() => FastCuller.StatBatches);
        nearTrisPerFrame = Track(() => FastCuller.StatTrisNear);
        nearRangesPerFrame = Track(() => FastCuller.StatRangesNear);
        cameraTrisPerFrame = Track(() => FastCuller.StatTrisCamera);
        farTrisPerFrame = Track(() => FastCuller.StatTrisFar);

        DebugHud.BackgroundRaster = config.HudBackgroundRaster;
        hud = new DebugHud(api, "komet " + KometVersion.Display(Mod.Info.Version))
        {
            Visible = config.DebugHudVisible,
            Compact = true,
            ExtraSection = WriteKometSection,
            ExtraCompactSection = WriteKometWarnings
        };
        api.Event.RegisterRenderer(hud, EnumRenderStage.Ortho, "komethud");

        // The mod profiler and its own overlay. The index is built here because this is the
        // first moment every mod is loaded and the client API exists; the load times booked
        // before it (by the phase patch, which has no index to write into yet) are merged in.
        ModProfiler.Enabled = config.ProfileMods;
        if (config.ProfileMods)
        {
            ModProfiler.BuildIndex(api.ModLoader?.Mods);
            Mod.Logger.Notification("mod profiler: {0} mods indexed, {1} of them with code",
                ModProfiler.ModCount, ModProfiler.CodeModCount);
        }
        modHud = new ModHud(api, "komet · mods")
        {
            Visible = config.ModHudVisible,
            Compact = true,
            // What this HUD can see depends on it, and the reader must not have to remember
            AllRenderersWrapped = () => RendererProfiler.Enabled
        };
        api.Event.RegisterRenderer(modHud, EnumRenderStage.Ortho, "kometmodhud");
        // Shift+F7, not a key of its own: this mod already owns F7, and the free-looking keys
        // are not free (F6 was tried and is a minimap macro). The engine matches modifiers
        // exactly and runs that pass over every hotkey BEFORE the modifier-ignoring fallback
        // pass, so plain F7 and Shift+F7 cannot trigger each other - the first exact match ends
        // the dispatch. Same three-step cycle as F7, and '.komet mods hud' does the same thing
        // for anyone who rebinds or prefers typing.
        api.Input.RegisterHotKey("kometmodhud", "komet: Mod-Profiler-HUD", GlKeys.F7,
            HotkeyType.HelpAndOverlays, altPressed: false, ctrlPressed: false, shiftPressed: true);
        api.Input.SetHotKeyHandler("kometmodhud", _ => { CycleModHud(); return true; });
        // Patches and registered classes, rescanned on the patch guard's cadence and for the
        // same reason: mods patch lazily, and an inventory nobody refreshes is a stale one.
        if (config.ProfileMods)
            modScanListenerId = api.Event.RegisterGameTickListener(ScanModsTick, 10000);

        // Ctrl+F7 opens the window, alongside F7 (overlay) and Shift+F7 (mod overlay). A third
        // variant of a key this mod already owns rather than a fourth key: the engine matches
        // modifiers exactly and runs that pass before the modifier-ignoring fallback, so the
        // three cannot trigger each other - and the keys that LOOK free are not (F6 was tried
        // and is a minimap macro). The dialog itself is created on first use; a player who
        // never opens it never pays for a cairo surface and a GL texture.
        api.Input.RegisterHotKey(Gui.KometDialog.HotkeyCode, "komet: Performance-Fenster", GlKeys.F7,
            HotkeyType.HelpAndOverlays, altPressed: false, ctrlPressed: true, shiftPressed: false);
        api.Input.SetHotKeyHandler(Gui.KometDialog.HotkeyCode, _ =>
        {
            if (window is { } open && open.IsOpened()) open.TryClose();
            else OpenWindow(Gui.KometView.Overview);
            return true;
        });

        api.Input.RegisterHotKey("komethud", "komet: Performance-HUD", GlKeys.F7, HotkeyType.HelpAndOverlays);
        api.Input.SetHotKeyHandler("komethud", _ =>
        {
            // aus -> kompakt (the player view) -> voll (the diagnostic instrument) -> aus.
            // The properties self-invalidate, so the next rendered frame shows exactly the
            // new state - the cycle rule itself lives in DebugHud where verify can pin it.
            (var v, var c) = DebugHud.CycleF7(hud.Visible, hud.Compact);
            hud.Compact = c;
            hud.Visible = v;
            return true;
        });

        // DestroyGameSession fires this BEFORE it tells the client threads to exit and
        // hands them their 200 ms window. Mod Dispose runs long after that window - a
        // shutdown flag set there provably missed a mid-tick tesselation (the exit NRE in
        // BuildExtendedChunkData came back). This is the only hook early enough.
        api.Event.LeaveWorld += OnLeaveWorldEarly;

        // Patch collision guard: who else is on the methods komet patches (or on komet's own
        // code), and is the engine underneath the build komet was verified against. Once at
        // world start - by LevelFinalize every mod has run its Start - then every 10 s for
        // the mods that patch lazily. Each finding is logged once and stays in the report;
        // nothing is "resolved", which side should win is not this mod's call.
        PatchGuard.Warn = msg => Mod.Logger.Warning(msg);
        PatchGuard.Notify = msg => Mod.Logger.Notification(msg);
        guardFinalize = () =>
        {
            // The drain budget starts here and not a frame earlier: everything the join
            // queued (LevelFinalize itself included) has to run in its own frame, or a
            // renderer that is already registered draws against state its LevelFinalize
            // has not built yet (see MainThreadTaskPatches.WorldReady).
            MainThreadTaskPatches.WorldReady = true;
            RunPatchGuard(engineCheck: true);
        };
        api.Event.LevelFinalize += guardFinalize;
        guardListenerId = api.Event.RegisterGameTickListener(PatchGuardTick, 10000);

        // Optimum (a forked client) and OptiTime (a mod) replace the same engine code komet
        // does, each unaware of the other. The player hears it at every world join: the log
        // line now, chat line and dialog a moment after LevelFinalize, when the loading
        // screen is gone. Komet stays on - which side should win is not this mod's call.
        ForeignClient.Scan(name => typeof(Vintagestory.Client.NoObf.ClientMain).Assembly.GetType(name),
            Vintagestory.API.Config.GameVersion.LongGameVersion,
            id => api.ModLoader.GetMod(id)?.Info);
        if (ForeignClient.Findings.Count > 0)
        {
            Mod.Logger.Warning("incompatible client: {0} - it replaces the same engine code komet does, each unaware of the other; run one or the other, not both",
                ForeignClient.Describe());
            foreignFinalize = () =>
            {
                if (foreignCallbackId >= 0) api.Event.UnregisterCallback(foreignCallbackId);
                foreignCallbackId = api.Event.RegisterCallback(_ =>
                {
                    foreignCallbackId = -1;
                    var what = ForeignClient.Describe();
                    api.ShowChatMessage(ForeignClient.ChatText(what));
                    foreignDialog?.TryClose();
                    foreignDialog = new ForeignClientDialog(api, ForeignClient.Title(), ForeignClient.DialogText(what), ForeignClient.Button());
                    foreignDialog.TryOpen();
                }, 1500);
            };
            api.Event.LevelFinalize += foreignFinalize;
        }

        // The enlarged shadow framebuffers have to be forced into existence: the engine builds
        // its framebuffers at window creation, before any mod loads, so the transpiler alone
        // reaches nothing on a normal launch (see ShadowResPatches.TryForceRebuild). Retried on
        // a slow tick because the engine suppresses buffer rebuilds while it is still loading.
        if (config.ShadowMapExtraQuality > 0 || config.ShadowNearMapSize > 0)
            fbRebuildListenerId = api.Event.RegisterGameTickListener(FbRebuildTick, 500);

        // 50 ms cadence: together with the per-tick cap this sets the drain capacity
        // (~5000/s baseline, more in catch-up mode) - it must exceed any realistic inflow.
        // Registered even when the feature is off, so the live toggle has a flusher.
        edgeFlushListenerId = api.Event.RegisterGameTickListener(EdgeFlushTick, 50);

        RegisterCommands(api);
        HookFrameBoundary(StressTest.OnFrameBoundary);

        // The pool's own frame tick: fold its rates, move the active worker count against what
        // the last frame and the loading front looked like, and run whatever a worker handed
        // back to the main thread - under a budget, so a burst of handoffs is spread over
        // frames instead of lengthening one.
        HookFrameBoundary(WorkerPoolFrame);
        FrameStats.FrameSummary += WorkerPoolSummary;

        if (config.StatsLogIntervalSeconds > 0)
        {
            statsListenerId = api.Event.RegisterGameTickListener(
                StatsLogTick, config.StatsLogIntervalSeconds * 1000);
        }
    }

    private void ApplyPatches()
    {
        // First of everything, and that is the point: this times the ModSystem phases of every
        // mod, and only the ones that run AFTER this line can be measured. Komet loads at
        // ExecuteOrder 0.05, so "after this line" is nearly the whole load.
        if (config.ProfileMods)
            Patch(() =>
            {
                ModPhasePatches.Apply(harmony);
                ModPhasePatches.Enabled = true;
            }, "mod load time attribution (per mod, per phase)");

        // Measurement only: who on the client's worker threads and the thread pool allocates
        // - the report's "rest" (03.09.: 79 of 216 MB/s unnamed while 384 of 402 hitches sat on
        // a GC pause). Applied always, gated at runtime, sampled with the other rates.
        Patch(() =>
        {
            ClientAllocPatches.Apply(harmony);
            ClientAllocPatches.Enabled = config.ClientAllocAttribution;
            FrameStats.PeriodicSample += ClientAllocPatches.Sample;
        }, "client thread allocation attribution " + (config.ClientAllocAttribution ? "on" : "off")
           + "; '.komet toggle clientalloc' flips it");

        // The sample-based view over every thread in the process, from the runtime's own
        // allocation tick events - names what no bracket reaches (03.09.: "rest 46 MB/s"
        // after every engine thread was bracketed).
        if (config.AllocSampling)
            Patch(() =>
            {
                AllocSampler.Start();
                if (!AllocSampler.Enabled)
                    throw new InvalidOperationException(AllocSampler.Failure ?? "the runtime event listener did not start");
                FrameStats.PeriodicSample += AllocSampler.Sample;
            }, "allocation sampling by thread and type (runtime GCAllocationTick events); '.komet toggle allocsample' flips it");

        if (config.FastFrustumCulling)
            Patch(() =>
            {
                FastCuller.EnsureReady();
                harmony.CreateClassProcessor(typeof(MeshDataPoolPatches)).Patch();
            }, "fast frustum culling");

        if (config.FastOcclusionCulling)
            Patch(() =>
            {
                FastChunkCuller.EnsureReady();
                harmony.CreateClassProcessor(typeof(ChunkCullerPatches)).Patch();
            }, "parallel occlusion culling");

        if (config.BulkMeshUpload || config.ExperimentalPersistentMapping)
            Patch(() => MeshUploadPatches.Apply(harmony), "bulk chunk mesh upload");

        // Must run before the framebuffers are built, i.e. before the first frame. Always
        // applied: the near-map size is gated at run time so '.komet shadownear' works in a
        // session that started at 0; the extra step's transpiler goes on only when asked.
        Patch(() => ShadowResPatches.Apply(harmony, config.ShadowMapExtraQuality, config.ShadowNearMapSize),
              (config.ShadowMapExtraQuality > 0
                  ? $"shadow map +{config.ShadowMapExtraQuality} quality step(s)"
                  : "shadow map at the engine's size")
              + (config.ShadowNearMapSize > 0 ? $", near cascade map {config.ShadowNearMapSize}px" : ", near cascade map as the far one"));

        // Always applied, gated at runtime ('.komet toggle shadowcull'): culling on the solid
        // passes of the shadow map draws the same depth map with half the solid faces.
        Patch(() => ShadowCullPatches.Apply(harmony, config.ShadowCullBackfaces, config.ShadowDepthOnlySolidPasses),
              "shadow pass solid passes: back-face culling " + (config.ShadowCullBackfaces ? "on" : "off")
              + ", depth-only shader " + (config.ShadowDepthOnlySolidPasses ? "on" : "off"));

        // Always applied, gated at runtime, so a shadow artefact can be bisected while it is
        // on screen ('.komet toggle shadowbox|shadowfade|shadowdist', and safemode).
        Patch(() =>
              {
                  ShadowPatches.Apply(harmony, config.FixShadowFadeCutoff, config.ShadowDistanceMultiplier, config.SymmetricShadowBox, config.ShadowTightCullBox, config.ShadowNearDepthExtend);
                  // The coverage margin and the throttle's movement limit are one decision in
                  // two places: the box is drawn wider exactly so the camera may move that far
                  // before the retained map has to be redrawn.
                  ShadowPatches.FarBoxMargin = Math.Max(0.0, config.ShadowFarBoxMargin);
              },
              $"shadow patches (fade fix {(config.FixShadowFadeCutoff ? "on" : "off")}, "
              + $"distance x{config.ShadowDistanceMultiplier:0.##}, symmetric box {(config.SymmetricShadowBox ? "on" : "off")}"
              + (config.SymmetricShadowBox && config.ShadowFarBoxMargin > 0
                  ? $", far coverage margin {config.ShadowFarBoxMargin:0.#} blocks"
                  : ", no far coverage margin") + ")");

        // measurement first: the throttle and the HUD both hang off it. The optional
        // attribution brackets report themselves by name when an engine build changed the
        // method they hang on (see MeasurementPatches.Apply); the core accounting throws.
        MeasurementPatches.Warn = msg => Mod.Logger.Warning(msg);
        Patch(() => MeasurementPatches.Apply(harmony), "frame + render stage measurement");

        if (config.SunOcclusionQueryInterval > 1)
            Patch(() => SunQueryPatches.Apply(harmony, config.SunOcclusionQueryInterval),
                  $"sun occlusion query every {config.SunOcclusionQueryInterval} frames");

        if (config.StabiliseShadowTexels)
            Patch(() => ShadowStabilityPatches.Apply(harmony), "shadow texel snapping");

        // Always applied, gated at runtime, because it changes what the near cascade DRAWS and
        // that is the one thing a user can see going wrong. Same window as the snapping above:
        // both are postfixes on loadOrthoModeMatrix, one writing x/y, this one z.
        Patch(() => ShadowDepthPatches.Apply(harmony, config.ShadowNearDepthFit),
              config.ShadowNearDepthFit
                  ? "near shadow depth fitted to what can cast (the untranslated ortho spends half the extend down-sun)"
                  : "near shadow depth as the engine projects it; '.komet toggle shadownearfit' enables the fit live");

        // Always applied, gated at runtime and by the near throttle: the planes are the
        // engine's own, moved in to what the camera can see.
        Patch(() => ShadowFootprintPatches.Apply(harmony, config.ShadowNearFootprintCull),
              config.ShadowNearFootprintCull
                  ? "near shadow pass culled to casters that can reach a visible receiver"
                  : "near shadow pass drawn for every direction (vanilla); '.komet toggle shadowfootprint' enables the cull live");

        // The passes measured where the timestamps could not: elapsed-time brackets around
        // the chunk passes on every third frame. The shadow halves hang on the transpiled
        // boundary ShadowCullPatches owns; this adds the camera pass.
        Patch(() =>
              {
                  ChunkPassProbePatches.Apply(harmony);
                  GpuPassProbe.Enabled = config.GpuPassProbe;
              },
              config.GpuPassProbe
                  ? "gpu pass probe (elapsed time + fragments per chunk pass, every 3rd frame)"
                  : "gpu pass probe off; '.komet toggle passprobe' enables it live");

        // Which pass a pool belongs to, for the sweep's triangle histogram and the foliage
        // range. Measurement first; the range is off unless komet.json says otherwise.
        Patch(() =>
              {
                  PoolPassPatches.Apply(harmony);
                  FastCuller.FoliageRangeSq = config.FoliageRange > 0 ? config.FoliageRange * config.FoliageRange : 0;
                  FastCuller.ShadowFoliageRangeSq = config.ShadowFoliageRange > 0 ? config.ShadowFoliageRange * config.ShadowFoliageRange : 0;
                  ParticlePatches.ConfiguredOrphan = config.ParticleBufferOrphaning;
                  ParticlePatches.Orphan = config.ParticleBufferOrphaning;
                  HookFrameBoundary(FastCuller.HistogramFrame);
              },
              config.FoliageRange > 0
                  ? $"camera pass triangles by pass and distance; foliage passes drawn to {config.FoliageRange:0} blocks"
                  : "camera pass triangles by pass and distance; foliage passes to the view distance (vanilla)");

        // The far LOD: beyond the far distance a chunk part is drawn as cells of two blocks,
        // beyond twice that as cells of four. Always patched (the pictures built at
        // tesselation time need the pool hook whatever the switch says), gated live; off
        // draws the engine's picture with nothing re-tesselated.
        Patch(() => FarMeshPatches.Apply(harmony, config.FarMesh, config.FarMeshDistance, config.FarMeshTier2, Mod.Logger),
              config.FarMesh
                  ? (config.FarMeshDistance > 0
                      ? $"far lod beyond {config.FarMeshDistance:0} blocks{(config.FarMeshTier2 ? ", cells of four beyond twice that" : "")}"
                      : $"far lod beyond max(400, 0.35 x view distance){(config.FarMeshTier2 ? ", cells of four beyond twice that" : "")}")
                  : "far lod off; '.komet toggle farmesh' enables it live");

        // Pools as places, and the camera pass nearest first. The routing is always patched
        // and gated live; the order is a flag on the sweep and the manager prefix.
        Patch(() =>
              {
                  SpatialPools.Apply(harmony, config.SpatialPools, config.SpatialPoolRegion);
                  FastCuller.ConfiguredFrontToBack = config.FrontToBack;
                  FastCuller.FrontToBack = config.FrontToBack;
              },
              (config.SpatialPools
                  ? $"mesh pools routed by {SpatialPools.ClampRegion(config.SpatialPoolRegion)}-block region"
                  : "mesh pools first-fit (vanilla); '.komet toggle spatialpools' routes them live")
              + (config.FrontToBack
                  ? ", camera pass drawn nearest first"
                  : ", camera pass in index order (vanilla); '.komet toggle fronttoback' sorts it live"));

        // Always applied, gated at runtime by the interval values (1/1 = exactly vanilla), so
        // '.komet toggle shadowthrottle' can bisect a shadow artefact live.
        Patch(() => ShadowThrottlePatches.Apply(harmony,
                        config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval,
                        config.ShadowFarMaxSkip, config.ShadowFarMoveThreshold),
              config.ShadowFarUpdateInterval > 1 || config.ShadowFarMaxSkip > 1 || config.ShadowNearUpdateInterval > 1
                  ? $"adaptive shadow throttling (far every {config.ShadowFarUpdateInterval}-{Math.Max(config.ShadowFarUpdateInterval, config.ShadowFarMaxSkip)} frames, "
                    + $"near every {config.ShadowNearUpdateInterval})"
                  : "shadow throttling off (far cascade every frame); '.komet toggle shadowthrottle' enables it live");

        if (config.AdaptiveUploadBudget)
            Patch(() =>
            {
                UploadBudgetPatches.Apply(harmony);
                // the frame-pressure input: the finished frame's totals reach the
                // controller each boundary, so it can see the deferred driver cost the
                // upload clock is blind to under mesa_glthread
                FrameStats.FrameSummary += UploadBudget.NotePressure;
            }, "adaptive chunk upload budget");

        // Always applied, gated at runtime: whether a relight storm's uploads may be spread
        // over frames is exactly the kind of question '.komet toggle prioupload' answers live.
        Patch(() =>
        {
            PrioUploadPatches.Apply(harmony);
            PrioUploadPatches.Enabled = config.BudgetPriorityUploads;
        }, config.BudgetPriorityUploads
            ? "priority chunk upload budget (storms spread over frames, player edits unaffected)"
            : "priority chunk uploads unbudgeted (vanilla); '.komet toggle prioupload' enables the budget live");

        if (config.TesselationNoIdleSleep || config.TesselationThreadPriority || config.TesselationNeighbourPrefetch)
        {
            // Thread.Priority is stored and never applied on Linux (CoreCLR's PAL; measured
            // for the cull workers, which sit at the process nice value whatever they ask
            // for), and a lower nice value needs privileges the game does not have. Saying
            // "thread priority: True" in a Linux log claimed a lever that does not exist.
            var priority = config.TesselationThreadPriority && !OperatingSystem.IsLinux();
            Patch(() => TesselationPatches.Apply(harmony,
                            config.TesselationNoIdleSleep, priority,
                            config.TesselationNeighbourPrefetch),
                  "faster chunk loading (no idle sleep: " + config.TesselationNoIdleSleep
                  + ", thread priority: " + (priority ? "True"
                      : config.TesselationThreadPriority ? "not available on Linux" : "False")
                  + ", neighbour prefetch: " + config.TesselationNeighbourPrefetch + ")");
        }

        if (config.FirepitContentsMaxDistance > 0 || config.FirepitLightCacheMs > 0)
            Patch(() =>
            {
                FirepitPatches.Log = msg => Mod.Logger.Warning(msg);
                FirepitPatches.Apply(harmony, config.FirepitContentsMaxDistance, config.FirepitLightCacheMs);
                // held in a field so Dispose can take exactly this handler off the static
                // event again - an anonymous lambda would be unremovable, and the stale
                // closure would keep publishing the previous session's camera and API
                HookFrameBoundary(() =>
                {
                    FirepitPatches.CameraPos = capi?.World?.Player?.Entity?.CameraPos;
                    FirepitPatches.Api ??= capi;
                });
            }, $"firepit contents gate (beyond {config.FirepitContentsMaxDistance} blocks, "
             + $"light cache {config.FirepitLightCacheMs} ms)");

        if (config.MeasureRetessSources)
            Patch(() => RetessSourcePatches.Apply(harmony), "dirty-mark source sampling");

        // Always applied but runtime-gated: default off since 1.36.0 (stress test measured
        // a small cost, and it was twice prime suspect for border holes on fresh terrain);
        // '.komet toggle edgecoal' switches the held-back marking on live for experiments.
        Patch(() =>
        {
            EdgeCoalescePatches.Log = msg => Mod.Logger.Warning(msg);
            EdgeCoalescePatches.Apply(harmony,
                config.EdgeRetessCoalesceMs > 0 ? config.EdgeRetessCoalesceMs : 400);
            EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0;
        }, config.EdgeRetessCoalesceMs > 0
            ? $"edge retess coalescing ({config.EdgeRetessCoalesceMs:0} ms window)"
            : "edge retess coalescing off (vanilla marking); '.komet toggle edgecoal' enables it live");

        // Always applied but runtime-gated: whether border holes close sooner is judged by
        // eye at the load front, so '.komet toggle edgeprio' must be able to A/B it live.
        Patch(() =>
        {
            EdgeRetessPriorityPatches.Log = msg => Mod.Logger.Warning(msg);
            EdgeRetessPriorityPatches.Apply(harmony);
            EdgeRetessPriorityPatches.Enabled = config.EdgeRetessPriority;
        }, config.EdgeRetessPriority
            ? "edge retess priority (visible border repairs jump the tesselation queue)"
            : "edge retess priority off (vanilla order); '.komet toggle edgeprio' enables it live");

        // Always applied, gated at runtime: the gate decides whose storage answers the
        // recycler's API, and '.komet toggle recycler' must be able to A/B it while the
        // GC counters are on screen. Enabling hands vanilla's held buffers over (on the
        // tesselation thread, the only place that may touch them), disabling frees ours.
        Patch(() =>
        {
            MeshRecyclerPatches.BudgetMb = config.MeshRecyclerBudgetMb;
            MeshRecyclerPatches.Apply(harmony);
            MeshRecyclerPatches.SetEnabled(config.FastMeshRecycler);
        }, config.FastMeshRecycler
            ? $"mesh recycler size-class pool ({config.MeshRecyclerBudgetMb} MB budget)"
            : "mesh recycler pool off (vanilla storage); '.komet toggle recycler' enables it live");

        // Always applied, gated at runtime, same reasoning as the recycler: the A/B against
        // the GC counters must work live.
        Patch(() =>
        {
            TightClonePatches.Apply(harmony);
            TightClonePatches.Enabled = config.TightCustomClones;
            TightClonePatches.PoolExtras = config.PoolMeshExtras;
            FarLod.PoolArrays = config.PoolMeshExtras;
        }, config.TightCustomClones
            ? "compact custom-part clones (content-sized, not capacity-sized)"
            : "capacity-sized clones (vanilla); '.komet toggle tightclone' enables the compact ones live");

        // Always applied, gated at runtime: a "my windmill vanished" report must be bisectable
        // with '.komet toggle animcull' while it is on screen, and safemode switches it off.
        Patch(() =>
        {
            AnimatableCullPatches.Apply(harmony);
            AnimatableCullPatches.Enabled = config.CullAnimatableRenderers;
        }, config.CullAnimatableRenderers
            ? "animatable renderer frustum gate (animated block entities outside the stage's frustum are skipped)"
            : "animatable renderer frustum gate off (vanilla); '.komet toggle animcull' enables it live");

        // Both entity budgets reopen their window on the frame boundary, and only there. If
        // the measurement bracket above did not apply, that boundary never fires: the
        // tesselation budget would then skip EVERY entity shape for the rest of the session
        // (all animals invisible) and the load budget would hold every entity forever. A
        // feature whose reset is missing runs vanilla instead - the patches carry the same
        // rule at runtime (StaleAfterMs), this is the same decision one step earlier.
        if (config.EntityTesselationBudgetMs > 0 && RequireFrameBoundary("entity tesselation budget"))
            Patch(() =>
            {
                EntityTessPatches.Apply(harmony, config.EntityTesselationBudgetMs);
                HookFrameBoundary(EntityTessPatches.OnFrameBoundary);
            }, $"entity tesselation budget ({config.EntityTesselationBudgetMs:0.#} ms/frame)");

        // Always applied, gated at runtime: '.komet toggle entload' must be able to A/B the
        // deferred entity loading while a join flood is on screen. Off = flush everything
        // held and hand every packet straight back to vanilla.
        Patch(() =>
        {
            EntityLoadPatches.Log = msg => Mod.Logger.Warning(msg);
            EntityLoadPatches.Apply(harmony);
            EntityLoadPatches.BudgetMs = config.EntityLoadBudgetMs > 0 ? config.EntityLoadBudgetMs : 1.5;
            EntityLoadPatches.Enabled = config.EntityLoadBudgetMs > 0 && MeasurementPatches.FrameBoundaryLive;
            HookFrameBoundary(EntityLoadPatches.OnFrameBoundary);
            // the warm-up only has a window while entities are held
            AnimationWarmup.Log = msg => Mod.Logger.Warning(msg);
            AnimationWarmup.Enabled = config.EntityAnimationPrewarm && EntityLoadPatches.Enabled;
        }, config.EntityLoadBudgetMs <= 0
            ? "entity load budget off (vanilla: each entity finishes in its packet's task); '.komet toggle entload' enables it live"
            : MeasurementPatches.FrameBoundaryLive
                ? $"entity load budget ({config.EntityLoadBudgetMs:0.#} ms/frame, nearest entity first)"
                : "entity load budget off: without the frame measurement nothing would ever drain the held entities");

        // Always applied, gated at runtime ('.komet toggle minimap'): the transpiled cap
        // returns vanilla's 200 while disabled, so off is exactly vanilla.
        Patch(() =>
        {
            MinimapPatches.Apply(harmony);
            MinimapPatches.TargetMs = config.MinimapPieceBudgetMs > 0 ? config.MinimapPieceBudgetMs : 1.0;
            MinimapPatches.Enabled = config.MinimapPieceBudgetMs > 0;
            MinimapPatches.DirectUpload = config.MinimapDirectUpload;
        }, (config.MinimapPieceBudgetMs > 0
            ? $"minimap piece upload budget ({config.MinimapPieceBudgetMs:0.#} ms/tick, adaptive cap)"
            : "minimap piece upload unbudgeted (vanilla 200/tick); '.komet toggle minimap' enables the budget live")
           + (config.MinimapDirectUpload ? ", pieces composed by direct sub-image upload" : ", pieces composed by vanilla's framebuffer draw"));

        // Always applied, gated at runtime: the drain is a 1:1 transcription with a clock
        // around each task, and '.komet toggle mtt' hands it back to vanilla.
        Patch(() =>
        {
            MainThreadTaskPatches.Apply(harmony);
            MainThreadTaskPatches.Enabled = config.AttributeMainThreadTasks;
            MainThreadTaskPatches.BudgetMs = Math.Max(0, config.MainThreadTaskBudgetMs);
            HookFrameBoundary(MainThreadTaskPatches.EndFrame);
        }, (config.AttributeMainThreadTasks
            ? "main-thread task attribution (a 'draussen' hitch names its packet type)"
            : "main-thread task attribution off (vanilla drain); '.komet toggle mtt' enables it live")
           + (config.MainThreadTaskBudgetMs > 0
               ? $", drain budget {config.MainThreadTaskBudgetMs:0.#} ms/frame (remainder requeued in order)"
               : ", no drain budget (vanilla: everything queued runs in the frame)"));

        // Measurement only, and cheap: two timestamps per particle pool per frame, i.e. eight.
        // Particles were the last part of the frame this mod could not name a number for.
        Patch(() =>
        {
            ParticlePatches.Apply(harmony);
            ParticlePatches.Enabled = config.MeasureParticles;
            HookFrameBoundary(ParticlePatches.EndFrame);
        }, config.MeasureParticles
            ? "particle pools measured (physics and upload on the render thread, apart from the off-thread pickup)"
            : "particle pools unmeasured ('.komet toggle particles')");

        // Always applied, gated at runtime: the Before-stage loop is a 1:1 transcription with
        // clocks around its two halves; '.komet toggle entbefore' hands it back to vanilla,
        // '.komet toggle animlod' flips the reduced animation rate for far / shadow-only entities.
        Patch(() =>
        {
            EntityAnimPatches.Apply(harmony);
            EntityAnimPatches.Enabled = config.AttributeEntityBeforeStage;
            EntityAnimPatches.LodEnabled = config.EntityAnimationLod;
            EntityAnimPatches.FarBlocks = Math.Max(8, config.EntityAnimationFarBlocks);
            HitchLog.EntityFrameProvider = EntityAnimPatches.TopOfCurrentFrame;
            HookFrameBoundary(EntityAnimPatches.EndFrame);
        }, config.AttributeEntityBeforeStage
            ? (config.EntityAnimationLod
                ? $"entity before-stage attribution + animation LOD (shadow-only entities every 3rd frame, rendered beyond {config.EntityAnimationFarBlocks:0} blocks every 2nd)"
                : "entity before-stage attribution (animation LOD off; '.komet toggle animlod' enables it live)")
            : "entity before-stage untouched (vanilla loop); '.komet toggle entbefore' enables the attribution live");

        // Always applied, gated at runtime: default off since 1.42.2 (prime suspect for wrong
        // terrain AO - see KometConfig), and '.komet toggle prebuild' has to be able to switch
        // it back on without a restart, which a patch that was never applied cannot do.
        Patch(() =>
        {
            WindowPrebuilder.Log = msg => Mod.Logger.Notification(msg);
            WindowPipelinePatches.Apply(harmony, config.TesselationPipelineValidateFirstN);
            WindowPrebuilder.Enabled = config.TesselationWindowPipelining;
        }, config.TesselationWindowPipelining
            ? $"tesselation window pipelining (validate first {config.TesselationPipelineValidateFirstN})"
            : "tesselation window pipelining off (vanilla window build); '.komet toggle prebuild' enables it live");

        if (config.AnimationLookupWithoutAlloc)
        {
            Patch(() =>
            {
                harmony.CreateClassProcessor(typeof(AnimatorBaseCtorPatch)).Patch();

                var dropLower = new HarmonyMethod(AccessTools.Method(
                    typeof(AnimationPatches), nameof(AnimationPatches.DropToLowerInvariant)));

                harmony.Patch(AccessTools.Method(typeof(AnimatorBase), nameof(AnimatorBase.OnFrame)),
                    transpiler: dropLower);
                harmony.Patch(AccessTools.Method(typeof(AnimatorBase), nameof(AnimatorBase.GetAnimationState)),
                    transpiler: dropLower);
            }, "allocation free animation lookup");
        }

        if (config.AnimationCollisionBoxWithoutAlloc)
        {
            Patch(() =>
            {
                var replaceAny = new HarmonyMethod(AccessTools.Method(
                    typeof(AnimationPatches), nameof(AnimationPatches.ReplaceAnyWithLoop)));

                harmony.Patch(AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame)),
                    transpiler: replaceAny);
            }, "allocation free AdjustCollisionBox check");
        }

        // Always applied, gated at runtime: with the flag off the original glGetError runs
        // unchanged, and '.komet toggle glerror' can A/B the two per-frame driver syncs live.
        Patch(() =>
        {
            GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck;
            GlErrorPatches.Apply(harmony);
        }, config.SkipPerFrameGlErrorCheck
            ? "skip per frame glGetError"
            : "per frame glGetError kept (vanilla); '.komet toggle glerror' skips it live");
    }

    /// <summary>Register a per-frame counter and remember it, so <see cref="UntrackCounters"/>
    /// takes it out again - the counter list is static and outlives the world, so one left
    /// behind keeps advancing for the rest of the process and every rejoin adds another.</summary>
    private SmoothedCounter Track(Func<long> read)
    {
        var counter = FrameStats.TrackCounter(read);
        trackedCounters.Add(counter);
        return counter;
    }

    private void UntrackCounters()
    {
        foreach (var counter in trackedCounters) FrameStats.Untrack(counter);
        trackedCounters.Clear();
    }

    /// <summary>Subscribe to the frame boundary and remember the delegate, so
    /// <see cref="UnhookFrameBoundaries"/> can take that exact handler off at world leave.</summary>
    private void HookFrameBoundary(Action handler)
    {
        MeasurementPatches.FrameBoundary += handler;
        frameBoundaryHooks.Add(handler);
    }

    private void UnhookFrameBoundaries()
    {
        foreach (var handler in frameBoundaryHooks) MeasurementPatches.FrameBoundary -= handler;
        frameBoundaryHooks.Clear();
    }

    /// <summary>
    /// Guards a feature that can only be correct while the frame boundary fires. Says so once,
    /// in the same voice as <see cref="Patch"/>, and answers false so the caller stays on
    /// vanilla - which is a real loss of speed, and never a loss of correctness.
    /// </summary>
    private bool RequireFrameBoundary(string what)
    {
        if (MeasurementPatches.FrameBoundaryLive) return true;
        Mod.Logger.Warning(
            "'{0}' stays off: the frame measurement did not apply, so its per-frame budget would never reset. Running vanilla here.",
            what);
        return false;
    }

    /// <summary>
    /// A failed patch must never take the game down with it - the engine's internals can shift
    /// between point releases. Log it, skip that one optimisation, carry on.
    /// </summary>
    private void Patch(Action apply, string what)
    {
        try
        {
            apply();
            Mod.Logger.Notification("enabled: {0}", what);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not enable '{0}', running without it. This is safe but you lose the speedup.", what);
            Mod.Logger.Error(e);
        }
    }

    /// <summary>
    /// One camera sample per frame boundary. The player entity's client-side Pos follows the
    /// mouse look, so the delta between two boundaries is exactly what the camera did during
    /// the frame in between - which is the frame the hitch log is about to book. Null-safe
    /// throughout: in menus there is simply no sample.
    /// </summary>
    private void RunPatchGuard(bool engineCheck)
    {
        try
        {
            if (engineCheck && !PatchGuard.EngineChecked)
                PatchGuard.CheckEngine(Vintagestory.API.Config.GameVersion.LongGameVersion);
            PatchGuard.Scan();
            if (engineCheck)
            {
                if (PatchGuard.Findings.Count == 0)
                    Mod.Logger.Notification("patch collisions: none - nobody else patches komet's methods or komet's own code");
                else
                    Mod.Logger.Notification("patch collisions: {0} ({1} high) - '.komet conflicts' for the list",
                        PatchGuard.Findings.Count, PatchGuard.CountAt(PatchGuard.Severity.High));
            }
        }
        catch (Exception e)
        {
            // a guard that reports problems must never become one
            Mod.Logger.Notification("patch guard could not check ({0}), continuing without it", e.GetType().Name);
        }
    }

    private void SampleCameraForHitchLog()
    {
        // pause first: a pending hitch that spans the pause menu is dropped before the
        // camera sample would commit it
        HitchLog.NotePaused(capi?.IsGamePaused ?? false);
        // an Ortho-heavy hitch names the dialogs that were open - the string is only built
        // when such a hitch is pending
        if (HitchLog.PendingWantsDialogs) HitchLog.NoteDialogs(OpenDialogNames(capi));
        var pos = capi?.World?.Player?.Entity?.Pos;
        if (pos != null) HitchLog.NoteCamera(pos.Yaw, pos.Pitch, pos.X, pos.Y, pos.Z);
    }

    /// <summary>The non-HUD dialogs currently open, comma separated; null when none.</summary>
    internal static string OpenDialogNames(ICoreClientAPI api)
    {
        var open = api?.Gui?.OpenedGuis;
        if (open == null) return null;
        StringBuilder sb = null;
        foreach (var g in open)
        {
            var name = g?.GetType().Name;
            // the HUD elements are always "open"; the question is which real dialog was
            if (name == null || name.StartsWith("Hud", StringComparison.Ordinal)) continue;
            sb ??= new StringBuilder();
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }
        return sb?.ToString();
    }

    private static readonly AccessTools.FieldRef<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ClientEventManager> EventManagerRef =
        AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ClientEventManager>("eventManager");

    /// <summary>
    /// Forces the enlarged shadow framebuffers into existence once the engine lets it (see
    /// ShadowResPatches.TryForceRebuild). Never gives up: after two minutes of "not yet" the
    /// cadence drops to five seconds and the blocking reason is logged once. The old version
    /// abandoned the rebuild after 240 tries, and on 01.09. a whole session ran with the
    /// vanilla-sized map because the window was not ready for those two minutes - the log
    /// said "window never ready" and nothing about why. A minimised window through the
    /// loading phase is legitimate; a shadow map that silently stays small is not.
    /// </summary>
    private void FbRebuildTick(float dt)
    {
        var done = true;
        try
        {
            if (capi?.World is Vintagestory.Client.NoObf.ClientMain game
                && game.Platform is Vintagestory.Client.NoObf.ClientPlatformWindows platform)
                done = ShadowResPatches.TryForceRebuild(platform, msg => Mod.Logger.Notification(msg), out fbBlockedBy);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("shadow framebuffer rebuild failed, shadow map stays vanilla-sized:\n{0}", e);
        }
        if (fbRebuildListenerId < 0) return;
        if (done)
        {
            capi?.Event.UnregisterGameTickListener(fbRebuildListenerId);
            fbRebuildListenerId = -1;
            return;
        }
        if (++fbRebuildTries == 240)
        {
            Mod.Logger.Notification(
                "shadow map rebuild still pending after {0} tries ({1}) - keeps trying every 5 s, the map is vanilla-sized until then",
                fbRebuildTries, fbBlockedBy ?? "reason unknown");
            capi?.Event.UnregisterGameTickListener(fbRebuildListenerId);
            fbRebuildListenerId = capi?.Event.RegisterGameTickListener(FbRebuildTick, 5000) ?? -1;
        }
    }

    /// <summary>
    /// Keeps the tick listeners wrapped (they register throughout a session) - and unwraps
    /// them while the engine's own tick profiler is on, because that one names listeners by
    /// their handler's target type, which a wrapper would hide.
    /// </summary>
    private void WrapTickListeners()
    {
        try
        {
            if (capi?.World is not Vintagestory.Client.NoObf.ClientMain game) return;
            var manager = EventManagerRef(game);
            if (TickProfiler.Enabled && !Vintagestory.Client.ScreenManager.FrameProfiler.Enabled)
                TickProfiler.Wrap(manager);
            else
                TickProfiler.Unwrap(manager);
        }
        catch (Exception e)
        {
            TickProfiler.Enabled = false;
            Mod.Logger.Error("tick listener profiling failed, switching it off:\n{0}", e);
        }
    }

    /// <summary>
    /// Refreshes what the mods DO (patches, registered classes). Walks Harmony's registry and
    /// the class registry, so it runs on a slow tick and switches itself off rather than
    /// repeating a failure every ten seconds.
    /// </summary>
    // ---- the periodic listeners ------------------------------------------------------
    //
    // Named methods, not lambdas, and that is not a style choice. The tick profiler and the
    // hitch log name a listener after the method its delegate belongs to, and a lambda written
    // inside StartClientSide belongs to StartClientSide - so all six of these landed in ONE
    // bucket called "KometModSystem.StartClientSide()". A field log then shows that bucket at
    // 10-16 ms every ten seconds with no way to tell which of the six it was, which is the one
    // thing this mod's own instrument must not do to itself.

    private void InflowBrakeTick(float dt) => InflowBrake.Update(0.5);

    private void RewrapRenderersTick(float dt) => WrapRenderers();

    /// <summary>
    /// Budget for continuations a worker handed back to the main thread. Small on purpose:
    /// nothing in this mod posts heavy work here - a handoff is a texture upload or a state
    /// flip that needs the GL context or a non-thread-safe engine API - and a queue that grows
    /// is a queue that gets drained over the next few frames rather than in this one.
    /// </summary>
    private const double HandoffBudgetMs = 1.0;

    private void WorkerPoolFrame()
    {
        // The monitor turns a job's dedup key back into chunk coordinates, and the multipliers
        // only exist once a world does.
        if (JobScheduler.KeyMulX == 0)
        {
            var map = capi?.World?.BlockAccessor == null ? null : (capi.World as Vintagestory.Client.NoObf.ClientMain)?.WorldMap;
            if (map != null) { JobScheduler.KeyMulX = map.index3dMulX; JobScheduler.KeyMulZ = map.index3dMulZ; }
        }

        JobScheduler.DrainMain(HandoffBudgetMs);
    }

    /// <summary>
    /// The finished frame's bill, from the same summary the upload throttle reads. The GC pause
    /// is subtracted first for the same reason it is there: a pause freezes every thread at
    /// once, so it is not evidence that the pool is competing with the render thread, and
    /// shrinking the pool cannot shorten one.
    /// </summary>
    private static void WorkerPoolSummary(double frameMs, double avgFrameMs, double gcPauseMs, double uploadMs)
        => JobScheduler.Sample(Math.Max(0, frameMs - Math.Max(0, gcPauseMs)), avgFrameMs,
                               Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation);

    private void EdgeFlushTick(float dt) => EdgeCoalescePatches.Flush();

    private void StatsLogTick(float dt)
        => Mod.Logger.Notification(BuildStats().Replace("\n", " | "));

    /// <summary>Ticks of the mod inventory scan so far - see <see cref="ScanModsTick"/>.</summary>
    private int modScanTicks;

    /// <summary>
    /// The mod inventory scan, on a cadence that follows what it is looking for. It walks the
    /// same Harmony registry the patch guard does, at the same price per method, and what it
    /// finds only changes when a mod patches or registers something new - which happens at load
    /// and on first use. Ten seconds for the first three minutes, once a minute after that.
    /// </summary>
    private void ScanModsTick(float dt)
    {
        modScanTicks++;
        if (modScanTicks > 18 && modScanTicks % 6 != 0) return;
        ScanMods();
    }

    /// <summary>
    /// The guard's periodic scan, in slices.
    ///
    /// The full scan measured 12,6 ms on the render thread - the hitch log named it as soon as
    /// this listener had a name of its own - and every millisecond of that is Harmony rebuilding
    /// a patched method's serialised info, once per method. None of it has to happen in one
    /// frame: the guard exists to notice a lazily applied patch eventually, not within a frame.
    /// So each tick walks two milliseconds' worth and publishes when it reaches the end, which
    /// at ~150 patched methods is a completed scan every minute or so.
    ///
    /// The world-join scan stays whole (see guardFinalize): there the answer is wanted at once.
    /// </summary>
    private void PatchGuardTick(float dt)
    {
        try
        {
            PatchGuard.ScanSlice(budgetMs: 2.0);
        }
        catch (Exception e)
        {
            // a guard that reports problems must never become one
            Mod.Logger.Notification("patch guard could not check ({0}), continuing without it", e.GetType().Name);
        }
    }

    private void ScanMods()
    {
        if (!ModProfiler.Enabled) return;
        try
        {
            ModProfiler.ScanInventory();
        }
        catch (Exception e)
        {
            ModProfiler.Enabled = false;
            if (modScanListenerId >= 0)
            {
                capi?.Event.UnregisterGameTickListener(modScanListenerId);
                modScanListenerId = -1;
            }
            Mod.Logger.Error("mod inventory scan failed, mod profiling off:\n{0}", e);
        }
    }

    private void WrapRenderers()
    {
        WrapTickListeners();
        try
        {
            if (capi?.World is Vintagestory.Client.NoObf.ClientMain game)
                RendererProfiler.Wrap(EventManagerRef(game));
        }
        catch (Exception e)
        {
            RendererProfiler.Enabled = false;
            if (rewrapListenerId >= 0)
            {
                capi?.Event.UnregisterGameTickListener(rewrapListenerId);
                rewrapListenerId = -1;
            }
            Mod.Logger.Error("renderer profiling failed, switching it off:\n{0}", e);
        }
    }

    /// <summary>
    /// Takes every timing decorator back out of the dispatch lists. Used by the live toggle and
    /// the stress phase; failing here would strand wrappers, so it reports rather than swallows.
    /// </summary>
    private void UnwrapRenderers()
    {
        try
        {
            if (capi?.World is Vintagestory.Client.NoObf.ClientMain game)
                RendererProfiler.Unwrap(EventManagerRef(game),
                    keepBeforeAttribution: RendererProfiler.AttributeBeforeStage);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not unwrap the renderer profiler - it stays on:\n{0}", e);
        }
    }

    public override void Dispose()
    {
        if (StressTest.Running) StressTest.Stop("leaving the world");
        if (fbRebuildListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(fbRebuildListenerId);
            fbRebuildListenerId = -1;
        }
        if (edgeFlushListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(edgeFlushListenerId);
            edgeFlushListenerId = -1;
        }
        EdgeCoalescePatches.Reset(); // the world map is going away; pending marks with it
        EdgeRetessPriorityPatches.Reset(); // stats and sweep clock; queues die with the world
        MeshRecyclerPatches.Clear(); // held buffers must not outlive the world
        TightClonePatches.ClearPools(); // same for the pooled extras arrays
        FarLod.ClearPools();            // and the far LOD's output arrays
        // FrameBoundary and the counter list are static and survive the world, so everything
        // this session subscribed comes off again here.
        UnhookFrameBoundaries();
        ParticlePatches.Reset();
        HitchLog.EntityFrameProvider = null;
        HitchLog.TopTickListenerProvider = null;
        // held entities belong to the world that is going away; the cached game instance too
        EntityLoadPatches.Reset();
        EntityAnimPatches.Reset();
        MainThreadTaskPatches.Detach();
        UploadBudgetPatches.Unhook();
        FrameStats.FrameSummary -= UploadBudget.NotePressure;

        // published through statics the boundary handler above kept fresh; the next session
        // must not start on a disposed API or a dead world's camera
        FirepitPatches.Api = null;
        FirepitPatches.CameraPos = null;

        UntrackCounters();
        ShadowFootprintPatches.Reset();
        GpuPassProbe.Reset();
        PoolPassPatches.Reset();
        FarMeshPatches.Reset();  // nothing tracked, mode unknown
        SpatialPools.Reset();
        ChunkShaderSwap.Restore();     // a diagnostic never outlives the world; the context still exists here
        ShadowCullPatches.SkipFoliage = false; // a diagnostic never survives the world

        HitchLog.TopRendererProvider = null;
        HitchLog.Log = null;
        FrameStats.PeriodicSample -= TesselationStats.Sample;
        FrameStats.PeriodicSample -= ClientAllocPatches.Sample;
        ClientAllocPatches.Clear();
        FrameStats.PeriodicSample -= AllocSampler.Sample;
        AllocSampler.Stop();
        AllocSampler.Clear();
        if (capi != null) capi.Event.LeaveWorld -= OnLeaveWorldEarly;
        // belt and braces: normally already done by OnLeaveWorldEarly, but Dispose can also
        // come without a DestroyGameSession (mod reload), and both are idempotent
        TesselationPatches.Shutdown();
        WindowPrebuilder.Shutdown();
        // FrameSummary is static and survives the world: a rejoin that stacked a second
        // handler would move the pool's worker count twice per frame. Same rule, same reason
        // as every other consumer of it.
        FrameStats.FrameSummary -= WorkerPoolSummary;
        if (gcLatencyChanged)
        {
            try { GCSettings.LatencyMode = previousLatencyMode; } catch { /* nothing to salvage */ }
            gcLatencyChanged = false;
        }
        if (rewrapListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(rewrapListenerId);
            rewrapListenerId = -1;
            try
            {
                if (capi?.World is Vintagestory.Client.NoObf.ClientMain game)
                {
                    RendererProfiler.Unwrap(EventManagerRef(game));
                    TickProfiler.Unwrap(EventManagerRef(game));
                }
            }
            catch { /* the world is going away anyway */ }
        }
        if (inflowListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(inflowListenerId);
            inflowListenerId = -1;
            InflowBrake.Release();
            InflowBrake.Enabled = false;
        }
        if (statsListenerId >= 0) capi?.Event.UnregisterGameTickListener(statsListenerId);
        if (guardFinalize != null && capi != null)
        {
            capi.Event.LevelFinalize -= guardFinalize;
            guardFinalize = null;
        }
        if (guardListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(guardListenerId);
            guardListenerId = -1;
        }
        PatchGuard.Reset();
        if (foreignFinalize != null && capi != null)
        {
            capi.Event.LevelFinalize -= foreignFinalize;
            foreignFinalize = null;
        }
        if (foreignCallbackId >= 0)
        {
            capi?.Event.UnregisterCallback(foreignCallbackId);
            foreignCallbackId = -1;
        }
        if (foreignDialog != null)
        {
            try { foreignDialog.TryClose(); foreignDialog.Dispose(); }
            catch (Exception) { /* a dialog mid-teardown has nothing left to close */ }
            foreignDialog = null;
        }
        ForeignClient.Findings.Clear();
        if (gpuBegin != null && capi != null)
        {
            capi.Event.UnregisterRenderer(gpuBegin, EnumRenderStage.Before);
            capi.Event.UnregisterRenderer(gpuEnd, EnumRenderStage.Done);
            gpuBegin = null;
            gpuEnd = null;
            GpuFrameTimer.Enabled = false;
            FrameStats.PeriodicSample -= GpuBusy.Sample;
        }
        if (reclaimer != null && capi != null)
        {
            capi.Event.UnregisterRenderer(reclaimer, EnumRenderStage.Done);
            reclaimer = null;
        }
        if (hud != null && capi != null)
        {
            capi.Event.UnregisterRenderer(hud, EnumRenderStage.Ortho);
            hud.Dispose();
            hud = null;
        }
        if (modScanListenerId >= 0)
        {
            capi?.Event.UnregisterGameTickListener(modScanListenerId);
            modScanListenerId = -1;
        }
        if (modHud != null && capi != null)
        {
            capi.Event.UnregisterRenderer(modHud, EnumRenderStage.Ortho);
            modHud.Dispose();
            modHud = null;
        }
        // The window owns a cairo surface and a GL texture per panel; closing it first makes
        // sure they go through the dialog's own teardown rather than being dropped on the
        // finaliser after the GL context is gone.
        if (window != null)
        {
            window.TryClose();
            window.Dispose();
            window = null;
        }
        // The toggle table closes over config and over this instance; a rejoin builds a new one.
        toggles = null;
        // The index holds an entry per mod and a cache of every type ever resolved - none of
        // it survives the session it was built for.
        ModProfiler.Clear();
        harmony?.UnpatchAll(harmony.Id);
        base.Dispose();
    }

    /// <summary>The engine rebuilt its programs: ours is rebuilt from the new one on the next shadow pass.</summary>
    private bool OnShadersReloaded()
    {
        ShadowCullPatches.OnShadersReloaded();
        return true;
    }

    /// <summary>
    /// Runs at the very start of DestroyGameSession, before the client threads are told to
    /// exit. Everything that touches world data from a background thread stands down here:
    /// the tesselation guard flips (its per-chunk prefix then drains the in-flight tick as
    /// no-ops within the engine's 200 ms window), the prefetcher and the window prebuilder
    /// stop. From Dispose this was provably too late - the teardown NRE recurred there.
    /// </summary>
    private void OnLeaveWorldEarly()
    {
        TesselationPatches.Shutdown();
        WindowPrebuilder.Shutdown();
        // Every queued job that names a chunk belongs to the world being torn down: running it
        // against the next one would read chunk data that has already been freed, which is the
        // exact shape of the teardown NRE the per-chunk tesselation guard exists for. The pool
        // keeps its threads - only the work is dropped - and the key multipliers go with the
        // map they decode.
        JobScheduler.CancelKind(JobKind.MeshPrep);
        JobScheduler.CancelKind(JobKind.ChunkPrep);
        JobScheduler.KeyMulX = 0;
        JobScheduler.KeyMulZ = 0;
        // the depth-only shadow program was compiled for this world's context
        try { capi?.Event.ReloadShader -= OnShadersReloaded; } catch (Exception) { /* event api gone */ }
        ShadowCullPatches.Game = null;
        ShadowCullPatches.OnShadersReloaded();
        // entities still held for the load budget were meant for this world; finishing them
        // into a disposing session would register renderers on a dying event manager
        EntityLoadPatches.Reset();
        EntityAnimPatches.Reset();
        // the join warning belongs to the world that is ending: a callback still pending
        // must not fire into the teardown, and the dialog was that session's
        if (foreignCallbackId >= 0)
        {
            try { capi?.Event.UnregisterCallback(foreignCallbackId); }
            catch (Exception) { /* an event manager already gone has nothing to unregister */ }
            foreignCallbackId = -1;
        }
        foreignDialog = null;
    }
}
