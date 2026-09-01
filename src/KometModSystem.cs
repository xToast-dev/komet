using System;
using System.Runtime;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Komet.Measure;

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
    private SmoothedCounter partsPerFrame, rawRangesPerFrame, rangesPerFrame, bridgedPerFrame, sweepsPerFrame, batchesPerFrame, rebuildsPerFrame, cellsSkippedPerFrame, rebuildTicksPerFrame;
    private PoolReclaimer.Renderer reclaimer;
    private GpuFrameTimer.BeginRenderer gpuBegin;
    private GpuFrameTimer.EndRenderer gpuEnd;
    private long inflowListenerId = -1;
    private long rewrapListenerId = -1;
    private long edgeFlushListenerId = -1;
    private long fbRebuildListenerId = -1;
    private int fbRebuildTries;
    private Action cameraSampler;
    private Action firepitBoundary;
    private bool uploadBudgetHooked;
    private bool entityTessHooked;
    private bool rendererProfilerHooked;

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
        FastCuller.MaxThreads = config.CullingThreads;
        FastCuller.MergeDrawRanges = config.MergeDrawRanges;
        FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges;
        FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod;
        FastChunkCuller.MaxThreads = config.OcclusionCullingThreads;
        FastChunkCuller.MinIntervalMs = config.OcclusionMinIntervalMs;
        UploadBudget.TargetMs = config.UploadBudgetTargetMs;
        UploadBudget.Enabled = config.AdaptiveUploadBudget;
        UploadBudget.FramePressureInput = config.UploadFramePressure;
        FastCuller.MeasureTime = config.MeasureCullTime;
        FastCuller.VectorCulling = config.VectorCulling && FastCuller.VectorAvailable;
        FastCuller.Log = msg => Mod.Logger.Notification(msg);

        // Both sweeps get their own threads rather than the shared ThreadPool, which the game
        // also queues chunk tesselation on. Started here, before the first frame, so no frame
        // pays for the thread creation.
        FastCuller.PartsPerCellTarget = config.PartsPerCellTarget;
        FastChunkCuller.Niceness = config.OcclusionThreadNiceness;
        if (config.ParallelCulling) FastCuller.StartWorkers();
        FastChunkCuller.StartWorkers();
        Patches.RetessSourcePatches.SampleSources = config.SampleRetessSources;
        CullVerifier.SampleEvery = config.VerifyCullSweepEvery;
        // Warning, not Notification: a disagreement here means the screen is wrong, and it has
        // to stand out in a log that scrolls past at a hundred lines a second.
        CullVerifier.Log = msg => Mod.Logger.Warning(msg);

        ApplyGcLatencyMode();

        harmony = new Harmony(Mod.Info.ModID);
        ApplyPatches(api);
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

        // reading and flipping allowPStorage needs the platform, which only exists client side
        Patch(() => Patches.PersistentMappingPatch.Probe(Mod.Logger), "persistent mapping probe");
        if (config.ExperimentalPersistentMapping)
            Patch(() => Patches.PersistentMappingPatch.Enable(Mod.Logger), "experimental persistent mapping");

        if (config.MeasureGpuTime)
            Patch(() =>
            {
                GpuFrameTimer.Enabled = true;
                gpuBegin = new GpuFrameTimer.BeginRenderer();
                gpuEnd = new GpuFrameTimer.EndRenderer();
                api.Event.RegisterRenderer(gpuBegin, EnumRenderStage.Before, "kometgpu0");
                api.Event.RegisterRenderer(gpuEnd, EnumRenderStage.Done, "kometgpu1");
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
                inflowListenerId = api.Event.RegisterGameTickListener(_ => InflowBrake.Update(0.5), 500);
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
            Patches.RendererProfiler.ApplyUnregisterFix(harmony);

            // Wrapping needs the event manager, which only exists once the client is up.
            // Renderers register throughout a session, so this is repeated periodically -
            // a renderer registered in the gap simply goes unmeasured until the next pass.
            MeasurementPatches.FrameBoundary += Patches.RendererProfiler.EndFrame;
            rendererProfilerHooked = true;
            rewrapListenerId = api.Event.RegisterGameTickListener(_ => WrapRenderers(), 250);

            // The hitch log asks for this at detection time - inside the frame boundary,
            // before EndFrame folds and clears the per-frame ticks - so a hitch frame can
            // name its most expensive renderer, not just its stage.
            HitchLog.TopRendererProvider = Patches.RendererProfiler.TopOfCurrentFrame;

            Patches.RendererProfiler.Enabled = config.ProfileRenderers;
            // The Before stage (a handful of system renderers: entities, chunk uploads,
            // liquid depth, camera ...) stays attributed even with the profiler off - it is
            // where the unnamed world-join bursts live, and naming a hitch must not depend
            // on having armed the profiler before it happened.
            Patches.RendererProfiler.AttributeBeforeStage = config.AttributeBeforeStage;
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
        cameraSampler = SampleCameraForHitchLog;
        MeasurementPatches.FrameBoundary += cameraSampler;

        // Both halves, because they can disagree: DOTNET_gcServer only reaches the process
        // through the desktop icon or vs-launch.sh, so a request that never arrived looks
        // exactly like one that was never made. Which mode is the right one is an open
        // measurement - see HitchLog.GcModeVerdict.
        Mod.Logger.Notification("gc-modus: {0} (angefordert: {1}), latenz {2}",
            System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
            Environment.GetEnvironmentVariable("DOTNET_gcServer") ?? "nichts gesetzt",
            System.Runtime.GCSettings.LatencyMode);

        partsPerFrame = FrameStats.TrackCounter(() => FastCuller.StatPartsTested);
        cellsSkippedPerFrame = FrameStats.TrackCounter(() => FastCuller.StatCellsSkipped);
        rebuildTicksPerFrame = FrameStats.TrackCounter(() => FastCuller.StatRebuildTicks);
        rebuildsPerFrame = FrameStats.TrackCounter(() => FastCuller.StatRebuilds);
        rawRangesPerFrame = FrameStats.TrackCounter(() => FastCuller.StatRangesRaw);
        rangesPerFrame = FrameStats.TrackCounter(() => FastCuller.StatRangesEmitted);
        bridgedPerFrame = FrameStats.TrackCounter(() => FastCuller.StatRangesBridged);
        sweepsPerFrame = FrameStats.TrackCounter(() => FastCuller.StatSweeps);
        batchesPerFrame = FrameStats.TrackCounter(() => FastCuller.StatBatches);

        DebugHud.BackgroundRaster = config.HudBackgroundRaster;
        hud = new DebugHud(api, "komet " + KometVersion.Display(Mod.Info.Version))
        {
            Visible = config.DebugHudVisible,
            Compact = true,
            ExtraSection = WriteKometSection,
            ExtraCompactSection = WriteKometWarnings
        };
        api.Event.RegisterRenderer(hud, EnumRenderStage.Ortho, "komethud");

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

        // The enlarged shadow framebuffers have to be forced into existence: the engine builds
        // its framebuffers at window creation, before any mod loads, so the transpiler alone
        // reaches nothing on a normal launch (see ShadowResPatches.TryForceRebuild). Retried on
        // a slow tick because the engine suppresses buffer rebuilds while it is still loading.
        if (config.ShadowMapExtraQuality > 0)
            fbRebuildListenerId = api.Event.RegisterGameTickListener(_ =>
            {
                var done = true;
                try
                {
                    if (capi?.World is Vintagestory.Client.NoObf.ClientMain game
                        && game.Platform is Vintagestory.Client.NoObf.ClientPlatformWindows platform)
                        done = Patches.ShadowResPatches.TryForceRebuild(platform, msg => Mod.Logger.Notification(msg));
                }
                catch (Exception e)
                {
                    Mod.Logger.Error("shadow framebuffer rebuild failed, shadow map stays vanilla-sized:\n{0}", e);
                }
                // 240 tries = two minutes: a window minimised through the whole loading
                // phase (alt-tab in fullscreen) is now a legitimate reason to keep waiting,
                // not an error. Giving up is harmless - the shadow map stays vanilla-sized -
                // but it must say so, or a HUD reading "5120px" looks like a broken patch.
                if ((done || ++fbRebuildTries > 240) && fbRebuildListenerId >= 0)
                {
                    if (!done)
                        Mod.Logger.Notification(
                            "shadow map rebuild abandoned after {0} tries (window never ready) - map stays vanilla-sized this session", fbRebuildTries - 1);
                    capi?.Event.UnregisterGameTickListener(fbRebuildListenerId);
                    fbRebuildListenerId = -1;
                }
            }, 500);

        // 50 ms cadence: together with the per-tick cap this sets the drain capacity
        // (~5000/s baseline, more in catch-up mode) - it must exceed any realistic inflow.
        // Registered even when the feature is off, so the live toggle has a flusher.
        edgeFlushListenerId = api.Event.RegisterGameTickListener(_ => Patches.EdgeCoalescePatches.Flush(), 50);

        RegisterCommands(api);
        MeasurementPatches.FrameBoundary += StressTest.OnFrameBoundary;

        if (config.StatsLogIntervalSeconds > 0)
        {
            statsListenerId = api.Event.RegisterGameTickListener(
                _ => Mod.Logger.Notification(BuildStats().Replace("\n", " | ")),
                config.StatsLogIntervalSeconds * 1000);
        }
    }

    private void ApplyPatches(ICoreAPI api)
    {
        if (config.FastFrustumCulling)
            Patch(() =>
            {
                FastCuller.EnsureReady();
                harmony.CreateClassProcessor(typeof(Patches.MeshDataPoolPatches)).Patch();
            }, "fast frustum culling");

        if (config.FastOcclusionCulling)
            Patch(() =>
            {
                FastChunkCuller.EnsureReady();
                harmony.CreateClassProcessor(typeof(Patches.ChunkCullerPatches)).Patch();
            }, "parallel occlusion culling");

        if (config.BulkMeshUpload || config.ExperimentalPersistentMapping)
            Patch(() => Patches.MeshUploadPatches.Apply(harmony), "bulk chunk mesh upload");

        // Must run before the framebuffers are built, i.e. before the first frame.
        if (config.ShadowMapExtraQuality > 0)
            Patch(() => Patches.ShadowResPatches.Apply(harmony, config.ShadowMapExtraQuality),
                  $"shadow map +{config.ShadowMapExtraQuality} quality step(s)");

        // Always applied, gated at runtime, so a shadow artefact can be bisected while it is
        // on screen ('.komet toggle shadowbox|shadowfade|shadowdist', and safemode).
        Patch(() => Patches.ShadowPatches.Apply(harmony, config.FixShadowFadeCutoff, config.ShadowDistanceMultiplier, config.SymmetricShadowBox),
              $"shadow patches (fade fix {(config.FixShadowFadeCutoff ? "on" : "off")}, "
              + $"distance x{config.ShadowDistanceMultiplier:0.##}, symmetric box {(config.SymmetricShadowBox ? "on" : "off")})");

        // measurement first: the throttle and the HUD both hang off it
        Patch(() => MeasurementPatches.Apply(harmony), "frame + render stage measurement");

        if (config.SunOcclusionQueryInterval > 1)
            Patch(() => Patches.SunQueryPatches.Apply(harmony, config.SunOcclusionQueryInterval),
                  $"sun occlusion query every {config.SunOcclusionQueryInterval} frames");

        if (config.StabiliseShadowTexels)
            Patch(() => Patches.ShadowStabilityPatches.Apply(harmony), "shadow texel snapping");

        // Always applied, gated at runtime by the interval values (1/1 = exactly vanilla), so
        // '.komet toggle shadowthrottle' can bisect a shadow artefact live.
        Patch(() => Patches.ShadowThrottlePatches.Apply(harmony,
                        config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval,
                        config.ShadowFarMaxSkip, config.ShadowFarMoveThreshold),
              config.ShadowFarUpdateInterval > 1 || config.ShadowFarMaxSkip > 1 || config.ShadowNearUpdateInterval > 1
                  ? $"adaptive shadow throttling (far every {config.ShadowFarUpdateInterval}-{Math.Max(config.ShadowFarUpdateInterval, config.ShadowFarMaxSkip)} frames, "
                    + $"near every {config.ShadowNearUpdateInterval})"
                  : "shadow throttling off (far cascade every frame); '.komet toggle shadowthrottle' enables it live");

        if (config.AdaptiveUploadBudget)
            Patch(() =>
            {
                Patches.UploadBudgetPatches.Apply(harmony);
                // the frame-pressure input: the finished frame's totals reach the
                // controller each boundary, so it can see the deferred driver cost the
                // upload clock is blind to under mesa_glthread
                FrameStats.FrameSummary += UploadBudget.NotePressure;
                uploadBudgetHooked = true;
            }, "adaptive chunk upload budget");

        // Always applied, gated at runtime: whether a relight storm's uploads may be spread
        // over frames is exactly the kind of question '.komet toggle prioupload' answers live.
        Patch(() =>
        {
            Patches.PrioUploadPatches.Apply(harmony);
            Patches.PrioUploadPatches.Enabled = config.BudgetPriorityUploads;
        }, config.BudgetPriorityUploads
            ? "priority chunk upload budget (storms spread over frames, player edits unaffected)"
            : "priority chunk uploads unbudgeted (vanilla); '.komet toggle prioupload' enables the budget live");

        if (config.TesselationNoIdleSleep || config.TesselationThreadPriority || config.TesselationNeighbourPrefetch)
            Patch(() => Patches.TesselationPatches.Apply(harmony,
                            config.TesselationNoIdleSleep, config.TesselationThreadPriority,
                            config.TesselationNeighbourPrefetch),
                  "faster chunk loading (no idle sleep: " + config.TesselationNoIdleSleep
                  + ", thread priority: " + config.TesselationThreadPriority
                  + ", neighbour prefetch: " + config.TesselationNeighbourPrefetch + ")");

        if (config.FirepitContentsMaxDistance > 0 || config.FirepitLightCacheMs > 0)
            Patch(() =>
            {
                Patches.FirepitPatches.Log = msg => Mod.Logger.Warning(msg);
                Patches.FirepitPatches.Apply(harmony, config.FirepitContentsMaxDistance, config.FirepitLightCacheMs);
                // held in a field so Dispose can take exactly this handler off the static
                // event again - an anonymous lambda would be unremovable, and the stale
                // closure would keep publishing the previous session's camera and API
                firepitBoundary = () =>
                {
                    Patches.FirepitPatches.CameraPos = capi?.World?.Player?.Entity?.CameraPos;
                    Patches.FirepitPatches.Api ??= capi;
                };
                MeasurementPatches.FrameBoundary += firepitBoundary;
            }, $"firepit contents gate (beyond {config.FirepitContentsMaxDistance} blocks, "
             + $"light cache {config.FirepitLightCacheMs} ms)");

        if (config.MeasureRetessSources)
            Patch(() => Patches.RetessSourcePatches.Apply(harmony), "dirty-mark source sampling");

        // Always applied but runtime-gated: default off since 1.36.0 (stress test measured
        // a small cost, and it was twice prime suspect for border holes on fresh terrain);
        // '.komet toggle edgecoal' switches the held-back marking on live for experiments.
        Patch(() =>
        {
            Patches.EdgeCoalescePatches.Log = msg => Mod.Logger.Warning(msg);
            Patches.EdgeCoalescePatches.Apply(harmony,
                config.EdgeRetessCoalesceMs > 0 ? config.EdgeRetessCoalesceMs : 400);
            Patches.EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0;
        }, config.EdgeRetessCoalesceMs > 0
            ? $"edge retess coalescing ({config.EdgeRetessCoalesceMs:0} ms window)"
            : "edge retess coalescing off (vanilla marking); '.komet toggle edgecoal' enables it live");

        // Always applied but runtime-gated: whether border holes close sooner is judged by
        // eye at the load front, so '.komet toggle edgeprio' must be able to A/B it live.
        Patch(() =>
        {
            Patches.EdgeRetessPriorityPatches.Log = msg => Mod.Logger.Warning(msg);
            Patches.EdgeRetessPriorityPatches.Apply(harmony);
            Patches.EdgeRetessPriorityPatches.Enabled = config.EdgeRetessPriority;
        }, config.EdgeRetessPriority
            ? "edge retess priority (visible border repairs jump the tesselation queue)"
            : "edge retess priority off (vanilla order); '.komet toggle edgeprio' enables it live");

        // Always applied, gated at runtime: the gate decides whose storage answers the
        // recycler's API, and '.komet toggle recycler' must be able to A/B it while the
        // GC counters are on screen. Enabling hands vanilla's held buffers over (on the
        // tesselation thread, the only place that may touch them), disabling frees ours.
        Patch(() =>
        {
            Patches.MeshRecyclerPatches.BudgetMb = config.MeshRecyclerBudgetMb;
            Patches.MeshRecyclerPatches.Apply(harmony);
            Patches.MeshRecyclerPatches.SetEnabled(config.FastMeshRecycler);
        }, config.FastMeshRecycler
            ? $"mesh recycler size-class pool ({config.MeshRecyclerBudgetMb} MB budget)"
            : "mesh recycler pool off (vanilla storage); '.komet toggle recycler' enables it live");

        // Always applied, gated at runtime, same reasoning as the recycler: the A/B against
        // the GC counters must work live.
        Patch(() =>
        {
            Patches.TightClonePatches.Apply(harmony);
            Patches.TightClonePatches.Enabled = config.TightCustomClones;
            Patches.TightClonePatches.PoolExtras = config.PoolMeshExtras;
        }, config.TightCustomClones
            ? "compact custom-part clones (content-sized, not capacity-sized)"
            : "capacity-sized clones (vanilla); '.komet toggle tightclone' enables the compact ones live");

        // Always applied, gated at runtime: a "my windmill vanished" report must be bisectable
        // with '.komet toggle animcull' while it is on screen, and safemode switches it off.
        Patch(() =>
        {
            Patches.AnimatableCullPatches.Apply(harmony);
            Patches.AnimatableCullPatches.Enabled = config.CullAnimatableRenderers;
        }, config.CullAnimatableRenderers
            ? "animatable renderer frustum gate (animated block entities outside the stage's frustum are skipped)"
            : "animatable renderer frustum gate off (vanilla); '.komet toggle animcull' enables it live");

        if (config.EntityTesselationBudgetMs > 0)
            Patch(() =>
            {
                Patches.EntityTessPatches.Apply(harmony, config.EntityTesselationBudgetMs);
                MeasurementPatches.FrameBoundary += Patches.EntityTessPatches.OnFrameBoundary;
                entityTessHooked = true;
            }, $"entity tesselation budget ({config.EntityTesselationBudgetMs:0.#} ms/frame)");

        // Always applied, gated at runtime: default off since 1.42.2 (prime suspect for wrong
        // terrain AO - see KometConfig), and '.komet toggle prebuild' has to be able to switch
        // it back on without a restart, which a patch that was never applied cannot do.
        Patch(() =>
        {
            WindowPrebuilder.Log = msg => Mod.Logger.Notification(msg);
            Patches.WindowPipelinePatches.Apply(harmony, config.TesselationPipelineValidateFirstN);
            WindowPrebuilder.Enabled = config.TesselationWindowPipelining;
        }, config.TesselationWindowPipelining
            ? $"tesselation window pipelining (validate first {config.TesselationPipelineValidateFirstN})"
            : "tesselation window pipelining off (vanilla window build); '.komet toggle prebuild' enables it live");

        if (config.AnimationLookupWithoutAlloc)
        {
            Patch(() =>
            {
                harmony.CreateClassProcessor(typeof(Patches.AnimatorBaseCtorPatch)).Patch();

                var dropLower = new HarmonyMethod(AccessTools.Method(
                    typeof(Patches.AnimationPatches), nameof(Patches.AnimationPatches.DropToLowerInvariant)));

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
                    typeof(Patches.AnimationPatches), nameof(Patches.AnimationPatches.ReplaceAnyWithLoop)));

                harmony.Patch(AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame)),
                    transpiler: replaceAny);
            }, "allocation free AdjustCollisionBox check");
        }

        // Always applied, gated at runtime: with the flag off the original glGetError runs
        // unchanged, and '.komet toggle glerror' can A/B the two per-frame driver syncs live.
        Patch(() =>
        {
            Patches.GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck;
            Patches.GlErrorPatches.Apply(harmony);
        }, config.SkipPerFrameGlErrorCheck
            ? "skip per frame glGetError"
            : "per frame glGetError kept (vanilla); '.komet toggle glerror' skips it live");
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
    private void SampleCameraForHitchLog()
    {
        var pos = capi?.World?.Player?.Entity?.Pos;
        if (pos != null) HitchLog.NoteCamera(pos.Yaw, pos.Pitch, pos.X, pos.Y, pos.Z);
    }

    private static readonly AccessTools.FieldRef<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ClientEventManager> EventManagerRef =
        AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ClientEventManager>("eventManager");

    private void WrapRenderers()
    {
        try
        {
            if (capi?.World is Vintagestory.Client.NoObf.ClientMain game)
                Patches.RendererProfiler.Wrap(EventManagerRef(game));
        }
        catch (Exception e)
        {
            Patches.RendererProfiler.Enabled = false;
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
                Patches.RendererProfiler.Unwrap(EventManagerRef(game),
                    keepBeforeAttribution: Patches.RendererProfiler.AttributeBeforeStage);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not unwrap the renderer profiler - it stays on:\n{0}", e);
        }
    }

    public override void Dispose()
    {
        if (StressTest.Running) StressTest.Stop("welt wird verlassen");
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
        Patches.EdgeCoalescePatches.Reset(); // the world map is going away; pending marks with it
        Patches.EdgeRetessPriorityPatches.Reset(); // stats and sweep clock; queues die with the world
        Patches.MeshRecyclerPatches.Clear(); // held buffers must not outlive the world
        Patches.TightClonePatches.ClearPools(); // same for the pooled extras arrays
        if (cameraSampler != null)
        {
            MeasurementPatches.FrameBoundary -= cameraSampler;
            cameraSampler = null;
        }

        // FrameBoundary and the counter list are static and survive the world: everything
        // this session subscribed has to come off again, or every rejoin stacks another set
        // of handlers - a doubled RendererProfiler.EndFrame folds each entry twice per frame
        // (halving every average), a doubled StressTest tick sees half-length frames, and a
        // doubled upload FrameEnd squares the budget controller's correction.
        MeasurementPatches.FrameBoundary -= StressTest.OnFrameBoundary;
        if (rendererProfilerHooked)
        {
            MeasurementPatches.FrameBoundary -= Patches.RendererProfiler.EndFrame;
            rendererProfilerHooked = false;
        }
        if (entityTessHooked)
        {
            MeasurementPatches.FrameBoundary -= Patches.EntityTessPatches.OnFrameBoundary;
            entityTessHooked = false;
        }
        if (firepitBoundary != null)
        {
            MeasurementPatches.FrameBoundary -= firepitBoundary;
            firepitBoundary = null;
        }
        if (uploadBudgetHooked)
        {
            Patches.UploadBudgetPatches.Unhook();
            FrameStats.FrameSummary -= UploadBudget.NotePressure;
            uploadBudgetHooked = false;
        }

        // published through statics the boundary handler above kept fresh; the next session
        // must not start on a disposed API or a dead world's camera
        Patches.FirepitPatches.Api = null;
        Patches.FirepitPatches.CameraPos = null;

        FrameStats.Untrack(partsPerFrame);
        FrameStats.Untrack(cellsSkippedPerFrame);
        FrameStats.Untrack(rebuildTicksPerFrame);
        FrameStats.Untrack(rebuildsPerFrame);
        FrameStats.Untrack(rawRangesPerFrame);
        FrameStats.Untrack(rangesPerFrame);
        FrameStats.Untrack(bridgedPerFrame);
        FrameStats.Untrack(sweepsPerFrame);
        FrameStats.Untrack(batchesPerFrame);

        HitchLog.TopRendererProvider = null;
        HitchLog.Log = null;
        if (capi != null) capi.Event.LeaveWorld -= OnLeaveWorldEarly;
        // belt and braces: normally already done by OnLeaveWorldEarly, but Dispose can also
        // come without a DestroyGameSession (mod reload), and both are idempotent
        Patches.TesselationPatches.Shutdown();
        WindowPrebuilder.Shutdown();
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
                    Patches.RendererProfiler.Unwrap(EventManagerRef(game));
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
        if (gpuBegin != null && capi != null)
        {
            capi.Event.UnregisterRenderer(gpuBegin, EnumRenderStage.Before);
            capi.Event.UnregisterRenderer(gpuEnd, EnumRenderStage.Done);
            gpuBegin = null;
            gpuEnd = null;
            GpuFrameTimer.Enabled = false;
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
        harmony?.UnpatchAll(harmony.Id);
        base.Dispose();
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
        Patches.TesselationPatches.Shutdown();
        WindowPrebuilder.Shutdown();
    }
}
