using System;
using System.Globalization;
using Komet.Culling;
using Komet.Measure;
using Komet.Patches;
using Komet.Runtime;

namespace Komet;

/// <summary>
/// The runtime toggles, declared once.
///
/// Every entry below used to be a <c>case</c> in <c>ToggleSystem</c>, and that was the right
/// shape while the chat command was the only caller. The window is a second caller, and the
/// thing a second caller must not do is keep its own list: a hand-written list in the GUI would
/// have stopped covering the system added after it - which is always the one being asked about.
/// So the switch became this table. The chat command looks its argument up here, the window
/// draws the same table, and <see cref="ToggleEntry.Flip"/> returns the very sentence the chat
/// has always printed, so the two surfaces cannot describe one flip differently.
///
/// What did NOT move here: <c>BuildStressPhases</c>. A phase is not a flip - it enters a named
/// state and leaves to whatever komet.json asked for, and half the phases enter the state that
/// is already the default so the delta reads as what the feature saves. Folding the two lists
/// into one would make both worse.
/// </summary>
public partial class KometModSystem
{
    private ToggleRegistry toggles;

    /// <summary>
    /// verify: an instance carrying a config and nothing else - no game, no harmony, no
    /// renderers. Enough to exercise the toggle table and every page of the window, which is
    /// exactly why it exists: those two are the surfaces a player drives, and until they could
    /// be built without a game the only way to find out that a page throws was to open it.
    /// </summary>
    internal static KometModSystem ForTest(KometConfig cfg)
        => new() { config = cfg };

    /// <summary>Built on demand: the lambdas close over <see cref="config"/>, which exists from
    /// StartPre onwards, and over instance methods that need the client.</summary>
    internal ToggleRegistry Toggles => toggles ??= BuildToggles();

    private ToggleRegistry BuildToggles()
    {
        var t = new ToggleRegistry();
        // One culture snapshot for the whole table, as before the table was split up.
        var ci = CultureInfo.CurrentCulture;

        AddCulling(t);
        AddRendering(t);
        AddShadows(t, ci);
        AddChunks(t, ci);
        AddEntities(t, ci);
        AddMemory(t);
        AddServer(t);
        AddDiagnostics(t);
        return t;
    }

    /// <summary>The visibility sweep.</summary>
    private void AddCulling(ToggleRegistry t)
    {
        t.AddFlag("cull", ToggleGroup.Culling, "visibility sweep",
            () => FastCuller.Enabled, v => FastCuller.Enabled = v,
            "ON", "OFF (vanilla)",
            visual: true);

        t.AddFlag("simd", ToggleGroup.Culling, "sweep vector kernel",
            () => FastCuller.VectorCulling, v => FastCuller.VectorCulling = v,
            "ON (4 parts per instruction)", "OFF (scalar, one part per instruction)",
            unavailable: () => FastCuller.VectorAvailable
                ? null
                : Loc.T("komet:msg-no-avx",
                    "this CPU has no AVX - the sweep runs scalar anyway."));

        t.AddFlag("gapmerge", ToggleGroup.Culling, "draw range gap merging",
            () => FastCuller.GapMergeDrawRanges, v => FastCuller.GapMergeDrawRanges = v,
            "ON (ranges span frustum-clipped parts)", "OFF (only seamlessly adjacent ranges)",
            sentence: "gap merging");

        t.AddFlag("occlusion", ToggleGroup.Culling, "occlusion culling",
            () => FastChunkCuller.Enabled, v => FastChunkCuller.Enabled = v,
            "ON", "OFF (vanilla)",
            visual: true);

        t.Add("cellsize", ToggleGroup.Culling, "grid cell target 160",
            () => FastCuller.PartsPerCellTarget != DefaultCellTarget,
            () =>
            {
                SetCellTarget(FastCuller.PartsPerCellTarget == DefaultCellTarget ? 160 : DefaultCellTarget);
                return "grid cell target now " + FastCuller.PartsPerCellTarget + " parts per cell";
            });
    }

    /// <summary>What else is drawn.</summary>
    private void AddRendering(ToggleRegistry t)
    {
        t.AddFlag("firepit", ToggleGroup.Rendering, "firepit gate",
            () => FirepitPatches.Enabled, v => FirepitPatches.Enabled = v,
            "ON", "OFF (vanilla)",
            visual: true);

        t.AddFlag("animcull", ToggleGroup.Rendering, "animatable frustum gate",
            () => AnimatableCullPatches.Enabled, v => AnimatableCullPatches.Enabled = v,
            "ON (animated block entities outside the frustum are skipped)",
            "OFF (vanilla: every instance draws in every stage)",
            visual: true);

        t.Add("sunquery", ToggleGroup.Rendering, "sun query throttle",
            () => SunQueryPatches.Interval > 1,
            () =>
            {
                SunQueryPatches.Interval = SunQueryPatches.Interval > 1 ? 1 : config.SunOcclusionQueryInterval;
                return "sun query throttle " + (SunQueryPatches.Interval > 1 ? "ON" : "OFF (every frame)");
            }, visual: true);

        t.AddFlag("glerror", ToggleGroup.Rendering, "glGetError skip",
            () => GlErrorPatches.SkipEnabled, v => GlErrorPatches.SkipEnabled = v,
            "ON (2 driver syncs/frame saved, VRAM warning off)", "OFF (vanilla)",
            visual: true);
    }

    /// <summary>Shadows.</summary>
    private void AddShadows(ToggleRegistry t, CultureInfo ci)
    {
        t.AddFlag("shadowbox", ToggleGroup.Shadows, "symmetric shadow box",
            () => ShadowPatches.SymmetricBox, v => ShadowPatches.SymmetricBox = v,
            "ON (cube around the camera)", "OFF (vanilla wedge)",
            visual: true);

        t.Add("shadowmargin", ToggleGroup.Shadows, "far shadow coverage margin",
            () => ShadowPatches.EffectiveFarBoxMargin > 0,
            () =>
            {
                // Off means the retained far map covers only what the fade needs, and the
                // throttle is back to redrawing on the first step anybody takes.
                ShadowPatches.FarBoxMargin =
                    ShadowPatches.FarBoxMargin > 0 ? 0.0 : Math.Max(0.0, config.ShadowFarBoxMargin);
                ShadowThrottlePatches.Invalidate();
                return "far shadow coverage margin " + (ShadowPatches.EffectiveFarBoxMargin > 0
                    ? "ON (" + ShadowPatches.EffectiveFarBoxMargin.ToString("0.#", ci)
                      + " blocks, redraw after "
                      + ShadowThrottlePatches.MoveLimit.ToString("0.#", ci)
                      + " blocks of camera movement)"
                    : "OFF (redraw after " + ShadowThrottlePatches.MoveLimit.ToString("0.##", ci)
                      + " blocks - i.e. on nearly every frame while moving)"
                      + (ShadowPatches.SymmetricBox ? "" : "; needs the symmetric box"));
            }, visual: true);

        t.AddFlag("shadowfade", ToggleGroup.Shadows, "shadow fade fix",
            () => ShadowPatches.FadeFix, v => ShadowPatches.FadeFix = v,
            "ON", "OFF (vanilla)",
            visual: true);

        // This one switches between vanilla and whatever komet.json configured, so with the
        // multiplier left at its default there is nothing on the other side of the switch. It
        // used to flip anyway and report "shadow distance x1 (vanilla)" every single time - a
        // field log shows twelve of those in a row, which is what a player does when a switch
        // says it worked and nothing happens.
        t.Add("shadowdist", ToggleGroup.Shadows, "shadow distance multiplier",
            () => ShadowPatches.DistanceMultiplier != 1.0,
            () =>
            {
                ShadowPatches.DistanceMultiplier =
                    ShadowPatches.DistanceMultiplier != 1.0 ? 1.0 : ShadowPatches.ConfiguredMultiplier;
                return "shadow distance x" + ShadowPatches.DistanceMultiplier.ToString("0.##", ci)
                    + (ShadowPatches.DistanceMultiplier == 1.0 ? " (vanilla)" : "");
            },
            unavailable: () => ShadowPatches.ConfiguredMultiplier == 1.0
                ? Loc.T("komet:msg-no-shadowdist",
                    "ShadowDistanceMultiplier is 1.0 in komet.json - this switches between that and vanilla, and they are the same.")
                : null,
            visual: true);

        t.AddFlag("shadowlod", ToggleGroup.Shadows, "lod3 stand-ins dropped",
            () => FastCuller.ShadowSkipRedundantLod, v => FastCuller.ShadowSkipRedundantLod = v,
            "GONE (only the detailed version left)", "IN (vanilla, both versions)",
            visual: true,
            sentence: "lod3 stand-ins in the shadow pass");

        t.AddFlag("shadowstab", ToggleGroup.Shadows, "shadow texel snapping",
            () => ShadowStabilityPatches.Enabled, v => ShadowStabilityPatches.Enabled = v,
            "ON", "OFF (vanilla)",
            unavailable: () => ShadowStabilityPatches.Installed
                ? null
                : Loc.T("komet:msg-no-shadowstab",
                    "the texel snapping patch is not installed - StabiliseShadowTexels in komet.json."),
            visual: true);

        t.Add("shadowthrottle", ToggleGroup.Shadows, "far cascade throttle",
            () => ShadowThrottlePatches.Throttling,
            () =>
            {
                if (ShadowThrottlePatches.Throttling)
                {
                    ShadowThrottlePatches.SetIntervals(1, 1, 1);
                    return "shadow throttle OFF (far cascade every frame, vanilla)";
                }

                // the config pair when it throttles, else the tested 2/4 - so the toggle
                // works even on a config that has throttling off
                var far = Math.Max(2, config.ShadowFarUpdateInterval);
                var skip = Math.Max(4, config.ShadowFarMaxSkip);
                ShadowThrottlePatches.SetIntervals(far, config.ShadowNearUpdateInterval, skip);
                return $"shadow throttle ON (far cascade every {far}-{skip} frames, movement forces it immediately)";
            }, visual: true);

        t.AddFlag("shadowcull", ToggleGroup.Shadows, "shadow back-face culling",
            () => ShadowCullPatches.Enabled, v => ShadowCullPatches.Enabled = v,
            "ON (solid passes draw front faces only into the shadow maps)",
            "OFF (vanilla: every face of every pass)",
            visual: true,
            sentence: "shadow pass back-face culling");

        t.Add("particles", ToggleGroup.Diagnostics, "particle pool measurement",
            () => ParticlePatches.Enabled,
            () =>
            {
                ParticlePatches.Enabled = !ParticlePatches.Enabled;
                if (!ParticlePatches.Enabled) ParticlePatches.Reset();
                return "particle pool measurement " + (ParticlePatches.Enabled
                    ? "ON (physics and upload on the render thread, off-thread pickup apart)"
                    : "OFF");
            });

        t.AddFlag("shadownearfit", ToggleGroup.Shadows, "near depth fitted to what can cast",
            () => ShadowDepthPatches.Enabled, v => ShadowDepthPatches.Enabled = v,
            "FITTED (the down-sun half of the extend is not drawn)",
            "as the engine projects it (vanilla: half the extend spent down-sun)",
            unavailable: () => ShadowDepthPatches.Installed
                ? null
                : Loc.T("komet:msg-no-shadownearfit",
                    "the near depth fit patch is not installed - ShadowNearDepthFit in komet.json."),
            visual: true,
            sentence: "near shadow depth");

        t.AddFlag("particleorphan", ToggleGroup.Diagnostics, "rename the particle instance buffers instead of waiting",
            () => ParticlePatches.Orphan, v => ParticlePatches.Orphan = v,
            "INVALIDATED before every upload - read 'particles' in the report now",
            "overwritten in place (vanilla) - read 'particles' in the report now",
            sentence: "particle instance buffers:");

        t.AddFlag("farmesh", ToggleGroup.Culling, "far lod: cells instead of blocks beyond the far distance",
            () => FarMesh.Enabled, v => FarMesh.Enabled = v,
            "ON: new chunks get their pictures built, built chunks draw them beyond the far distance (next frame)",
            "OFF: built chunks draw the engine's mesh at every distance again (next frame); new chunks get no picture",
            unavailable: () => FarMeshPatches.Installed
                ? null
                : Loc.T("komet:msg-no-farmesh",
                    "the far lod patch is not installed - FarMesh in komet.json."),
            visual: true,
            sentence: "far lod");

        t.AddFlag("farlod2", ToggleGroup.Culling, "far lod tier 2: cells of four beyond twice the far distance",
            () => FarMesh.Tier2, v => FarMesh.Tier2 = v,
            "ON: beyond twice the far distance the cells of four are drawn (next frame); new chunks build them",
            "OFF: tier 1 carries on to the view distance (next frame); new chunks build no tier 2",
            unavailable: () => FarMeshPatches.Installed
                ? null
                : Loc.T("komet:msg-no-farmesh",
                    "the far lod patch is not installed - FarMesh in komet.json."),
            visual: true,
            sentence: "far lod tier 2");

        t.Add("spatialpools", ToggleGroup.Culling, "mesh pools routed by region",
            () => SpatialPools.Enabled,
            () =>
            {
                SpatialPools.Enabled = !SpatialPools.Enabled;
                return "mesh pools " + (SpatialPools.Enabled
                    ? $"routed by {SpatialPools.RegionBlocks}-block region for every model added from now on"
                    : "first-fit (vanilla) for every model added from now on; routed pools stay as they are");
            });

        t.AddFlag("fronttoback", ToggleGroup.Culling, "camera pass nearest first",
            () => FastCuller.FrontToBack, v => FastCuller.FrontToBack = v,
            "drawn NEAREST FIRST (pools by distance, cells by distance; gap bridging off)",
            "drawn in index order (vanilla)",
            visual: true,
            sentence: "camera pass");

        t.Add("shadowfoliage", ToggleGroup.Shadows, "skip foliage in the shadow maps (diagnostic)",
            () => ShadowCullPatches.SkipFoliage,
            () =>
            {
                ShadowCullPatches.SkipFoliage = !ShadowCullPatches.SkipFoliage;
                return ShadowCullPatches.SkipFoliage
                    ? "shadow maps WITHOUT the foliage passes - leaves, grass and crops cast nothing; read the gpu row now"
                    : "shadow maps with the foliage passes again (vanilla)";
            }, visual: true);

        t.Add("flatfrag", ToggleGroup.Diagnostics, "flat chunk fragment shader (diagnostic)",
            () => ChunkShaderSwap.Active,
            () =>
            {
                if (ChunkShaderSwap.Active)
                {
                    return ChunkShaderSwap.Restore()
                        ? "chunk fragment shader: the engine's again"
                        : "chunk fragment shader: restore FAILED - " + ChunkShaderSwap.LastError;
                }
                return ChunkShaderSwap.Enable()
                    ? "chunk fragment shader FLAT: one fetch and the alpha test, no fog, no shadows on chunks - read 'camera opaque' on the gpu row now"
                    : "chunk fragment shader swap FAILED - " + ChunkShaderSwap.LastError;
            },
            unavailable: () => Vintagestory.Client.NoObf.ShaderPrograms.Chunkopaque == null
                ? Loc.T("komet:msg-no-flatfrag", "the chunk shader program is not loaded yet - join a world first.")
                : null,
            visual: true);

        t.Add("passprobe", ToggleGroup.Diagnostics, "gpu pass probe",
            () => GpuPassProbe.Enabled,
            () =>
            {
                GpuPassProbe.Enabled = !GpuPassProbe.Enabled;
                if (!GpuPassProbe.Enabled) GpuPassProbe.Reset();
                return "gpu pass probe " + (GpuPassProbe.Enabled
                    ? "ON (elapsed time and fragments per chunk pass, every 3rd frame)"
                    : "OFF (the whole-frame query runs every frame again)");
            });

        t.Add("shadowfootprint", ToggleGroup.Shadows, "near pass cut to visible receivers",
            () => ShadowFootprintPatches.Enabled,
            () =>
            {
                ShadowFootprintPatches.Enabled = !ShadowFootprintPatches.Enabled;
                if (!ShadowFootprintPatches.Enabled) ShadowFootprintPatches.Reset();
                return "near shadow pass " + (ShadowFootprintPatches.Enabled
                    ? "CUT to casters that can reach a visible receiver"
                    : "drawn for every direction (vanilla)");
            },
            unavailable: () => ShadowFootprintPatches.Installed
                ? null
                : Loc.T("komet:msg-no-shadowfootprint",
                    "the near footprint cull patch is not installed - ShadowNearFootprintCull in komet.json."),
            visual: true);

        t.AddFlag("shadowclip", ToggleGroup.Shadows, "cull to the shadow box",
            () => ShadowPatches.TightCullBox, v => ShadowPatches.TightCullBox = v,
            "ON (only what the shadow projection can keep is submitted)",
            "OFF (vanilla: distance + depth extend, spent on the world X axis)",
            visual: true,
            sentence: "shadow cull to the projected box");

        t.Add("shadowdepth", ToggleGroup.Shadows, "depth-only solid passes",
            () => ShadowCullPatches.DepthOnly,
            () =>
            {
                ShadowCullPatches.DepthOnly = !ShadowCullPatches.DepthOnly;
                return "shadow pass depth-only shader for the solid passes " + (ShadowCullPatches.DepthOnly
                    ? "ON" + (ShadowCullPatches.DepthOnlyState != null ? " (" + ShadowCullPatches.DepthOnlyState + ")" : "")
                    : "OFF (vanilla: chunkshadowmap with alpha test for every pass)");
            }, visual: true);
    }

    /// <summary>Chunks: loading, upload, tesselation.</summary>
    private void AddChunks(ToggleRegistry t, CultureInfo ci)
    {
        t.Add("prebuild", ToggleGroup.Chunks, "window pipeline",
            () => WindowPrebuilder.Enabled,
            () =>
            {
                WindowPrebuilder.Enabled = !WindowPrebuilder.Enabled;
                if (WindowPrebuilder.Enabled) WindowPrebuilder.HardDisabled = false; // explicit user intent overrides a self-disable
                return "window pipeline " + (WindowPrebuilder.Enabled ? "ON" : "OFF (vanilla window build)");
            });

        t.AddFlag("prioupload", ToggleGroup.Chunks, "prio upload budget",
            () => PrioUploadPatches.Enabled, v => PrioUploadPatches.Enabled = v,
            "ON (bursts spread over several frames)", "OFF (vanilla: the whole prio queue in one frame)");

        t.AddFlag("uploaddruck", ToggleGroup.Chunks, "upload frame pressure",
            () => UploadBudget.FramePressureInput, v => UploadBudget.FramePressureInput = v,
            "ON (hot frames with uploads in flight push the budget down)", "OFF (the throttle only sees the upload clock, as before 01.09.)");

        t.Add("edgecoal", ToggleGroup.Chunks, "edge coalescing",
            () => EdgeCoalescePatches.Enabled,
            () =>
            {
                if (EdgeCoalescePatches.Enabled)
                {
                    // never strand a held mark: everything pending goes out before vanilla takes over
                    EdgeCoalescePatches.Enabled = false;
                    EdgeCoalescePatches.FlushAll();
                    return "edge coalescing OFF (vanilla, everything flushed)";
                }

                // the patch is always applied and runtime-gated, so the toggle can
                // enable the experiment even with the config default of 0/off
                EdgeCoalescePatches.Enabled = true;
                return "edge coalescing ON (experimental; the default is off)";
            }, visual: true);

        t.Add("edgeprio", ToggleGroup.Chunks, "edge retess priority",
            () => EdgeRetessPriorityPatches.Enabled,
            () =>
            {
                if (EdgeRetessPriorityPatches.Enabled)
                {
                    EdgeRetessPriorityPatches.Enabled = false;
                    return "edge retess prio OFF (vanilla order, visible edge repairs wait again)";
                }

                EdgeRetessPriorityPatches.Enabled = true;
                // explicit user intent overrides a self-disable
                EdgeRetessPriorityPatches.HardDisabled = false;
                return "edge retess prio ON (visible edge repairs overtake the queue)";
            }, visual: true);

        t.Add("minimap", ToggleGroup.Chunks, "minimap budget",
            () => MinimapPatches.Enabled,
            () =>
            {
                MinimapPatches.Enabled = !MinimapPatches.Enabled;
                return "minimap budget " + (MinimapPatches.Enabled
                    ? "ON (" + MinimapPatches.TargetMs.ToString("0.#", ci)
                      + " ms per tick, the cap adapts)"
                    : "OFF (vanilla: up to 200 tiles per tick)");
            });

        t.AddFlag("minimapdirect", ToggleGroup.Chunks, "minimap direct upload",
            () => MinimapPatches.DirectUpload, v => MinimapPatches.DirectUpload = v,
            "ON (tiles via glTexSubImage2D into the component texture)", "OFF (vanilla: a framebuffer draw per tile)");

        t.Add("taskbudget", ToggleGroup.Chunks, "main thread task budget",
            () => MainThreadTaskPatches.BudgetMs > 0,
            () =>
            {
                MainThreadTaskPatches.BudgetMs = MainThreadTaskPatches.BudgetMs > 0
                    ? 0 : (config.MainThreadTaskBudgetMs > 0 ? config.MainThreadTaskBudgetMs : 3.0);
                return "task drain budget " + (MainThreadTaskPatches.BudgetMs > 0
                    ? "ON (" + MainThreadTaskPatches.BudgetMs.ToString("0.#", ci)
                      + " ms per frame, the remainder goes to the next frame in order)"
                    : "OFF (vanilla: everything queued runs in this frame)")
                    + (MainThreadTaskPatches.Enabled ? "" : " - only takes effect with 'mtt' ON");
            });
    }

    /// <summary>Entities.</summary>
    private void AddEntities(ToggleRegistry t, CultureInfo ci)
    {
        t.AddFlag("enttess", ToggleGroup.Entities, "entity tesselation budget",
            () => EntityTessPatches.Enabled, v => EntityTessPatches.Enabled = v,
            "ON", "OFF (vanilla)",
            visual: true);

        t.Add("entload", ToggleGroup.Entities, "entity load budget",
            () => EntityLoadPatches.Enabled,
            () =>
            {
                if (EntityLoadPatches.Enabled)
                {
                    // never strand a held entity: everything pending finishes before vanilla takes over
                    EntityLoadPatches.Enabled = false;
                    EntityLoadPatches.FlushAll();
                    return "entity load budget OFF (vanilla: every entity finishes in its packet task; everything held is loaded now)";
                }

                EntityLoadPatches.Enabled = true;
                return "entity load budget ON (" + EntityLoadPatches.BudgetMs.ToString("0.#", ci)
                    + " ms/frame, nearest entity first)";
            });

        t.Add("animlod", ToggleGroup.Entities, "animation lod",
            () => EntityAnimPatches.LodEnabled,
            () =>
            {
                EntityAnimPatches.LodEnabled = !EntityAnimPatches.LodEnabled;
                return "anim lod " + (EntityAnimPatches.LodEnabled
                    ? "ON (shadow-only entities every 3rd, beyond " + EntityAnimPatches.FarBlocks.ToString("0", ci)
                      + " blocks every 2nd frame)"
                    : "OFF (vanilla: every entity every frame)")
                    + (EntityAnimPatches.Enabled ? "" : " - only takes effect with 'entbefore' ON");
            });

        t.AddFlag("entbefore", ToggleGroup.Entities, "entity before attribution",
            () => EntityAnimPatches.Enabled, v => EntityAnimPatches.Enabled = v,
            "ON (pre-render and anim clocked separately, hitch lines name the entity)", "OFF (vanilla loop, and therefore no anim lod either)");

        t.Add("animwarm", ToggleGroup.Entities, "animation frame warm-up",
            () => AnimationWarmup.Enabled,
            () =>
            {
                AnimationWarmup.Enabled = !AnimationWarmup.Enabled && EntityLoadPatches.Enabled;
                return "animation frame warm-up " + (AnimationWarmup.Enabled
                    ? "ON (a worker generates a new shape's frames while its first entity is held)"
                    : EntityLoadPatches.Enabled ? "OFF (vanilla: generated on the main thread when an animation first plays)"
                                                        : "OFF - needs the entity load hold ('.komet toggle entload')");
            });
    }

    /// <summary>Memory.</summary>
    private void AddMemory(ToggleRegistry t)
    {
        t.Add("recycler", ToggleGroup.Memory, "mesh recycler pool",
            () => MeshRecyclerPatches.Enabled,
            () =>
            {
                MeshRecyclerPatches.SetEnabled(!MeshRecyclerPatches.Enabled);
                return "mesh recycler pool " + (MeshRecyclerPatches.Enabled
                    ? "ON (size classes, budget " + MeshRecyclerPatches.BudgetMb + " MB)"
                    : "OFF (vanilla store, own stock released)");
            });

        t.AddFlag("tightclone", ToggleGroup.Memory, "tight clone",
            () => TightClonePatches.Enabled, v => TightClonePatches.Enabled = v,
            "ON (custom parts are copied at content size)", "OFF (vanilla: capacity-sized copies)");

        t.Add("extrapool", ToggleGroup.Memory, "extras pool",
            () => TightClonePatches.PoolExtras,
            () =>
            {
                TightClonePatches.PoolExtras = !TightClonePatches.PoolExtras;
                FarLod.PoolArrays = TightClonePatches.PoolExtras;
                if (!TightClonePatches.PoolExtras) { TightClonePatches.ClearPools(); FarLod.ClearPools(); }
                return "extras pool " + (TightClonePatches.PoolExtras
                    ? "ON (per-face and custom arrays of the chunk parts are recycled)"
                    : "OFF (vanilla: fresh arrays per part, stock released)");
            });

        t.AddFlag("reclaim", ToggleGroup.Memory, "vram reclaimer",
            () => PoolReclaimer.Enabled, v => PoolReclaimer.Enabled = v,
            "ON", "OFF",
            visual: true);
    }

    /// <summary>The integrated server.</summary>
    private void AddServer(ToggleRegistry t)
    {
        t.Add("entsync", ToggleGroup.Server, "entity sync tuning",
            () => EntitySyncPatches.DistanceSendRate,
            () =>
            {
                EntitySyncPatches.DistanceSendRate = !EntitySyncPatches.DistanceSendRate;
                EntitySyncPatches.TrackingHysteresis = EntitySyncPatches.DistanceSendRate;
                return "entity sync tuning (server) " + (EntitySyncPatches.DistanceSendRate
                    ? "ON (positions by distance, tracking with hysteresis)"
                    : "OFF (vanilla: 30 Hz for everything, hard tracking band)")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only has an effect on a server that runs komet");
            });

        t.Add("attrskip", ToggleGroup.Server, "attribute no-op skip",
            () => EntitySyncPatches.AttributeNoOpSkip,
            () =>
            {
                EntitySyncPatches.AttributeNoOpSkip = !EntitySyncPatches.AttributeNoOpSkip;
                return "attribute no-op skip (server) " + (EntitySyncPatches.AttributeNoOpSkip
                    ? "ON (unchanged attribute paths are not sent)"
                    : "OFF (vanilla: every dirty path goes out)")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only has an effect on a server that runs komet");
            });

        t.Add("serveralloc", ToggleGroup.Server, "server alloc attribution",
            () => ServerAllocPatches.Enabled,
            () =>
            {
                ServerAllocPatches.Enabled = !ServerAllocPatches.Enabled;
                return "server alloc attribution " + (ServerAllocPatches.Enabled ? "ON" : "OFF")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only measures the integrated server");
            });

        t.Add("packetsrc", ToggleGroup.Server, "block packet sources",
            () => PacketSourcePatches.Enabled,
            () =>
            {
                PacketSourcePatches.Enabled = !PacketSourcePatches.Enabled;
                return "block packet sources (server) " + (PacketSourcePatches.Enabled ? "ON" : "OFF")
                    + (capi != null && capi.IsSinglePlayer ? "" : " - only measures the integrated server");
            });
    }

    /// <summary>The instruments themselves.</summary>
    private void AddDiagnostics(ToggleRegistry t)
    {
        // These do not draw anything and safemode leaves them alone, which is exactly why they
        // need to be switchable: instrumentation is on the same side of the ledger as the work
        // it removes, and "safemode is faster" was reported once for this reason.
        t.Add("profiler", ToggleGroup.Diagnostics, "renderer profiler",
            () => RendererProfiler.Enabled,
            () =>
            {
                // Wrapping/unwrapping needs the event manager, which only exists in a world.
                RendererProfiler.Enabled = !RendererProfiler.Enabled;
                if (RendererProfiler.Enabled) WrapRenderers(); else UnwrapRenderers();
                return "renderer profiling " + (RendererProfiler.Enabled
                    ? "ON (" + RendererProfiler.StatWrapped + " renderers wrapped - costs frame time)"
                    : "OFF (vanilla dispatch)");
            });

        t.Add("beforeattr", ToggleGroup.Diagnostics, "before stage attribution",
            () => RendererProfiler.AttributeBeforeStage,
            () =>
            {
                RendererProfiler.AttributeBeforeStage = !RendererProfiler.AttributeBeforeStage;
                if (RendererProfiler.AttributeBeforeStage) WrapRenderers();
                else if (!RendererProfiler.Enabled) UnwrapRenderers();
                return "before stage attribution " + (RendererProfiler.AttributeBeforeStage
                    ? "ON (hitch lines can name the before renderer)"
                    : "OFF (vanilla dispatch in the before stage)");
            });

        t.Add("tickprofiler", ToggleGroup.Diagnostics, "tick profiler",
            () => TickProfiler.Enabled,
            () =>
            {
                TickProfiler.Enabled = !TickProfiler.Enabled;
                WrapTickListeners();
                return "tick profiler " + (TickProfiler.Enabled
                    ? "ON (" + TickProfiler.StatWrapped + " listeners wrapped)"
                    : "OFF (vanilla delegates)");
            });

        t.AddFlag("mtt", ToggleGroup.Diagnostics, "main thread task attribution",
            () => MainThreadTaskPatches.Enabled, v => MainThreadTaskPatches.Enabled = v,
            "ON (every task is clocked, hitch lines name the packet type)", "OFF (vanilla drain)");

        t.AddFlag("clientalloc", ToggleGroup.Diagnostics, "client alloc attribution",
            () => ClientAllocPatches.Enabled, v => ClientAllocPatches.Enabled = v,
            "ON (worker threads and thread pool per caller)", "OFF");

        t.Add("allocsample", ToggleGroup.Diagnostics, "alloc sampling",
            () => AllocSampler.Enabled,
            () =>
            {
                if (AllocSampler.Enabled)
                {
                    Measure.FrameStats.PeriodicSample -= AllocSampler.Sample;
                    AllocSampler.Stop();
                    return "alloc sampling OFF";
                }

                AllocSampler.Start();
                if (AllocSampler.Enabled) Measure.FrameStats.PeriodicSample += AllocSampler.Sample;
                return AllocSampler.Enabled
                    ? "alloc sampling ON (runtime events, all threads, by type)"
                    : "alloc sampling could not start: " + AllocSampler.Failure;
            });

        t.AddFlag("retess", ToggleGroup.Diagnostics, "dirty mark source sampling",
            () => RetessSourcePatches.SampleSources, v => RetessSourcePatches.SampleSources = v,
            "UNCAPPED (every 8th mark with a stack - '.komet retess' shows the ranking)", "CAPPED (still active, at most 25 captures/s)");

        t.Add("cullcheck", ToggleGroup.Diagnostics, "sweep cross-check",
            () => CullVerifier.SampleEvery > 0,
            () =>
            {
                CullVerifier.SampleEvery = CullVerifier.SampleEvery > 0 ? 0 : Math.Max(1, config.VerifyCullSweepEvery);
                CullVerifier.Reset();
                return "sweep cross-check " + (CullVerifier.SampleEvery > 0
                    ? "ON (every " + CullVerifier.SampleEvery + "th sweep against vanilla)" : "OFF");
            });

        t.AddFlag("hudraster", ToggleGroup.Diagnostics, "hud raster off-thread",
            () => DebugHud.BackgroundRaster, v => DebugHud.BackgroundRaster = v,
            "IN THE WORKER (the frame only pays sampling + upload)",
            "SYNCHRONOUS (full rebuild inside the frame, like vanilla overlays)",
            sentence: "hud raster");
    }
}
