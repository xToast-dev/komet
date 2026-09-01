namespace Komet;

/// <summary>
/// Written to VintagestoryData/ModConfig/komet.json on first start. Every patch can be
/// switched off individually so a regression can be bisected without rebuilding.
/// </summary>
public class KometConfig
{
    /// <summary>
    /// The layout version of this file. Bumped by hand, and only when a change actually
    /// concerns the config: a new setting, a removed one, a changed default. It is deliberately
    /// NOT the mod version - the two used to be the same value, which meant every single
    /// release threw away everybody's settings, including the releases that touched nothing a
    /// user had configured.
    ///
    /// A regeneration is needed at all because changing a default in the source does *not*
    /// reach anyone who already has the file: LoadModConfig deserialises whatever is on disk
    /// and StoreModConfig writes it straight back. A fix shipped as "the default is now X"
    /// therefore silently missed every existing install - which is exactly how a shadow fix
    /// stayed half-applied.
    /// </summary>
    public const string Current = "3";

    /// <summary>
    /// The <see cref="Current"/> value this file was written by. Does not match the running
    /// mod's? Then the file is backed up and regenerated from the current defaults.
    /// </summary>
    public string ConfigVersion { get; set; }

    /// <summary>
    /// Replace MeshDataPool.FrustumCull with the struct-of-arrays version. This is the patch
    /// that actually moves the frame time; everything else here is small change.
    /// </summary>
    public bool FastFrustumCulling { get; set; } = true;

    /// <summary>
    /// Cull every mesh pool of a render stage in one parallel pass instead of one at a time.
    /// Once the spatial index has cut the per-part work, the fixed cost per sweep dominates -
    /// and the engine issues a sweep per pool per stage - thousands a frame at view
    /// distance 1536. Pools only ever touch their own arrays, so they can be done concurrently.
    /// </summary>
    public bool ParallelCulling { get; set; } = true;

    /// <summary>
    /// Helper threads for the parallel cull, on top of the render thread. 0 = auto: physical
    /// cores minus one, capped at 8.
    ///
    /// These are dedicated threads, not the shared ThreadPool - the game queues chunk
    /// tesselation on that pool, and a sweep that has to wait for the pool's thread injection
    /// heuristic (about one thread per 500 ms) stalls the frame. That was measured: sweeps of
    /// 26, 30 and 46 ms in frames with no GC pause, against an average well under one.
    ///
    /// The count is derived from physical cores rather than hardware threads because the sweep
    /// is a memory-bound linear scan, where an SMT sibling adds queueing and not throughput -
    /// and because eleven cull threads on a six core part left nothing for the render thread,
    /// the tesselator, or the collector.
    /// </summary>
    public int CullingThreads { get; set; }

    /// <summary>
    /// Test four mesh parts per instruction in the visibility sweep, using 256 bit double lanes
    /// (AVX). Measured at the in-game pool shape over three runs: 0,96-1,02 ms scalar against
    /// 0,69-0,71 ms, so about 1,4x - roughly 29 % off the sweep, which is the largest single
    /// item in the frame this mod touches. The plane test alone is 4,3x (0,224 -> 0,052 ms for
    /// 24 000 parts in the micro-benchmark); the rest of the sweep is cell rejection, the
    /// bitmap scan and the pointer chase to each surviving part, none of which vectorise.
    ///
    /// The decision is bit-identical, not approximately identical: each lane performs the same
    /// multiplications and additions in the same order as the scalar code, without FMA
    /// contraction, and NaN is treated as "inside" exactly as Plane.AABBisOutside does. Both
    /// kernels run the benchmark's full equivalence set against vanilla (3120 checks) on every
    /// build. Ignored on a CPU without AVX. '.komet toggle simd' flips it live.
    /// </summary>
    public bool VectorCulling { get; set; } = true;

    /// <summary>Reject an entire mesh pool up front using its cached bounding box.</summary>
    public bool PoolLevelCulling { get; set; } = true;

    /// <summary>
    /// Parts a grid cell should hold on average - what the cell edge length is derived from.
    ///
    /// Exposed because the benchmark has been wrong about it twice: it modelled 96 pools where
    /// the game reports 600, and it drew part positions uniformly at random while its own
    /// comment claimed distance-ordered tesselation order. Both of those are precisely what a
    /// spatial grid's optimum depends on. '.komet toggle cellsize' flips between this and 160
    /// live, and the stress test has a phase for it - measuring in the scene beats modelling it.
    /// </summary>
    public int PartsPerCellTarget { get; set; } = 32;

    /// <summary>
    /// Unix nice increment for the occlusion walk's threads, 0 = leave them at normal priority.
    ///
    /// Default off. The idea is sound - the walk holds five threads for 11 ms about five times a
    /// second while the cull batch needs every core for a fraction of a millisecond on the
    /// frame's deadline - but the one field measurement with it on had the cull batch waiting
    /// *longer*, not shorter, and I have no mechanism that explains that. The measurement was
    /// confounded (heavier scene, a diagnostic running), so this ships as an option rather than
    /// as a default until a clean A/B says which way it goes. Linux only; Thread.Priority is
    /// accepted and silently ignored there, so this goes through setpriority.
    /// </summary>
    public int OcclusionThreadNiceness { get; set; }

    /// <summary>
    /// Coalesce mesh parts that sit back to back in the index buffer into one draw range, so
    /// glMultiDrawElements gets a few long ranges instead of thousands of short ones.
    /// </summary>
    public bool MergeDrawRanges { get; set; } = true;

    /// <summary>
    /// Also merge across gaps of parts the frustum test rejected: their triangles are clipped
    /// by the GPU before rasterisation (identical pixels), so drawing them along costs idle GPU
    /// vertex work and saves one draw range per gap on the CPU, where the measured frames are
    /// actually spent. Never bridges LOD-rejected, hidden, occluded parts or free buffer space -
    /// those would change the picture. Requires MergeDrawRanges.
    /// </summary>
    public bool GapMergeDrawRanges { get; set; } = true;

    /// <summary>
    /// Replace the occlusion culling ray walk with the hoisted, snapshot-based, multi-threaded
    /// one. Same visibility result; it just stops pinning a core at high view distances.
    /// </summary>
    public bool FastOcclusionCulling { get; set; } = true;

    /// <summary>
    /// Helper threads for the occlusion walk. 0 = auto: physical cores minus two, capped at 8.
    /// Its own dedicated set, separate from the cull threads, so a walk in flight can never
    /// hold up a render stage queued behind it.
    /// </summary>
    public int OcclusionCullingThreads { get; set; }

    /// <summary>
    /// Minimum milliseconds between two occlusion passes triggered by chunks streaming in.
    /// While loading, vanilla re-runs the pass every ten new chunks; each run clears and
    /// snapshots the whole chunk dictionary under the same lock the network and tesselation
    /// paths need. Newly loaded distant chunks appear at most this much later; a camera move
    /// across a chunk border still passes immediately. 0 restores vanilla behaviour.
    /// </summary>
    public int OcclusionMinIntervalMs { get; set; } = 200;

    /// <summary>
    /// Copy chunk mesh data into persistently mapped GPU buffers in bulk instead of one
    /// element at a time.
    ///
    /// Off by default: in 1.22.7 ClientPlatformWindows.allowPStorage is never assigned, so
    /// persistent mapping is dead code and every upload already goes through glBufferSubData.
    /// Measured on this install: 0 bulk copies against 342 605 fall-throughs. Kept in case a
    /// later version or another mod turns persistent storage on.
    /// </summary>
    public bool BulkMeshUpload { get; set; }

    /// <summary>
    /// EXPERIMENTAL. Turn on the engine's own persistent-mapping path for chunk VBOs
    /// (ClientPlatformWindows.allowPStorage, which vanilla never sets). Uploads then write
    /// straight into mapped GPU memory instead of glBufferSubData into a buffer the GPU may
    /// still be reading - which is where the upload spikes come from. Implies BulkMeshUpload.
    ///
    /// A path the developers never enable is a path they never tested: if terrain renders
    /// wrong or the game crashes on world join, set this back to false.
    /// </summary>
    public bool ExperimentalPersistentMapping { get; set; }

    /// <summary>
    /// Cap how long the main thread spends uploading chunk meshes per frame. Vanilla's budget
    /// grows with the square of the view distance and again with the tesselation backlog,
    /// which is what makes frame times collapse while moving at high view distance.
    /// </summary>
    public bool AdaptiveUploadBudget { get; set; } = true;

    /// <summary>Milliseconds per frame the upload throttle aims for.</summary>
    public double UploadBudgetTargetMs { get; set; } = 6.0;

    /// <summary>
    /// Also budget the PRIORITY chunk upload queue, which vanilla drains completely in a
    /// single frame with no limit of any kind. Its designed load is a player block edit -
    /// one or two chunks - but relight storms (time or season changes, light-baking mods)
    /// and priority re-tesselations all route through the same queue, and the hitch log
    /// measured 10-27 ms of upload in single frames while one was running. The budget
    /// uploads at least one chunk per frame and at least a full chunk mesh's worth of
    /// vertices (so an edit still appears in the frame it was meshed in) and carries the
    /// rest into the following frames - deferred, never lost. '.komet toggle prioupload'
    /// flips it live.
    /// </summary>
    public bool BudgetPriorityUploads { get; set; } = true;

    /// <summary>
    /// Look animation codes up case-insensitively instead of allocating a lowercase copy of
    /// the key on every animated entity, every frame.
    /// </summary>
    public bool AnimationLookupWithoutAlloc { get; set; } = true;

    /// <summary>
    /// Replace the LINQ .Any() over the active animation dictionary with a plain loop; the
    /// LINQ version boxes the dictionary enumerator once per animated entity per frame.
    /// </summary>
    public bool AnimationCollisionBoxWithoutAlloc { get; set; } = true;

    /// <summary>
    /// Skip the two unconditional glGetError() calls per frame in ClientMain. They are a
    /// hard sync point when mesa_glthread is enabled, and the cost is load-dependent:
    /// measured via `.komet stress` at 0.06 ms in an idle scene but 2.3 ms per frame while
    /// chunks stream in (deep GL queue).
    ///
    /// ON by default since 1.29.0, and this time with the full story: 1.28.0 shipped it on
    /// and a post-teleport flicker report followed, so 1.28.2 reverted it as prime suspect -
    /// but the flicker survived the revert, turned out to live only in the clouds, and was
    /// finally pinned on the sun-occlusion-query throttle (now default-off). The user then
    /// re-enabled this skip live and teleports stayed clean - exonerated by test, not by
    /// argument. The trade that remains: the engine's out-of-video-memory message goes
    /// quiet; turn this off on cards with tight VRAM. `.komet toggle glerror` flips it live.
    /// </summary>
    public bool SkipPerFrameGlErrorCheck { get; set; } = true;

    /// <summary>
    /// Time the visibility sweep so .komet can report milliseconds per frame rather than
    /// event counts. Costs roughly 0.03 ms per frame.
    /// </summary>
    public bool MeasureCullTime { get; set; } = true;

    /// <summary>
    /// Tell the shader the range the far shadow map actually covers, so distant shadows fade
    /// out instead of ending at a hard line. Vanilla sets the uniform to twice the real box
    /// size, which never lets the smooth fade finish. Costs nothing.
    /// </summary>
    public bool FixShadowFadeCutoff { get; set; } = true;

    /// <summary>
    /// Scales the far shadow cascade's radius. 1.0 is vanilla (255 blocks at shadow quality 4).
    ///
    /// This is the single most expensive number in the file, and it was 1.5 until 1.40.0. With
    /// the symmetric box the shadow map covers a square of 2 x R x multiplier blocks, so the
    /// cost and the sharpness both go with the SQUARE of this: 1.5 spread one shadow map over
    /// 765 x 765 blocks where vanilla covers a 0.78 R x 0.44 R wedge, which is about twelve
    /// times the ground on the same texels. That is 9.4 texels per block against vanilla's ~31,
    /// and every chunk in the extra area is geometry both shadow cascades have to draw.
    ///
    /// Back to 1.0: 14 texels per block and less than half the shadow-pass area, for a shadow
    /// range vanilla never had in most directions anyway. The HUD's "schattenmap ... texel je
    /// block" row is the number to watch - it decides whether thin geometry still casts.
    /// </summary>
    public double ShadowDistanceMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Builds the shadow box as a cube centred on the camera instead of vanilla's cone along
    /// the fixed world -Z axis (0.78 R wide, 0.45 R tall at FoV 70 / 16:9). Vanilla's box runs
    /// out before the smooth distance fade finishes in most directions, so shadows end in a
    /// hard line whose distance depends on which way you look. The cube puts the map edge at R
    /// everywhere, and the fade always completes first - no visible cut.
    ///
    /// Replaces the FAR cascade's shadow box with a camera-centred cube; the near cascade
    /// stays exactly vanilla (since 1.43.0 - it used to widen both).
    ///
    /// Back ON by default, and the full history belongs here because this flag has now flipped
    /// twice. 1.42.1 turned it off: the stress test priced the both-cascades version at
    /// +0,72 ms +-0,08 - the whole "safemode is faster" gap. The user then photographed what
    /// vanilla's box actually looks like: a hard, view-direction-dependent line where the
    /// shadow map runs out, called it "extrem stark sichtbar", and that settled the trade the
    /// other way. Both facts stand; this default is the judgement that ~0,5 ms is the fair
    /// price for the line being gone, on a 5-7 ms frame. `.komet toggle shadowbox` flips it
    /// live, the stress phase prices it in your scene, and ShadowDistanceMultiplier below 1.0
    /// cheapens it quadratically if the frame budget is tight.
    ///
    /// Why vanilla shows the line at all (all three verified in the engine source): the ortho
    /// shadow projection has no translation, so the map is always centred on the CAMERA and
    /// only the box's spans matter; vanilla's box is the view frustum's AABB, built with
    /// getCameraRotationMatrix() returning the identity, so it points along world -Z whatever
    /// way you look; and half of its span therefore covers ground behind you while the map
    /// runs out in front at roughly half the fade distance. The sphere is the optimal shape
    /// for a camera-centred projection - a camera-oriented wedge was designed and rejected
    /// (with the sun overhead it degenerates to the sphere's spans, and it would go stale on
    /// every camera turn, which the sphere never does).
    ///
    /// Far-only makes it cheaper AND sharper than the 1.42.x version: the near cascade has a
    /// safety net (where its map ends, the far map takes over seamlessly), so widening it
    /// bought nothing and halved near-shadow texel density. Only the far map has no net -
    /// where IT ends, the shader cuts. So only the far cascade pays for the fix.
    /// </summary>
    public bool SymmetricShadowBox { get; set; } = true;

    /// <summary>
    /// Check one visibility sweep in every N against what vanilla's FrustumCull would have
    /// drawn, and log any disagreement. 0 = off.
    ///
    /// This exists because a cache bug shipped and flickered in the field: the engine assigns
    /// CullVisible and LodLevel to a mesh part AFTER MeshDataPool.TryAdd returns, so the patch
    /// that folds a newly inserted part into the cache was reading a LodLevel of 0 - which the
    /// camera pass treats as invisible. Every test passed, because every test built its parts
    /// fully populated. Only the running game holds the state that breaks it, so the check
    /// belongs there. It costs one vanilla sweep over one pool every N sweeps; at the default
    /// and ~290 sweeps a frame that is well under one pool per frame.
    ///
    /// 0 (off) by default since 1.42.0: the bug it was built for is fixed and confirmed fixed in
    /// the field, and a permanent second opinion on a sweep the benchmark already checks 3120
    /// ways is the same kind of always-on measurement this release is removing everywhere else.
    /// '.komet toggle cullcheck' arms it again without a restart, which is what to do the
    /// moment anything flickers.
    /// </summary>
    public int VerifyCullSweepEvery { get; set; }

    /// <summary>
    /// Measure GPU time per frame with a GL_TIME_ELAPSED query, shown as "gpu-frame" in the
    /// HUD. This is the number that separates "CPU-bound" from "GPU-bound" - the underwater
    /// half-framerate case was undiagnosable without it. Results are collected once a second
    /// from a query that is already three frames old, so it adds no per-frame driver sync.
    /// </summary>
    public bool MeasureGpuTime { get; set; } = true;

    /// <summary>
    /// Run the sun's occlusion query every Nth frame instead of every frame. 1 = vanilla.
    ///
    /// SystemRenderSunMoon draws a colour-masked quad purely to measure how much of the sun is
    /// covered, and reads the result with two glGetQueryObject calls. With mesa_glthread on -
    /// the radeonsi default - a GL call that returns a value must sync with the driver thread,
    /// which measured 1.86 ms of an 11.9 ms frame here. The pass writes no pixels and its
    /// result is time-smoothed into the sun glare over ~50 ms, so sampling it less often
    /// cannot change what you see.
    /// </summary>
    /// (1 = vanilla, the default since 1.28.3: the `.komet stress` runs measured the
    /// throttle at -0.11 ms idle and -0.93 +-0.95 ms while streaming - noise around zero
    /// on current frames - and bisection points at it for cloud artefacts in the seconds
    /// after teleporting: clouds render in OIT right after this pass, and the restored
    /// state set is evidently not complete while the renderer list churns. A patch that
    /// saves nothing and is the prime suspect for a visual artefact earns no default.)
    public int SunOcclusionQueryInterval { get; set; } = 1;

    /// <summary>
    /// Extra shadow map resolution steps on top of the graphics setting, 1024 texels per axis
    /// each - the framebuffer only, so the shader defines and cascade distances your quality
    /// setting picked stay exactly as they are. At shadow quality 4 the engine's ceiling is
    /// 6144 squared; 1 step makes it 7168, 2 steps 8192.
    ///
    /// One step (7168) is the default: with the sphere far box (span 488 blocks) that is
    /// 14,7 texels per block against vanilla's 13,7-24 (its span swings 257-450 with the sun),
    /// for 411 MB instead of 288 MB of video memory. Raise to 2 (8192, 537 MB) if shadow edges
    /// still look coarse and the HUD's `gpu-frame` row shows headroom - this scene is
    /// GPU-bound, and shadow resolution is pure GPU fill.
    ///
    /// A confession that belongs in the record: until 1.43.0 this setting had NEVER taken
    /// effect on a normal game launch. The engine builds its framebuffers when the window is
    /// created - before any mod loads - so the patch applied cleanly, logged "enabled", and
    /// sized nothing; it only worked if a graphics setting was changed mid-session, which
    /// rebuilds the framebuffers. The user's HUD caught it ("schattenmap 6144px" with 1 step
    /// configured). The mod now forces one framebuffer rebuild after loading, and logs
    /// "shadow map framebuffers rebuilt at Npx" as proof - a patch that applies but is never
    /// exercised looks exactly like one that works, for the fourth time in this project.
    ///
    /// Cost per step, two cascades at 4 bytes per depth texel: 288 MB at 6144, 411 MB at 7168,
    /// 537 MB at 8192. The pass draws the same geometry either way - resolution costs fill
    /// rate and memory, not triangles. 0 = vanilla size.
    /// </summary>
    public int ShadowMapExtraQuality { get; set; } = 1;

    /// <summary>
    /// Quantise the shadow projection to whole shadow map texels, so the texel grid stands
    /// still in the world instead of sliding along with the player.
    ///
    /// The engine centres the shadow projection on the camera and never writes a translation
    /// into it, so every fraction of a block you walk re-samples every shadow edge on a
    /// different texel boundary - the edges crawl and shimmer. Snapping is the standard answer
    /// and costs two subtractions per shadow pass.
    ///
    /// OFF by default. It changes what you see and I cannot see your screen: it shipped
    /// unverified in 1.15.0, a shadow complaint followed, and a visual change nobody has
    /// checked does not get to stay on by assumption. Turn it on to judge it - if distant
    /// shadow edges stop crawling while you walk, it is doing its job.
    /// </summary>
    public bool StabiliseShadowTexels { get; set; }

    /// <summary>
    /// Leave the far-LOD stand-in geometry out of the shadow passes wherever the camera pass
    /// does not draw it either.
    ///
    /// A block that has a simplified Lod2Mesh is tesselated twice: the detailed mesh into LOD
    /// level 2 and the stand-in into level 3. The camera pass picks exactly one by distance -
    /// the stand-in only beyond 640 blocks. The shadow passes apply no distance rule at all,
    /// so inside the shadow box (at most ~415 blocks) they rasterise both versions of the same
    /// block into the shadow map. Skipping the stand-in there leaves the *more* detailed mesh
    /// in place, so shadows should be unchanged or marginally more accurate.
    ///
    /// Off by default because it is a real change to what vanilla draws and the effect can
    /// only be judged by looking at shadows in your world - turn it on, look at foliage and
    /// fences, turn it off again if anything looks thinner.
    /// </summary>
    public bool ShadowSkipRedundantLod { get; set; }

    /// <summary>
    /// Shortest gap, in frames, between two renders of the far shadow cascade. The shadow
    /// stages draw the terrain again from the light's point of view - the far cascade is the
    /// expensive half, and it changes slowly.
    ///
    /// Default 2 (with MaxSkip 4) since 1.43.0, after three releases at 1/1. The history, so
    /// this flag's next reader has it: throttling shipped in 1.9, collected three visual
    /// complaints (cut-off line while flying, flicker, missing near shadows) and was defaulted
    /// off in 1.17 because a retained map only covers the volume it was drawn for. Every one of
    /// those mechanisms has since been fixed or removed: the movement test now runs BEFORE the
    /// interval floor (the flying cut-off), skipped frames rewrite the sampling matrix
    /// exactly (the flicker - verified against an independent reference multiply), the near
    /// cascade is not throttled at all (the missing near shadows), and the sphere far box
    /// covers a symmetric +-R whatever way the camera looks - the coverage asymmetry that made
    /// staleness visible is gone. What remains is the honest residual: shadows of MOVING
    /// things 40+ blocks away update up to 4 frames late, about 21 ms at 190 fps.
    ///
    /// Why now: the user's hitch log. The recurring spikes book 20-44 ms into the shadow
    /// stages while standing still - and standing still is exactly when the throttle skips at
    /// its maximum (a retained map is bit-identical then, the skip is free). Fewer shadow
    /// submissions per second also means less driver back-pressure, which is where those
    /// spikes actually come from. '.komet toggle shadowthrottle' flips 1/1 vs the configured
    /// pair live, and the stress test has a phase for it.
    /// </summary>
    public int ShadowFarUpdateInterval { get; set; } = 2;

    /// <summary>
    /// Longest gap, in frames, the far cascade may go without a re-render. Between the two
    /// intervals the cascade is redrawn as soon as the camera has actually moved (see
    /// ShadowFarMoveThreshold) or the sun has turned - so standing still costs almost nothing
    /// while flying still updates at the floor rate. 4 is the historically tested pair with
    /// interval 2 and the default since 1.43.0; see ShadowFarUpdateInterval for the reasoning.
    /// </summary>
    public int ShadowFarMaxSkip { get; set; } = 4;

    /// <summary>
    /// How far the camera may move, in blocks, before the far cascade is redrawn - overriding
    /// even ShadowFarUpdateInterval.
    ///
    /// A retained shadow map is kept correctly positioned, but it only *covers* the volume it
    /// was drawn for. Move out of that volume and the sample coordinates leave the map, where
    /// the shader cuts the shadow off hard instead of fading it - visible as a cut-off line
    /// that jumps when the cascade is redrawn, most obviously while flying up and down. Small
    /// on purpose: at 85 fps this is about three frames of walking but less than one of flying.
    /// </summary>
    public double ShadowFarMoveThreshold { get; set; } = 0.15;

    /// <summary>
    /// Same for the near cascade. Left at 1 by default: it covers the ground right around you,
    /// where any lag is much easier to see. When set above 1 it is phase-shifted against the
    /// far cascade so the two never skip on the same frame.
    /// </summary>
    public int ShadowNearUpdateInterval { get; set; } = 1;

    /// <summary>
    /// Attribute each render stage's time to the individual renderers inside it, so the HUD
    /// can name what is actually expensive instead of only which stage.
    ///
    /// OFF by default since 1.42.0, and the reason is the mod's own headline number. Every
    /// registered renderer is replaced by a timing decorator, and "roughly fifty renderers a
    /// frame" - what this comment used to say - is wrong by two orders of magnitude: at view
    /// distance 1536 the client holds around ten thousand renderer instances, nearly all of
    /// them block entities. That is ten thousand extra interface dispatches and cache misses
    /// on EVERY frame, plus two Stopwatch reads apiece on the sampled quarter, plus a linear
    /// scan of the stage's renderer list every time an unloading block entity unregisters
    /// itself. All of it is measurement, none of it draws anything, and it is on the same side
    /// of the ledger as everything this mod removes - it even inflates the frame it reports.
    ///
    /// It is still the sharpest tool here when a specific renderer is suspect (it found the
    /// firepit), so it turns on live with '.komet toggle profiler' and the stress test has a
    /// phase for it. Turn it on to answer a question, then turn it off again.
    /// </summary>
    public bool ProfileRenderers { get; set; }

    /// <summary>
    /// Keep the Before render stage's renderers timed every frame even while the full
    /// profiler is off. That stage holds only a handful of system renderers (entity
    /// preparation, chunk mesh uploads, the liquid depth pre-pass, camera, ambient - plus
    /// whatever other mods put there), so the cost is a few microseconds - but it is where
    /// the repeated unattributed world-join bursts (60-87 ms of "before" with no GC) live,
    /// and a hitch line that can say "renderer Before-ree 60 ms" beats one that cannot.
    /// The full profiler stays available via '.komet toggle profiler' as before.
    /// </summary>
    public bool AttributeBeforeStage { get; set; } = true;

    /// <summary>Show the on-screen performance overlay right away. Toggle in game with F7.</summary>
    public bool DebugHudVisible { get; set; }

    /// <summary>
    /// Measure each render stage and the game tick so the HUD can show where a frame goes.
    /// Two Stopwatch reads per stage, about 13 stages a frame.
    /// </summary>
    public bool MeasureRenderStages { get; set; } = true;

    /// <summary>
    /// Keep the terrain tesselation thread awake while chunks are queued. Vanilla's thread
    /// loop sleeps 5 ms after every tick regardless of the backlog; with 1500 chunks waiting
    /// those naps are pure loading time. An empty queue still sleeps like vanilla.
    /// </summary>
    public bool TesselationNoIdleSleep { get; set; } = true;

    /// <summary>
    /// Run the tesselation thread at AboveNormal priority. There is exactly one such thread
    /// and it is the whole chunk loading pipeline, so it should not have to queue behind the
    /// other eight workers for a core. Costs a little render smoothness while loading, which
    /// is exactly the trade wanting chunks sooner implies.
    /// </summary>
    public bool TesselationThreadPriority { get; set; } = true;

    /// <summary>
    /// Skip rendering firepit contents beyond this many blocks. The renderer declares
    /// RenderRange 48 but the engine never reads it, so every firepit with contents pays a
    /// shader switch, ~15 uniforms, a temperature lookup and a light lookup per frame at any
    /// distance - 4 ms/frame in a ruins area. Skipping is provably state-safe for this one
    /// renderer: an empty firepit already draws nothing, so successors never depended on its
    /// state. Never applied while a pot/crucible renderer is attached (those manage the
    /// cooking sound). 0 = vanilla.
    /// </summary>
    public int FirepitContentsMaxDistance { get; set; } = 64;

    /// <summary>
    /// How long a near firepit's light, temperature and glow colour stay cached, in
    /// milliseconds. Vanilla looks all three up per firepit per frame: a chunk-data read
    /// that contends with the network thread while chunks stream in, an attribute-tree walk,
    /// and a float[4] allocation - measured together at 4.6 ms/frame near a ruin. Light only
    /// changes on block updates and cooling is slow, so 150 ms staleness is invisible.
    /// 0 renders near firepits fully vanilla.
    /// </summary>
    public int FirepitLightCacheMs { get; set; } = 150;

    /// <summary>
    /// Build the 34x34x34 neighbourhood window for the NEXT queued chunk on a worker thread
    /// while the tesselation thread is still meshing the current one. The window build is
    /// 25-38 % of the per-chunk cost; overlapping it raises chunk digestion accordingly. The
    /// build is serialised with vanilla's own (a shared static makes two at once unsafe), and
    /// a prebuilt window is dropped whenever the underlying chunk data was replaced or a sun
    /// relight ran after the build started - vanilla then simply builds as usual.
    ///
    /// OFF by default since 1.42.2, and this is the reasoning rather than a hunch:
    ///
    /// * The user reported that terrain ambient occlusion - the corner darkening the tesselator
    ///   bakes into the mesh - "does not quite fit any more". Vertex AO is computed from which
    ///   neighbouring blocks are solid, i.e. from the BLOCK half of exactly this window. Nothing
    ///   else in this mod touches that data.
    /// * The canary caught it in the field. Three log lines in two minutes read
    ///   `validation mismatch ... (blocks True, ...)`, and that True is `!ReferenceEquals`: the
    ///   prebuilt window's blocks really differed from what vanilla read moments later. The
    ///   canary samples one window in 64, so three caught implies on the order of 190 uncaught
    ///   in the same span - each one a chunk mesh baked from block data the guard let through,
    ///   and it stays baked until something re-tesselates that chunk.
    /// * It buys nothing on frame time. The user's own stress run scores
    ///   `fenster-pipe aus: -0,05 ms +-0,10` - noise. What it buys is chunk LOADING throughput.
    ///
    /// So the trade is "load fresh terrain somewhat faster" against "some of that terrain is
    /// shaded wrong until it is rebuilt", which is not a trade worth taking by default - the
    /// same rule that retired the sun-query throttle and edge coalescing. `.komet toggle
    /// prebuild` switches it on live; the mismatch counter and hit rate are in `.komet`.
    ///
    /// If it is ever re-enabled by default, the thing to fix first is the staleness guard, not
    /// the canary rate: a canary mismatch means the guard ALREADY failed on that window, since
    /// rejecting stale windows is precisely its job.
    /// </summary>
    public bool TesselationWindowPipelining { get; set; }

    /// <summary>
    /// For the first N would-be pipeline hits, build the window BOTH ways and compare them
    /// element by element; any difference disables the pipeline for the session and is
    /// logged. Costs the speedup while it runs, proves equivalence on real world data.
    /// 0 turns validation off.
    /// </summary>
    public int TesselationPipelineValidateFirstN { get; set; } = 200;

    /// <summary>
    /// Decompress the neighbourhood of upcoming queue entries on a spare core before the
    /// tesselation thread needs it. Each tesselation touches 27 chunks; any that are packed
    /// get unpacked on the critical path otherwise. Idempotent and lock-protected - the
    /// engine's own compresschunks thread already does the same kind of concurrent access.
    /// </summary>
    public bool TesselationNeighbourPrefetch { get; set; } = true;

    /// <summary>
    /// Ask the runtime for SustainedLowLatency, which keeps the garbage collector from doing
    /// blocking, compacting gen2 collections while the game is running. Those are the
    /// collections long enough to show up as a dropped frame. The trade is a somewhat larger
    /// heap - the collector defers work rather than skipping it - so turn it off if the client
    /// is already close to running out of memory at your view distance.
    /// </summary>
    public bool LowLatencyGC { get; set; } = true;

    /// <summary>
    /// Release the GPU buffers of chunk mesh pools that have run empty. Each pool holds
    /// ~10 MB whether or not it contains geometry, and the engine never gives one back - so
    /// terrain video memory only ever grows to the session's peak, which after some flying
    /// around is far more than the view distance actually needs.
    /// </summary>
    public bool ReclaimEmptyPools { get; set; } = true;

    /// <summary>
    /// How many seconds a pool must stay empty before its memory is released. Pools empty and
    /// refill constantly while moving; a short window would trade video memory for a fresh
    /// allocation moments later.
    /// </summary>
    public double ReclaimEmptyPoolsAfterSeconds { get; set; } = 20.0;

    /// <summary>
    /// Slow the integrated server down when this client cannot keep up with meshing what it
    /// already received. Measured in a fresh world: 463 chunks/s arriving against 82/s
    /// tesselated - everything past the client's rate only grows the queue, hammers the chunk
    /// lock the tesselator needs, and spends cores on worldgen that the tesselation thread is
    /// waiting for. Singleplayer only; on a real server the client cannot pace anything.
    /// </summary>
    public bool AdaptiveChunkInflow { get; set; } = true;

    /// <summary>Tesselation backlog below which the server runs at full rate.</summary>
    public int InflowLowWaterChunks { get; set; } = 400;

    /// <summary>Backlog at which the server is throttled all the way down to one column per tick.</summary>
    public int InflowHighWaterChunks { get; set; } = 2000;

    // ---- server side (singleplayer: the integrated server runs in the same process) ----
    // These replace hand edits to servermagicnumbers.json: set in memory at every world
    // start, gone the moment the mod is removed. 0 always means "leave vanilla alone".

    /// <summary>
    /// Worldgen threads for the integrated server. AUTHORITATIVE: this value is applied at
    /// every world start and whatever servermagicnumbers.json says is ignored - one source
    /// of truth. 1 behaves exactly like vanilla; values are clamped to 1-6.
    /// The 6-thread flood that once collapsed client tesselation (3000+ chunks/s arriving,
    /// ~400 -> ~53/s meshed) predates the inflow brake: the brake now paces ACCEPTANCE, so
    /// worldgen threads only generate what the client asked for and extra threads shorten
    /// the generation latency instead of flooding. Default 6 since 1.30.0 - more of the
    /// machine's cores doing useful work while exploring. Lower this if the inflow brake
    /// (AdaptiveChunkInflow) is disabled, because then nothing paces delivery.
    /// </summary>
    public int ServerWorldgenThreads { get; set; } = 6;

    /// <summary>
    /// Server-side chunk request queue capacity (vanilla: 2000). At view distance 1536 more
    /// than 5000 columns can be in flight and the engine logs an overflow warning naming
    /// exactly this knob. Only ever raised, never lowered. 0 keeps the vanilla value.
    /// </summary>
    public int ServerRequestQueueSize { get; set; } = 4000;

    /// <summary>
    /// Chunk columns the server accepts into its load/generate queue per 20 ms tick
    /// (vanilla: 4, and local connections already get four times that). 0 keeps the vanilla
    /// value - which is the default here, because delivery has not been the bottleneck.
    /// </summary>
    public int ServerChunksColumnsPerTick { get; set; }

    /// <summary>
    /// Milliseconds to collect edge-only re-tesselation marks before issuing one per chunk.
    /// Every arriving chunk marks its six neighbours edge-only, so while a neighbourhood
    /// streams in packet by packet the same border chunk is re-meshed up to six times
    /// (measured: 6322 of 7244 marks/s were edge-only during loading). Coalescing turns
    /// that into one rebuild per chunk per window. Full marks, priority, relight and block
    /// changes are never delayed.
    ///
    /// OFF by default (0) since 1.36.0, by the same rule that retired the sun-query
    /// throttle: the stress test measured it at -0,12 ms (a small COST, not a saving), and
    /// it was prime suspect twice for visible holes along water chunk borders on fresh
    /// terrain - the edge repair of an early-meshed chunk already waits behind thousands
    /// of full tesselations, and this window stretches that visible gap further. The
    /// tesselation-thread relief (hundreds of thousands of absorbed rebuilds) never showed
    /// up as frame time. '.komet toggle edgecoal' enables it live for experiments.
    /// </summary>
    public double EdgeRetessCoalesceMs { get; set; }

    /// <summary>
    /// Move edge-only re-tesselation marks to the front of the tesselation queue, so visible
    /// border holes close before invisible new chunks mesh.
    ///
    /// On fresh terrain a chunk at the load front is meshed before its neighbour arrives, so
    /// the shared face is culled against the unknown - a visible hole, most obvious on the
    /// ocean surface. The engine's repair mark waits at the BACK of the queue, behind
    /// thousands of full tesselations of chunks nobody can see yet (~5 s at the measured
    /// 371 chunks/s). Promoting the repair changes only WHEN it runs, never what it produces:
    /// each promoted entry is work vanilla had already queued, handed to the same consumer.
    /// Capped so player block edits are never buried (at most 64 promotions per 50 ms, none
    /// while the priority queue is already busy). '.komet toggle edgeprio' flips it live -
    /// the A/B is judged by eye at the load front, not by frame time.
    /// </summary>
    public bool EdgeRetessPriority { get; set; } = true;

    /// <summary>
    /// Count every chunk dirty-mark, so the HUD can show marks per second and how many of them
    /// are edge-only. Three interlocked increments per mark; keep this on.
    /// </summary>
    public bool MeasureRetessSources { get; set; } = true;

    /// <summary>
    /// Additionally capture a stack for every 8th mark, so '.komet retess' can rank WHO keeps
    /// marking chunks dirty. Built for the settled-scene mystery of ~112 chunks/s re-tesselating
    /// at an empty queue, and it answered it.
    ///
    /// OFF by default since 1.42.0: resolving a captured frame's method metadata costs tens of
    /// microseconds, and while chunks stream in the marks arrive at thousands per second
    /// (measured 7244/s), so this was ~900 captures a second on the very threads doing the
    /// loading. '.komet toggle retess' switches it on while the question is open.
    /// </summary>
    public bool SampleRetessSources { get; set; }

    /// <summary>
    /// Main-thread milliseconds per frame that entity shape (re-)tesselations may take;
    /// anything beyond is deferred to the following frames (the entity keeps drawing its old
    /// mesh, a brand-new one appears a few frames later - vanilla already has that async gap).
    ///
    /// This is the look-around stutter: BeforeRender tesselates a stale entity shape only
    /// once it enters the frustum, so turning the camera across a freshly streamed area runs
    /// the expensive main-thread half (shape clone, clothing/armor StepParentShape, texture
    /// baking with atlas uploads) for many entities in one frame - measured 12-39 ms spikes
    /// at 600-1000 grad/s. At least one tesselation per frame always runs, so nothing
    /// starves. The player's own renderer is structurally unaffected. 0 = vanilla.
    /// </summary>
    public double EntityTesselationBudgetMs { get; set; } = 2.0;

    /// <summary>
    /// Replace the engine MeshDataRecycler's storage with a size-class pool behind the same
    /// API. The vanilla storage discards usable buffers two ways that peak exactly while
    /// chunks stream in (one mesh per size key - the fifth same-size buffer of a burst is
    /// thrown away; and a get only accepts a fit within 25 %), which feeds the measured
    /// ~380 MB/s of tesselation-thread allocation behind the GC-pause hitches while loading.
    /// Mesh content is byte-identical either way - CloneUsingRecycler copies into whatever
    /// buffer comes back - this only changes which buffer that is. The HUD/report row
    /// "mesh-recycler" shows the hit rate and the fresh-allocation rate either way it is
    /// answered. '.komet toggle recycler' flips it live.
    /// </summary>
    public bool FastMeshRecycler { get; set; } = true;

    /// <summary>
    /// Upper bound in MB on the buffers the replacement pool keeps for reuse. Vanilla's own
    /// storage holds 300-400 MB for the same purpose (its class comment says so), so the
    /// default is not new memory - it is the same reserve with the eviction actually enforced
    /// (oldest first, 15 s idle TTL).
    /// </summary>
    public int MeshRecyclerBudgetMb { get; set; } = 384;

    /// <summary>
    /// Make MeshData.CloneExtraData copy content instead of capacity. The engine clones a
    /// mesh's custom data parts with Values.Clone() - the FULL backing array. Chunk part
    /// clones copy from the tesselator's accumulation buffers, which grow to the high-water
    /// of the biggest chunk ever meshed and mostly carry CustomInts nobody wrote to - so
    /// every chunk paid high-water-sized copies of zeroes: measured 217 of 255 MB/s on the
    /// tesselation thread with the mesh recycler already at 100% hits. Uploads are driven by
    /// Count, not array length, so the only observable change is the garbage that no longer
    /// exists. Report row "klon-kompakt"; '.komet toggle tightclone' flips it live.
    /// </summary>
    public bool TightCustomClones { get; set; } = true;

    /// <summary>
    /// A frame is booked as a hitch (HUD row "ruckler", '.komet hitch', one log line) when it
    /// is at least this long in milliseconds AND at least HitchFrameFactor times the current
    /// average frame. Each hitch is recorded with its bucket breakdown, GC pause share, the
    /// camera's turn/move rate during exactly that frame, and - when the renderer profiler
    /// sampled that frame - its most expensive renderer. The floor keeps a 240 Hz target from
    /// flagging every 60 Hz frame; the factor keeps a heavy-but-steady scene quiet.
    /// </summary>
    public double HitchMinMs { get; set; } = 15.0;

    /// <summary>See HitchMinMs. 2.0 = a hitch is at least a doubled frame.</summary>
    public double HitchFrameFactor { get; set; } = 2.0;

    /// <summary>Log a one line summary of the culling statistics every N seconds. 0 disables.</summary>
    public int StatsLogIntervalSeconds { get; set; }
}
