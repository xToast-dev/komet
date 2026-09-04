using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Komet.Measure;

namespace Komet;

/// <summary>
/// Drop-in replacement for <see cref="MeshDataPool.FrustumCull"/>, the per-frame visibility
/// sweep over every tesselated chunk mesh part.
///
/// Vanilla walks List&lt;ModelDataPoolLocation&gt; and for each element calls
/// IsVisible -&gt; FrustumCulling.InFrustum* -&gt; Plane.AABBisOutside six times. Every one of
/// those calls copies a 24 byte Sphere and a 32 byte Plane by value and does three float
/// divisions by sqrt(3). Worse, the data lives in one heap object per mesh part plus a
/// separate Bools object, so the sweep is dominated by pointer chasing, not by arithmetic.
/// The client does this sweep about three times per frame (opaque, shadow far, shadow near)
/// over *all* geometry in memory - at viewDistance 1536 that is tens of thousands of parts.
///
/// This version keeps a per-pool struct-of-arrays copy of the cull geometry, so the common
/// case streams linearly through ~28 bytes per part and only touches the heap object for
/// the parts that survive the geometric test. The arithmetic itself is unchanged: the
/// expression tree of the plane test matches vanilla operation for operation, so the
/// visibility decision is bit-identical.
///
/// The cache is invalidated whenever a pool gains or loses a mesh part (see
/// <see cref="Invalidate"/>, wired up from the TryAdd / RemoveLocation patches), and is
/// rebuilt on the next sweep. Rebuilding costs less than a single vanilla sweep, so even a
/// pool that changes every frame comes out ahead.
/// </summary>
public static class FastCuller
{
    /// <summary>The constant vanilla's Plane.AABBisOutside divides the radii by.</summary>
    private const float Sqrt3 = 1.7320508f;

    // ---- configuration -------------------------------------------------------------

    /// <summary>
    /// Master switch, togglable at runtime. False hands every FrustumCull call straight back
    /// to vanilla - sweep, spatial index, batching, pool box and range merging all off in one
    /// place. This is what makes ".komet safemode" able to answer "is the mod drawing this
    /// wrong?" in seconds instead of a restart-per-guess bisection.
    /// </summary>
    public static bool Enabled = true;

    /// <summary>Time each sweep so the HUD can report milliseconds instead of event counts.</summary>
    public static bool MeasureTime = true;

    /// <summary>Reject a whole pool up front using its cached bounding box.</summary>
    public static bool PoolLevelCulling = true;

    /// <summary>
    /// Leave the far-LOD stand-in geometry out of the shadow passes where the camera pass
    /// does not draw it either. LOD 2 holds a block's detailed mesh and LOD 3 its simplified
    /// Lod2Mesh - two representations of the same block, and InFrustumAndRange picks exactly
    /// one by distance (LOD 3 only beyond lod2Bias, 640 blocks by default). The shadow passes
    /// apply no distance rule at all, so inside the shadow box - at most ~415 blocks - they
    /// rasterise *both* into the shadow map. This makes the shadow pass agree with the camera
    /// pass. Off by default because it does change what vanilla draws.
    /// </summary>
    public static bool ShadowSkipRedundantLod;

    /// <summary>
    /// Coalesce mesh parts that are adjacent in the index buffer into a single draw range.
    /// The engine pools chunks in tesselation order and ChunkTesselatorManager sorts that
    /// queue by distance, so the visible slice of a pool is made of long contiguous runs -
    /// each of which glMultiDrawElements is otherwise told to draw separately.
    /// </summary>
    public static bool MergeDrawRanges = true;

    /// <summary>
    /// Merge two visible runs even across a gap of INVISIBLE parts, when every byte of the gap
    /// belongs to a part whose box lies provably outside the frustum. The GPU clips those
    /// triangles before rasterisation - zero fragments, identical pixels - so the only cost is
    /// vertex work on a GPU that the measured frames leave ~80 % idle, and the saving is one
    /// draw range per bridged gap on the CPU side, where the frame is actually spent.
    ///
    /// What may NEVER be bridged, and why the proof is per part: a part rejected by the LOD
    /// distance band but inside the frustum (LOD 2 and LOD 3 are the same chunk twice - drawing
    /// both z-fights), a hidden part inside the frustum, a shadow part inside the light frustum
    /// but outside the shadow range (it would cast a shadow vanilla suppresses), and free bytes
    /// between allocations (stale indices render leftover geometry). The tiling walk refuses
    /// all of these: it only crosses bytes it can attribute to a part it has itself proven
    /// fully outside.
    /// </summary>
    public static bool GapMergeDrawRanges = true;

    /// <summary>
    /// Longest gap (in parts, by list index) the bridge walk will attempt. Bounds the extra
    /// per-gap work: each examined part costs one location dereference and one box/plane test.
    /// </summary>
    public static int GapMergeMaxParts = 8;

    // ---- statistics ----------------------------------------------------------------

    /// <summary>
    /// Set by the mod system. Called once, the first time a sweep actually runs, so the log
    /// can prove the patch is reached - a Harmony patch that applies cleanly but whose target
    /// is never called looks exactly like one that works.
    /// </summary>
    public static Action<string> Log;
    private static bool loggedFirstSweep;

    public static long StatSweeps;
    public static long StatPartsTested;
    public static long StatPoolsSkipped;
    public static long StatRebuilds;

    /// <summary>Mid-list inserts folded into the cache without a rebuild.</summary>
    public static long StatIncInserts;
    /// <summary>Parts taken out of a pool without a rebuild (RemoveLocation postfix).</summary>
    public static long StatIncRemovals;
    /// <summary>Kill switch for the incremental removal, so the fuzz test can prove the
    /// reference path and the field can fall back to rebuilds if it ever misbehaves.</summary>
    public static bool IncrementalRemoval = true;
    public static long StatRangesRaw;
    public static long StatRangesEmitted;

    /// <summary>Gaps bridged - each one is a draw range that no longer exists.</summary>
    public static long StatRangesBridged;

    /// <summary>Frustum-clipped parts drawn along inside bridged gaps, and their triangles.</summary>
    public static long StatPartsBridged;
    public static long StatTrisBridged;
    public static long StatCellsSkipped;
    public static long StatBucketsSkipped;

    /// <summary>
    /// Stopwatch ticks spent rebuilding pool caches, separated from the sweep itself. Without
    /// the split, "sichtbarkeit" lumps together two things that are fixed by opposite means -
    /// and guessing which one dominates has been wrong twice now.
    /// </summary>
    public static long StatRebuildTicks;

    /// <summary>Target edge length of a grid cell, in blocks.</summary>
    private const float BaseCellSize = 96f;
    private const int MaxCells = 4096;

    /// <summary>Parts a grid cell should hold on average - below this the cell test does not pay.</summary>
    /// <summary>
    /// Target parts per grid cell - what the cell edge length is derived from, since a fixed
    /// edge gets one or two parts per cell in a distance-ordered pool.
    ///
    /// 48 since v1.7, re-measured at 160 in 1.42.0, and now 32 - the last change being a
    /// correction of the *shape* rather than of the kernel.
    ///
    /// The 160 was tuned against a modelled pool shape of 96 pools x 1500 parts, inferred from
    /// a sweep count. A real report finally carried the shape itself: 177.269 parts in 600
    /// pools, i.e. 295 per pool - six times the pools and a fifth of the parts each. That is a
    /// different regime, not a different tuning: with 295 parts a target of 160 gives a pool
    /// one or two grid cells, so the spatial index the constant was tuned to exercise is barely
    /// present, and the per-sweep fixed cost dominates instead.
    ///
    /// The curve at the measured shape, best of three interleaved rounds, three independent runs:
    ///
    ///     16:1,55/1,59/1,54  24:1,35/1,39/1,42  32:1,17/1,20/1,20
    ///     48:1,27/1,34/1,34  96:1,24/1,27/1,26  160:1,39/1,39/1,40
    ///
    /// An interior optimum in every run, and 16 % below the old value. The benchmark also
    /// sweeps parts-per-pool, because the optimum moves with it: at 150 parts the curve is flat
    /// (the grid cannot help), at 600 the best is 24. So this constant is only as good as the
    /// shape it was measured at - which is why FastCuller now reports StatPartsHeld and
    /// StatPoolsLive, and the shape is a measurement rather than an estimate.
    ///
    /// Settable so the benchmark can sweep it, which it does on every run - a constant tuned
    /// against one kernel or one shape must not survive silently into the next.
    /// </summary>
    public static int PartsPerCellTarget = 32;

    // ---- reflection into the engine's internals ------------------------------------

    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> LocationsRef =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");

    private static readonly AccessTools.FieldRef<FrustumCulling, Plane[]> FrustumRef =
        AccessTools.FieldRefAccess<FrustumCulling, Plane[]>("frustum");

    private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> PlayerPosRef =
        AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");

    // ---- per pool cache ------------------------------------------------------------

    private sealed class PoolCache
    {
        public bool Dirty = true;
        public int Count;

        /// <summary>Which batch last culled this pool, so a stage does not cull it twice.</summary>
        public int BatchToken = -1;
        public bool Registered;

        /// <summary>
        /// Cull geometry in PLANAR order: six blocks of <see cref="GeoStride"/> floats, holding
        /// x, y, z, halfExtentX, halfExtentY, halfExtentZ in that order. Part k of block b is
        /// at <c>Geo[b * GeoStride + k]</c>.
        ///
        /// Planar rather than one six-float record per part, for two reasons. The vector path
        /// needs four consecutive parts' x in one register, which an interleaved layout can only
        /// produce by gathering. And in the camera pass most parts are rejected by the LOD
        /// distance band, which reads x and z alone: interleaved that pulls the whole 24-byte
        /// record through the cache, planar it costs 8 bytes.
        /// </summary>
        public float[] Geo = Array.Empty<float>();

        /// <summary>Distance between the six blocks of <see cref="Geo"/>, in floats.</summary>
        public int GeoStride;
        /// <summary>LOD level per part, 0-3.</summary>
        public byte[] Lod = Array.Empty<byte>();
        /// <summary>indicesStartByte, indicesLength, triangleCount per part.</summary>
        public int[] Meta = Array.Empty<int>();
        public ModelDataPoolLocation[] Locs = Array.Empty<ModelDataPoolLocation>();

        // There is deliberately no cached copy of CullVisible here any more. The engine assigns
        // it - and LodLevel - to the location AFTER MeshDataPool.TryAdd has returned:
        //
        //     ModelDataPoolLocation loc = pools.AddModel(...);   // our TryAdd postfix fires here
        //     loc.CullVisible = cullVisible;                     // the chunk's shared Bools
        //     loc.LodLevel = lodLevel;
        //
        // so anything read from inside that postfix sees the constructor's defaults: a private
        // Bools(true, true) that no occlusion pass will ever write to, and LodLevel 0. Both were
        // snapshotted by NoteInserted, and LodLevel 0 means "invisible unless the LOD0 setting
        // is on" - so every part TrySqueezeInbetween squeezed into a fragmented pool vanished
        // from the camera pass and cast no far shadow until something forced a rebuild. That is
        // the flickering. Both fields now come off the location at sweep time, where they are
        // correct; the location object is dereferenced there anyway for Hide, so it is free.

        /// <summary>Sum of all part triangle counts - AllocatedTris never varies between sweeps.</summary>
        public int AllocatedTris;

        // ---- spatial index -------------------------------------------------------
        // Geo and Lod are stored in *cell* order so a cell's members stream contiguously;
        // Orig maps back to the original index, which is what Meta/Locs/Cull and the output
        // order use. Buckets are (cell, lodLevel) pairs, so a cell can be rejected either by
        // the frustum or by the LOD distance band without touching a single part.
        public int[] Orig = Array.Empty<int>();
        public int[] BucketStart = Array.Empty<int>();
        public float[] CellBox = Array.Empty<float>();
        public int CellCount;
        public ulong[] VisBits = Array.Empty<ulong>();

        /// <summary>
        /// x, z and the bucket index per part, in original order. Written once in pass 1 and
        /// read by the two passes that follow, so the grid build never chases the location
        /// objects again - during chunk streaming this rebuild runs for a dozen pools a frame
        /// and it was reading the same heap objects three times over.
        /// </summary>
        public float[] Scratch = Array.Empty<float>();
        public int[] ScratchBucket = Array.Empty<int>();

        // ---- append overflow ------------------------------------------------------
        // Parts added since the last full rebuild. They are not in the grid, so the sweep
        // walks them linearly after the cell loop - a few dozen parts against a rebuild that
        // has to re-read every location object in the pool. Kept in original order, their
        // index being GridCount + k, which is what Meta/Locs/Cull and the output already use.
        public int GridCount;
        public float[] OverGeo = Array.Empty<float>();
        /// <summary>Original index of each overflow part. For pure appends this equals
        /// GridCount + k, but a mid-list insert lands at an arbitrary index - storing it
        /// explicitly is what lets inserts share the overflow instead of forcing rebuilds.</summary>
        public int[] OverOrig = Array.Empty<int>();
        public int OverCount;

        /// <summary>Set by the TryAdd postfix when the new part went on the end of the list.</summary>
        public bool Appended;

        public bool HasBox;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>
        /// Back reference used to validate a memo slot. Weak on purpose: the memo holds the
        /// cache strongly, so a strong link back to the pool here would keep dead pools - and
        /// their geometry arrays - alive for as long as the slot is not overwritten.
        /// </summary>
        public WeakReference<MeshDataPool> Owner;
    }

    private static readonly ConditionalWeakTable<MeshDataPool, PoolCache> Caches = new();

    /// <summary>
    /// Direct-mapped memo in front of the ConditionalWeakTable.
    ///
    /// Every FrustumCull call has to get from a pool to its cache, and at view distance 1536
    /// there are several thousand of those a frame - one per pool per render stage - of which
    /// all but the first per stage do no work at all. The table lookup was then the single
    /// biggest item in that no-op path. The memo turns it into one array read plus an identity
    /// check; it is cleared whenever a new stage key appears, so it can never keep a pool's
    /// arrays alive for longer than one stage.
    /// </summary>
    private const int MemoSlots = 1024; // power of two
    private static readonly PoolCache[] Memo = new PoolCache[MemoSlots];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PoolCache Lookup(MeshDataPool pool)
    {
        var slot = RuntimeHelpers.GetHashCode(pool) & (MemoSlots - 1);
        var hit = Memo[slot];
        if (hit != null)
        {
            var owner = hit.Owner;
            if (owner != null && owner.TryGetTarget(out var same) && ReferenceEquals(same, pool)) return hit;
        }

        var c = Caches.GetOrCreateValue(pool);
        // Only the render thread ever gets here - the parallel batch is handed its caches
        // rather than looking them up - and the identity check above is what makes a slot
        // trustworthy in any case, so a stale slot costs a lookup and nothing else.
        c.Owner ??= new WeakReference<MeshDataPool>(pool);
        Memo[slot] = c;
        return c;
    }

    // ---- batched parallel culling --------------------------------------------------

    /// <summary>
    /// Cull every pool for a render stage in one parallel pass instead of one at a time.
    ///
    /// MeshDataPoolManager.Render interleaves the cull with GL draw calls, so the sweep cannot
    /// be parallelised where it stands. But a stage uses a single cull mode and a single
    /// frustum, and pools only ever touch their own arrays - so the first sweep of a stage can
    /// do all of them across every core, and the remaining calls in that stage then find their
    /// results already there. That turns roughly eight thousand short sequential sweeps a frame
    /// into a handful of parallel batches.
    /// </summary>
    public static bool Parallel = true;

    /// <summary>0 = auto.</summary>
    public static int MaxThreads;

    /// <summary>
    /// The threads the batch runs on. Dedicated rather than the shared ThreadPool: see
    /// <see cref="WorkerSet"/> for why - in short, the game queues chunk tesselation on the
    /// same pool, and a sweep that has to wait for thread injection is a dropped frame.
    /// </summary>
    public static readonly WorkerSet Workers = new("komet-cull");

    /// <summary>Reused across batches so a sweep allocates nothing at all.</summary>
    private sealed class BatchBody : IWorkBody
    {
        public MeshDataPool[] Pools;
        public PoolCache[] Caches;
        public FrustumCulling Culler;
        public EnumFrustumCullMode Mode;

        public void Run(int from, int to)
        {
            Stats st = default;
            for (var i = from; i < to; i++) CullCore(Pools[i], Caches[i], Culler, Mode, ref st);
            Flush(ref st);
        }
    }

    private static readonly BatchBody batchBody = new();

    private static readonly List<WeakReference<MeshDataPool>> KnownPools = new();
    private static MeshDataPool[] batchBuffer = Array.Empty<MeshDataPool>();
    private static PoolCache[] batchCaches = Array.Empty<PoolCache>();
    private static int batchCount;

    /// <summary>
    /// Mesh parts a batch has to have before it is worth spreading over the thread pool.
    /// A real client at view distance 1536 carries several hundred thousand, so this only
    /// takes effect early in a session and at small view distances.
    /// </summary>
    private const int ParallelPartThreshold = 50_000;


    /// <summary>
    /// Bumped by a postfix on FrustumCulling.CalcFrustumEquations - the only thing that moves
    /// the planes. Without it a batch would be reused across the frame boundary: the last
    /// stage of one frame and the first of the next share a culler and a mode, so nothing
    /// would signal that the camera had moved.
    /// </summary>
    public static int FrustumGeneration;

    private static FrustumCulling batchCuller;
    private static EnumFrustumCullMode batchMode = (EnumFrustumCullMode)(-1);
    private static int batchGeneration = -1;
    private static int batchVisBuf = -1;
    private static int batchToken;
    private static int batchStamp;
    private static int keyCulls;
    private static bool keyBatched;

    /// <summary>
    /// How many pools have to be culled with the same key before batching the rest.
    ///
    /// Not every caller sweeps hundreds of pools: SystemRenderDecals culls a single pool with
    /// CullInstant in the middle of the opaque stage. Batching on the first call would make
    /// that one decal pool drag every terrain pool through a parallel sweep. Waiting for a
    /// handful of sequential culls first tells the two cases apart at no cost.
    /// </summary>
    private const int BatchThreshold = 8;

    public static long StatBatches;

    /// <summary>
    /// Mesh parts held across every live pool, as of the last batch - not parts *tested*, which
    /// pool and cell rejection make much smaller.
    ///
    /// This exists because <see cref="PartsPerCellTarget"/> was tuned against a modelled pool
    /// shape that turned out to be 5.8x off in pool count, and the benchmark says the optimum
    /// moves with parts per pool: at 150 the curve is flat, at 300 the best value is 32, at 600
    /// it is 24, and the 160 in use is best at none of them. The pool count came from a real
    /// report; this is the other half of that shape, so the constant can be set from a
    /// measurement instead of from a guess.
    /// </summary>
    public static long StatPartsHeld;

    /// <summary>Live pools at the last batch - the denominator for <see cref="StatPartsHeld"/>.</summary>
    public static int StatPoolsLive;

    /// <summary>
    /// Counters accumulated without atomics and flushed once per thread. Twelve threads doing
    /// Interlocked.Add on the same eight fields per pool turned into cache line ping-pong that
    /// cost more than the culling itself.
    /// </summary>
    private struct Stats
    {
        public long Sweeps, Parts, Cells, Buckets, RangesRaw, RangesEmitted, PoolsSkipped, Rebuilds, RebuildTicks;
        public long RangesBridged, PartsBridged, TrisBridged;
    }

    private static void Flush(ref Stats st)
    {
        System.Threading.Interlocked.Add(ref StatSweeps, st.Sweeps);
        System.Threading.Interlocked.Add(ref StatPartsTested, st.Parts);
        System.Threading.Interlocked.Add(ref StatCellsSkipped, st.Cells);
        System.Threading.Interlocked.Add(ref StatBucketsSkipped, st.Buckets);
        System.Threading.Interlocked.Add(ref StatRangesRaw, st.RangesRaw);
        System.Threading.Interlocked.Add(ref StatRangesEmitted, st.RangesEmitted);
        System.Threading.Interlocked.Add(ref StatRangesBridged, st.RangesBridged);
        System.Threading.Interlocked.Add(ref StatPartsBridged, st.PartsBridged);
        System.Threading.Interlocked.Add(ref StatTrisBridged, st.TrisBridged);
        System.Threading.Interlocked.Add(ref StatPoolsSkipped, st.PoolsSkipped);
        System.Threading.Interlocked.Add(ref StatRebuilds, st.Rebuilds);
        System.Threading.Interlocked.Add(ref StatRebuildTicks, st.RebuildTicks);
        st = default;
    }

    private static long rebuildTicksReported, rebuildsReported;

    /// <summary>
    /// Hands this frame's rebuild share to the frame accounting, so a hitch line can say
    /// whether a long sweep was rebuilding caches or sweeping. Runs on the render thread after
    /// a batch has fully returned, so every worker's Flush has landed in the totals; a reset of
    /// the totals ('.komet reset') shows up as a negative delta and is simply skipped.
    /// </summary>
    private static void ReportRebuilds()
    {
        var ticks = System.Threading.Interlocked.Read(ref StatRebuildTicks);
        var count = System.Threading.Interlocked.Read(ref StatRebuilds);
        var dTicks = ticks - rebuildTicksReported;
        var dCount = count - rebuildsReported;
        rebuildTicksReported = ticks;
        rebuildsReported = count;
        if (dTicks > 0 && dCount >= 0) FrameStats.AddCullRebuild(dTicks, (int)dCount);
    }

    /// <summary>Called when a pool is seen for the first time, so the batch knows about it.</summary>
    private static void Register(MeshDataPool pool)
    {
        lock (KnownPools) KnownPools.Add(new WeakReference<MeshDataPool>(pool));
    }

    /// <summary>
    /// A render stage begins whenever the culler or the mode changes. Everything alive gets
    /// culled now, in parallel; the individual calls that follow are then no-ops.
    /// </summary>
    private static void RunBatch(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        batchToken++;

        var live = 0;
        long parts = 0;
        lock (KnownPools)
        {
            if (batchBuffer.Length < KnownPools.Count)
            {
                batchBuffer = new MeshDataPool[KnownPools.Count + 64];
                batchCaches = new PoolCache[KnownPools.Count + 64];
            }

            var write = 0;
            for (var i = 0; i < KnownPools.Count; i++)
            {
                if (KnownPools[i].TryGetTarget(out var p))
                {
                    // Resolving the cache here also fills the memo, so the thousands of no-op
                    // FrustumCull calls that follow this batch never touch the weak table.
                    var c = Lookup(p);
                    batchCaches[live] = c;
                    batchBuffer[live++] = p;
                    parts += c.Count;
                    KnownPools[write++] = KnownPools[i]; // compact away collected pools
                }
            }
            if (write < KnownPools.Count) KnownPools.RemoveRange(write, KnownPools.Count - write);
        }

        // Drop the tail: those slots hold the last batch's pools, and a strong reference in a
        // scratch buffer is enough to keep a pool that the world has already unloaded - along
        // with its geometry - out of the collector's hands.
        if (live < batchBuffer.Length)
        {
            Array.Clear(batchBuffer, live, batchBuffer.Length - live);
            Array.Clear(batchCaches, live, batchCaches.Length - live);
        }

        batchCount = live;
        // Already summed for the parallel threshold above, so recording it is free.
        StatPartsHeld = parts;
        StatPoolsLive = live;
        if (live == 0) return;

        StatBatches++;
        var threads = Workers.ThreadCount;

        // Going wide has to be paid for before the first part is tested: waking the helpers
        // costs tens of microseconds. Below a real workload that is more than the sweep itself,
        // which is why the threshold counts mesh parts and not just pools.
        if (threads < 1 || live < 32 || parts < ParallelPartThreshold)
        {
            Stats st = default;
            for (var i = 0; i < live; i++) CullCore(batchBuffer[i], batchCaches[i], culler, mode, ref st);
            Flush(ref st);
            return;
        }

        // Slices, not one item per thread. Pools differ in size by orders of magnitude, so a
        // static partition leaves threads idle while one of them finishes the big pool; the
        // workers take the next slice as they come free. Small enough that the tail is short,
        // large enough that the interlocked hand-out disappears next to the work in it.
        batchBody.Pools = batchBuffer;
        batchBody.Caches = batchCaches;
        batchBody.Culler = culler;
        batchBody.Mode = mode;

        var waitBefore = Workers.StatWaitTicks;
        try
        {
            Workers.Run(batchBody, live, Math.Max(1, live / (threads * 8)));
        }
        finally
        {
            // Only the render thread ever fires a batch, so this reads its own delta.
            if (MeasureTime) FrameStats.AddCullWaitTicks(Workers.StatWaitTicks - waitBefore);
            batchBody.Pools = null;
            batchBody.Caches = null;
            batchBody.Culler = null;
        }
    }

    /// <summary>
    /// Forces the reflection field accessors to resolve now, so a renamed engine field fails
    /// at patch time - where it can be caught and the optimisation skipped - rather than
    /// inside the render loop as a TypeInitializationException.
    /// </summary>
    /// <summary>
    /// Brings the cull threads up. Called once from the mod system; the benchmark calls it too,
    /// which is the only reason the batch does not start them lazily - a lazy start would put
    /// six thread creations inside whichever frame happened to be first.
    /// </summary>
    public static void StartWorkers()
        => Workers.Start(MaxThreads > 0 ? MaxThreads : WorkerSet.AutoThreads(1));

    public static void EnsureReady()
    {
        if (LocationsRef == null || FrustumRef == null || PlayerPosRef == null)
            throw new InvalidOperationException("MeshDataPool/FrustumCulling internals not found");
    }

    /// <summary>Called from the TryAdd / RemoveLocation patches when a pool's contents change.</summary>
    public static void Invalidate(MeshDataPool pool)
    {
        if (Caches.TryGetValue(pool, out var c)) c.Dirty = true;
    }

    public static void InvalidateAll()
    {
        // ConditionalWeakTable has no bulk access; dropping our reference is enough because
        // every entry is recreated lazily and starts out dirty.
        foreach (var kv in Caches) kv.Value.Dirty = true;
    }

    /// <summary>
    /// Drops the batch's record of which pools exist. Only for the benchmark, which builds
    /// several independent sets of pools in one process: KnownPools is pruned by the GC, so a
    /// previous section's pools stay in the batch and every later measurement is charged for
    /// culling them too. The game never wants this - there a pool going away is exactly what
    /// the weak reference already handles.
    /// </summary>
    public static void ForgetAllPools()
    {
        lock (KnownPools) KnownPools.Clear();
        Array.Clear(Memo, 0, MemoSlots);
        batchStamp = int.MinValue;
        batchCuller = null;
        batchMode = (EnumFrustumCullMode)(-1);
        batchGeneration = -1;
        keyCulls = 0;
        keyBatched = false;
    }

    /// <summary>
    /// Called from the TryAdd postfix when the new part landed at the *end* of the pool's
    /// list. That case - and only that case - leaves every existing index untouched, so the
    /// spatial index built for them is still valid and the new part can simply be carried
    /// alongside it. An insert in the middle (TrySqueezeInbetween, above 3 % fragmentation)
    /// shifts every following index and still needs the full rebuild.
    /// </summary>
    public static void NoteAppended(MeshDataPool pool)
    {
        if (Caches.TryGetValue(pool, out var c) && !c.Dirty) c.Appended = true;
    }

    /// <summary>Parts allowed to accumulate outside the grid before a rebuild is worth it.</summary>
    private static int OverflowLimit(int n) => Math.Max(48, n >> 4);

    private static void EnsureOverflowCapacity(PoolCache c, int over)
    {
        if (c.OverGeo.Length >= over * 6 && c.OverOrig.Length >= over) return;
        var overCap = Math.Max(64, over * 2);
        Array.Resize(ref c.OverGeo, overCap * 6);
        Array.Resize(ref c.OverOrig, overCap);
    }

    /// <summary>
    /// Folds a part that TrySqueezeInbetween inserted into the MIDDLE of the pool's list -
    /// the case that used to force a full rebuild, nine-plus times a frame while streaming
    /// near a fragmented base (measured 36 rebuilds/frame at ~0.14 ms each). All original
    /// indices at or past the insert position move up by one: the flat per-part arrays shift
    /// with three Array.Copy calls, the grid's index mapping gets one sequential +1 pass -
    /// no location object is ever re-read, which is what made the rebuild expensive. The new
    /// part itself rides in the overflow with its true index, exactly like an appended one.
    ///
    /// Falls back to Invalidate (a rebuild on the next sweep) whenever the cache is not in
    /// exact sync with the list - pending appends, prior invalidation, a full overflow - so
    /// every skipped assumption costs a rebuild, never correctness.
    /// </summary>
    public static void NoteInserted(MeshDataPool pool, ModelDataPoolLocation loc)
    {
        if (!Caches.TryGetValue(pool, out var c)) return; // no cache yet - first cull builds it
        if (c.Dirty) return;

        var locations = LocationsRef(pool);
        if (locations == null) { c.Dirty = true; return; }

        var n = c.Count;
        // Pending appends (parts the cache has not folded yet) always sit at the END of the
        // list, so an insert in front of them shifts them by exactly one and Extend - which
        // reads from c.Count upwards - still finds exactly the pending ones. Without pending
        // appends the list has to be in exact sync. This used to bail to a rebuild whenever an
        // append was pending, which while streaming is nearly always: the uploads land in the
        // Before stage, the squeeze-inserts and removals run in Opaque, and the first sweep
        // that could fold the appends comes after both.
        var expected = n + 1;
        if (c.Appended ? locations.Count < expected : locations.Count != expected) { c.Dirty = true; return; }

        var p = locations.IndexOf(loc);
        if (p < 0) { c.Dirty = true; return; }
        if (p >= n) { c.Appended = true; return; }   // at or behind the boundary: a pending append now

        if (c.OverCount + 1 > OverflowLimit(n + 1)) { c.Dirty = true; return; }

        EnsureCapacity(c, n + 1);
        EnsureOverflowCapacity(c, c.OverCount + 1);

        Array.Copy(c.Meta, p * 3, c.Meta, (p + 1) * 3, (n - p) * 3);
        Array.Copy(c.Locs, p, c.Locs, p + 1, n - p);

        // Only the fields the engine has already filled in by the time TryAdd returns are read
        // here: the sphere and the index range come out of InsertAt's object initialiser, while
        // CullVisible and LodLevel are assigned by the caller afterwards - see the note on
        // PoolCache. Those two are read at sweep time instead, off c.Locs[i].
        var s2 = loc.FrustumCullSphere;
        var ex = s2.radius / Sqrt3;
        var ey = s2.radiusY / Sqrt3;
        var ez = s2.radiusZ / Sqrt3;

        var len = loc.IndicesEnd - loc.IndicesStart;
        var m = p * 3;
        c.Meta[m] = loc.IndicesStart * 4;
        c.Meta[m + 1] = len;
        c.Meta[m + 2] = len / 3;
        c.Locs[p] = loc;
        c.AllocatedTris += len / 3;

        var orig = c.Orig;
        for (int k = 0, gridCount = c.GridCount; k < gridCount; k++)
            if (orig[k] >= p) orig[k]++;
        var overOrig = c.OverOrig;
        for (int k = 0, overCount = c.OverCount; k < overCount; k++)
            if (overOrig[k] >= p) overOrig[k]++;

        var g = c.OverCount * 6;
        c.OverGeo[g] = s2.x;
        c.OverGeo[g + 1] = s2.y;
        c.OverGeo[g + 2] = s2.z;
        c.OverGeo[g + 3] = ex;
        c.OverGeo[g + 4] = ey;
        c.OverGeo[g + 5] = ez;
        c.OverOrig[c.OverCount] = p;
        c.OverCount++;

        if (s2.x - ex < c.MinX) c.MinX = s2.x - ex;
        if (s2.y - ey < c.MinY) c.MinY = s2.y - ey;
        if (s2.z - ez < c.MinZ) c.MinZ = s2.z - ez;
        if (s2.x + ex > c.MaxX) c.MaxX = s2.x + ex;
        if (s2.y + ey > c.MaxY) c.MaxY = s2.y + ey;
        if (s2.z + ez > c.MaxZ) c.MaxZ = s2.z + ez;

        c.Count = n + 1;
        c.HasBox = true;
        System.Threading.Interlocked.Increment(ref StatIncInserts);
    }

    /// <summary>
    /// The list index the engine is about to remove, or -1 when it is not in the list. Called
    /// from the RemoveLocation PREFIX: List.Remove works by reference and the index is only
    /// knowable before it runs; the postfix then hands it to <see cref="NoteRemoved"/>. -2
    /// means "no cache involved", so the postfix has nothing to do.
    /// </summary>
    public static int IndexBeforeRemoval(MeshDataPool pool, ModelDataPoolLocation loc)
    {
        if (!IncrementalRemoval) return -2;
        if (!Caches.TryGetValue(pool, out var c) || c.Dirty) return -2;
        var locations = LocationsRef(pool);
        if (locations == null) return -2;
        return locations.IndexOf(loc);
    }

    /// <summary>
    /// Takes one part out of the cache after MeshDataPool.RemoveLocation removed it from the
    /// list - the mirror image of <see cref="NoteInserted"/>, and the last routine reason a
    /// pool was still rebuilt from scratch. Every re-tesselated chunk removes its old parts
    /// (three frames delayed, then RemoveLocationsNow at the start of the opaque stage), and a
    /// rebuild re-reads every location object in the pool - one cache miss each - to lose a
    /// handful of them. Chunk unloads while walking do the same to most pools at once.
    ///
    /// The part's grid slot is closed by shifting the cell-ordered arrays down by one and
    /// lowering every bucket boundary past it; a part in the overflow is replaced by the last
    /// overflow entry (that list is unordered by construction). Then the original-order arrays
    /// shift, every index above the part drops by one - one sequential pass over flat ints, no
    /// location object is touched - and the count follows. The pool and cell boxes are NOT
    /// shrunk: a box that bounds the parts plus one that is gone is still a bound, only a
    /// looser one, and the next full rebuild (still forced by any deviation) tightens it.
    ///
    /// Pending appends are tolerated like in NoteInserted: they sit past c.Count, so a removal
    /// in front of them just moves them down and Extend reads them from the lowered count.
    /// Any inconsistency - an index the cache does not hold, a count that does not add up -
    /// falls back to a rebuild, never to a wrong answer.
    /// </summary>
    public static void NoteRemoved(MeshDataPool pool, int p)
    {
        if (p == -2) return;
        if (!Caches.TryGetValue(pool, out var c)) return;
        if (c.Dirty) return;
        if (p < 0) { c.Dirty = true; return; }

        var n = c.Count;
        if (p >= n) return; // a pending append the cache never folded; Extend simply will not see it

        var locations = LocationsRef(pool);
        if (locations == null) { c.Dirty = true; return; }
        var expected = n - 1;
        if (c.Appended ? locations.Count < expected : locations.Count != expected) { c.Dirty = true; return; }

        var orig = c.Orig;
        var gridCount = c.GridCount;
        var pos = -1;
        for (var k = 0; k < gridCount; k++)
            if (orig[k] == p) { pos = k; break; }

        if (pos >= 0)
        {
            // the bucket holding pos is the last one whose start is <= pos; every boundary
            // after it moves down by one, the sentinel at [buckets] included
            var bucketStart = c.BucketStart;
            var buckets = c.CellCount * 4;
            var b = 0;
            for (var q = 1; q <= buckets; q++)
            {
                if (bucketStart[q] > pos) break;
                b = q;
            }
            var tail = gridCount - pos - 1;
            if (tail > 0)
            {
                var geo = c.Geo;
                var gs = c.GeoStride;
                for (var blk = 0; blk < 6; blk++)
                    Array.Copy(geo, blk * gs + pos + 1, geo, blk * gs + pos, tail);
                Array.Copy(c.Lod, pos + 1, c.Lod, pos, tail);
                Array.Copy(orig, pos + 1, orig, pos, tail);
            }
            for (var q = b + 1; q <= buckets; q++) bucketStart[q]--;
            c.GridCount = --gridCount;
        }
        else
        {
            var overOrig = c.OverOrig;
            var over = c.OverCount;
            var k2 = -1;
            for (var k = 0; k < over; k++)
                if (overOrig[k] == p) { k2 = k; break; }
            if (k2 < 0) { c.Dirty = true; return; } // the cache does not hold this index at all
            var last = over - 1;
            if (k2 != last)
            {
                Array.Copy(c.OverGeo, last * 6, c.OverGeo, k2 * 6, 6);
                overOrig[k2] = overOrig[last];
            }
            c.OverCount = last;
        }

        var meta = c.Meta;
        c.AllocatedTris -= meta[p * 3 + 2];
        var tailN = n - p - 1;
        if (tailN > 0)
        {
            Array.Copy(meta, (p + 1) * 3, meta, p * 3, tailN * 3);
            Array.Copy(c.Locs, p + 1, c.Locs, p, tailN);
        }
        c.Locs[n - 1] = null; // the engine dropped it; a stale strong reference here would keep it alive

        for (var k = 0; k < gridCount; k++)
            if (orig[k] > p) orig[k]--;
        var overOrig2 = c.OverOrig;
        for (int k = 0, overCount = c.OverCount; k < overCount; k++)
            if (overOrig2[k] > p) overOrig2[k]--;

        c.Count = n - 1;
        System.Threading.Interlocked.Increment(ref StatIncRemovals);
    }

    /// <summary>
    /// Takes the newly appended parts into the cache without touching the ones already there.
    ///
    /// A full rebuild costs one cache miss per part in the pool - it has to read every
    /// ModelDataPoolLocation object again - which at three thousand parts measured ~128 us.
    /// While terrain streams in that ran nine times a frame, 1.15 ms of an 8.5 ms frame, for
    /// what is usually a handful of new parts.
    /// </summary>
    private static void Extend(PoolCache c, List<ModelDataPoolLocation> locations)
    {
        var n = locations.Count;
        var added = n - c.Count;

        EnsureCapacity(c, n);

        EnsureOverflowCapacity(c, c.OverCount + added);

        for (var i = c.Count; i < n; i++)
        {
            var loc = locations[i];
            var s2 = loc.FrustumCullSphere;

            var ex = s2.radius / Sqrt3;
            var ey = s2.radiusY / Sqrt3;
            var ez = s2.radiusZ / Sqrt3;

            var len = loc.IndicesEnd - loc.IndicesStart;
            var m = i * 3;
            c.Meta[m] = loc.IndicesStart * 4;
            c.Meta[m + 1] = len;
            c.Meta[m + 2] = len / 3;

            c.Locs[i] = loc;
            c.AllocatedTris += len / 3;

            var g = c.OverCount * 6;
            c.OverGeo[g] = s2.x;
            c.OverGeo[g + 1] = s2.y;
            c.OverGeo[g + 2] = s2.z;
            c.OverGeo[g + 3] = ex;
            c.OverGeo[g + 4] = ey;
            c.OverGeo[g + 5] = ez;

            c.OverOrig[c.OverCount] = i;
            c.OverCount++;

            // the pool box has to grow with them, or the whole-pool rejection could throw
            // away parts that are in view
            if (s2.x - ex < c.MinX) c.MinX = s2.x - ex;
            if (s2.y - ey < c.MinY) c.MinY = s2.y - ey;
            if (s2.z - ez < c.MinZ) c.MinZ = s2.z - ez;
            if (s2.x + ex > c.MaxX) c.MaxX = s2.x + ex;
            if (s2.y + ey > c.MaxY) c.MaxY = s2.y + ey;
            if (s2.z + ez > c.MaxZ) c.MaxZ = s2.z + ez;
        }

        c.Count = n;
        c.HasBox = n > 0;
        c.Appended = false;
    }

    /// <summary>
    /// Grows every per-part array to the same capacity, or none of them.
    ///
    /// Two crashes came out of this one spot. First Extend grew only the arrays it writes and
    /// Rebuild trusted c.Locs.Length as a stand-in for all of them. The fix - checking each
    /// array separately in Rebuild but still growing only the short ones - was worse: the
    /// arrays then ended up with genuinely different capacities, Extend's own c.Locs check
    /// passed, and Array.Clear ran off the end of the shorter VisBits instead.
    ///
    /// So capacity is decided in exactly one place, for all arrays together. Either every
    /// array holds at least n parts or every array is regrown to the same figure; they cannot
    /// disagree about how many parts the cache holds because nothing sizes them apart.
    /// </summary>
    private static void EnsureCapacity(PoolCache c, int n)
    {
        if (c.Locs.Length >= n && c.Orig.Length >= n && c.Lod.Length >= n
            && c.ScratchBucket.Length >= n && c.Meta.Length >= n * 3
            && c.GeoStride >= n && c.Scratch.Length >= n * 6
            && c.VisBits.Length >= (n + 63) >> 6)
            return;

        var cap = Math.Max(64, n + (n >> 1));
        Array.Resize(ref c.Meta, cap * 3);
        Array.Resize(ref c.Locs, cap);
        Array.Resize(ref c.Orig, cap);
        Array.Resize(ref c.Lod, cap);
        Array.Resize(ref c.Scratch, cap * 6);
        Array.Resize(ref c.ScratchBucket, cap);
        Array.Resize(ref c.VisBits, (cap + 63) >> 6);

        // Geo is planar, so its stride IS the capacity: growing it moves every block, which
        // Array.Resize cannot do. Only the grid's GridCount entries hold live data - the rest
        // is either overflow (a separate array) or not written yet - so that is all that moves.
        // Getting this wrong is silent: the arrays would still be big enough and every read
        // would land in the neighbouring axis.
        if (c.GeoStride < cap)
        {
            var geo = new float[cap * 6];
            var keep = Math.Min(c.GridCount, c.GeoStride);
            if (keep > 0)
                for (var b = 0; b < 6; b++) Array.Copy(c.Geo, b * c.GeoStride, geo, b * cap, keep);
            c.Geo = geo;
            c.GeoStride = cap;
        }
    }

    private static void Rebuild(PoolCache c, List<ModelDataPoolLocation> locations, ref Stats st)
    {
        var rebuildStart = MeasureTime ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        st.Rebuilds++;
        var n = locations.Count;

        EnsureCapacity(c, n);

        var meta = c.Meta;
        var locs = c.Locs;

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        var allocated = 0;

        var scratch = c.Scratch;

        // pass 1: the per-part data that stays in original order, plus the pool's bounds.
        // This is the only pass that touches the location objects; everything after it reads
        // the flat scratch copy.
        for (var i = 0; i < n; i++)
        {
            var loc = locations[i];
            var s2 = loc.FrustumCullSphere;

            var ex = s2.radius / Sqrt3;
            var ey = s2.radiusY / Sqrt3;
            var ez = s2.radiusZ / Sqrt3;

            var len = loc.IndicesEnd - loc.IndicesStart;
            var m = i * 3;
            meta[m] = loc.IndicesStart * 4;
            meta[m + 1] = len;
            meta[m + 2] = len / 3;

            locs[i] = loc;
            allocated += len / 3;

            var g0 = i * 6;
            scratch[g0] = s2.x;
            scratch[g0 + 1] = s2.y;
            scratch[g0 + 2] = s2.z;
            scratch[g0 + 3] = ex;
            scratch[g0 + 4] = ey;
            scratch[g0 + 5] = ez;
            // LOD is packed alongside so pass 2 needs nothing but the scratch arrays
            var lodLevel = loc.LodLevel;
            c.ScratchBucket[i] = (uint)lodLevel < 4u ? lodLevel : 3;

            if (s2.x - ex < minX) minX = s2.x - ex;
            if (s2.y - ey < minY) minY = s2.y - ey;
            if (s2.z - ez < minZ) minZ = s2.z - ez;
            if (s2.x + ex > maxX) maxX = s2.x + ex;
            if (s2.y + ey > maxY) maxY = s2.y + ey;
            if (s2.z + ez > maxZ) maxZ = s2.z + ez;
        }

        c.Count = n;
        c.GridCount = n;
        c.OverCount = 0;
        c.Appended = false;
        c.AllocatedTris = allocated;
        c.MinX = minX; c.MinY = minY; c.MinZ = minZ;
        c.MaxX = maxX; c.MaxY = maxY; c.MaxZ = maxZ;
        c.HasBox = n > 0;
        c.Dirty = false;

        if (n == 0)
        {
            c.CellCount = 0;
            if (MeasureTime) st.RebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStart;
            return;
        }

        // ---- grid over X/Z ----
        // Pools are filled in tesselation order and that queue is distance sorted, so a pool's
        // parts form a wide ring rather than a compact blob. A fixed cell size therefore ends
        // up with one or two parts per cell, where the per-cell test costs more than the parts
        // it saves. Size the grid by part count instead, aiming for a few dozen parts per cell.
        var targetCells = Math.Clamp(n / PartsPerCellTarget, 1, MaxCells);
        var area = (double)(maxX - minX) * (maxZ - minZ);
        var cellSize = area > 0 ? (float)Math.Max(BaseCellSize, Math.Sqrt(area / targetCells)) : BaseCellSize;

        int gx, gz;
        while (true)
        {
            gx = Math.Max(1, (int)((maxX - minX) / cellSize) + 1);
            gz = Math.Max(1, (int)((maxZ - minZ) / cellSize) + 1);
            if ((long)gx * gz <= MaxCells) break;
            cellSize *= 2f;
        }
        var cellCount = gx * gz;
        c.CellCount = cellCount;

        var buckets = cellCount * 4;
        if (c.BucketStart.Length < buckets + 1) c.BucketStart = new int[buckets + 1];
        if (c.CellBox.Length < cellCount * 6) c.CellBox = new float[cellCount * 6];
        if (cursor == null || cursor.Length < buckets) cursor = new int[buckets];

        var bucketStart = c.BucketStart;
        var cellBox = c.CellBox;
        Array.Clear(bucketStart, 0, buckets + 1);

        // pass 2: how many parts land in each (cell, LOD) bucket. The bucket index is kept
        // so pass 3 does not have to compute it a second time.
        var scratchBucket = c.ScratchBucket;
        for (var i = 0; i < n; i++)
        {
            var g0 = i * 6;
            var b = BucketOf(scratch[g0], scratch[g0 + 2], scratchBucket[i], minX, minZ, cellSize, gx, gz);
            scratchBucket[i] = b;
            bucketStart[b + 1]++;
        }
        for (var b = 1; b <= buckets; b++) bucketStart[b] += bucketStart[b - 1];

        // pass 3: scatter into cell order and accumulate each cell's box
        for (var cell = 0; cell < cellCount; cell++)
        {
            var o = cell * 6;
            cellBox[o] = cellBox[o + 1] = cellBox[o + 2] = float.MaxValue;
            cellBox[o + 3] = cellBox[o + 4] = cellBox[o + 5] = float.MinValue;
        }
        Array.Copy(bucketStart, cursor, buckets);

        var geo = c.Geo;
        var lod = c.Lod;
        var orig = c.Orig;
        var gs = c.GeoStride;

        for (var i = 0; i < n; i++)
        {
            var b = scratchBucket[i];
            var pos = cursor[b]++;

            var g0 = i * 6;
            float sx = scratch[g0], sy = scratch[g0 + 1], sz = scratch[g0 + 2];
            float ex = scratch[g0 + 3], ey = scratch[g0 + 4], ez = scratch[g0 + 5];

            geo[pos] = sx;
            geo[gs + pos] = sy;
            geo[2 * gs + pos] = sz;
            geo[3 * gs + pos] = ex;
            geo[4 * gs + pos] = ey;
            geo[5 * gs + pos] = ez;

            // The out-of-range LOD levels vanilla treats as permanently invisible share
            // bucket 3, so the real level still has to come from the location itself.
            var l = locs[i].LodLevel;
            lod[pos] = (byte)((uint)l < 4u ? l : 255);
            orig[pos] = i;

            var o = (b >> 2) * 6;
            if (sx - ex < cellBox[o]) cellBox[o] = sx - ex;
            if (sy - ey < cellBox[o + 1]) cellBox[o + 1] = sy - ey;
            if (sz - ez < cellBox[o + 2]) cellBox[o + 2] = sz - ez;
            if (sx + ex > cellBox[o + 3]) cellBox[o + 3] = sx + ex;
            if (sy + ey > cellBox[o + 4]) cellBox[o + 4] = sy + ey;
            if (sz + ez > cellBox[o + 5]) cellBox[o + 5] = sz + ez;
        }

        // store the cell boxes the way the plane test wants them: centre plus half extent
        for (var cell = 0; cell < cellCount; cell++)
        {
            if (bucketStart[cell * 4] == bucketStart[cell * 4 + 4]) continue;
            var o = cell * 6;
            float x0 = cellBox[o], y0 = cellBox[o + 1], z0 = cellBox[o + 2];
            float x1 = cellBox[o + 3], y1 = cellBox[o + 4], z1 = cellBox[o + 5];
            cellBox[o] = (x0 + x1) * 0.5f;
            cellBox[o + 1] = (y0 + y1) * 0.5f;
            cellBox[o + 2] = (z0 + z1) * 0.5f;
            cellBox[o + 3] = (x1 - x0) * 0.5f;
            cellBox[o + 4] = (y1 - y0) * 0.5f;
            cellBox[o + 5] = (z1 - z0) * 0.5f;
        }

        if (MeasureTime) st.RebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStart;
    }

    /// <summary>Grid cell times four, plus the LOD level - out of range LODs share bucket 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketOf(float x, float z, int lodLevel, float minX, float minZ, float cellSize, int gx, int gz)
    {
        var cx = (int)((x - minX) / cellSize);
        var cz = (int)((z - minZ) / cellSize);
        if ((uint)cx >= (uint)gx) cx = cx < 0 ? 0 : gx - 1;
        if ((uint)cz >= (uint)gz) cz = cz < 0 ? 0 : gz - 1;
        var l = (uint)lodLevel < 4u ? lodLevel : 3;
        return ((cz * gx + cx) << 2) + l;
    }

    // ---- the plane test ------------------------------------------------------------

    /// <summary>A frustum plane with the per-axis sign of its normal folded in.</summary>
    private struct FastPlane
    {
        public double Nx, Ny, Nz, D;
        public float Sx, Sy, Sz;
    }

    /// <summary>
    /// The same plane with every coefficient broadcast to four lanes, so the vector path can
    /// use them as register operands instead of reloading and splatting them per group.
    /// </summary>
    private struct FastPlaneV
    {
        public Vector256<double> Nx, Ny, Nz, D, Sx, Sy, Sz;
    }

    /// <summary>
    /// Test four mesh parts per instruction instead of one, using 256 bit double lanes.
    ///
    /// Settable so the scalar path can be measured against it in the same process - both are
    /// compiled in, both are exercised by the benchmark's equivalence run, and the whole point
    /// of the vector path is that it decides identically. Defaults to on wherever AVX exists
    /// and is forced off on anything else; '.komet toggle simd' flips it live.
    /// </summary>
    public static bool VectorCulling = Avx.IsSupported;

    /// <summary>Whether the vector path may run at all on this CPU, regardless of the switch.</summary>
    public static bool VectorAvailable => Avx.IsSupported;

    [ThreadStatic] private static FastPlane[] tlsPlanes;
    [ThreadStatic] private static FastPlaneV[] tlsPlanesV;
    [ThreadStatic] private static int[] cursor;
    [ThreadStatic] private static double[] tlsLo;
    [ThreadStatic] private static double[] tlsHi;

    // Which culler and which frustum generation the cached planes and LOD bounds belong to.
    // A batch sweeps hundreds of pools through the same planes; rebuilding them per pool was
    // ~50 stores of pure repetition per call, and at view distance 1536 there are thousands
    // of calls a frame.
    [ThreadStatic] private static FrustumCulling tlsPlaneCuller;
    [ThreadStatic] private static int tlsPlaneGen;
    // The LOD bounds get their own key on the values themselves, not on the generation:
    // ClientMain.MainRenderLoop assigns lod0BiasSq and lod2BiasSq *after* calling
    // CalcFrustumEquations, so the generation does not actually cover them.
    [ThreadStatic] private static FrustumCulling tlsLodCuller;
    [ThreadStatic] private static float tlsLod0Bias;
    [ThreadStatic] private static double tlsLod2Bias;
    [ThreadStatic] private static int tlsLodViewDistSq;

    /// <summary>
    /// Turns FrustumCulling.InFrustumAndRange's per-LOD switch into "distSq &gt; lo &amp;&amp; distSq &lt; hi".
    /// LOD 2's inclusive bound becomes exclusive via BitIncrement, which is exact for any
    /// finite bound, and an impossible range (negative infinity) encodes "never visible".
    /// </summary>
    private static void BuildLodBounds(FrustumCulling culler, double[] lo, double[] hi)
    {
        var lod0BiasSq = culler.lod0BiasSq;
        double viewDistSq = culler.ViewDistanceSq;
        var lod2BiasSq = culler.lod2BiasSq;

        // lodLevel 0: lod0BiasSq > 0 && distSq < lod0BiasSq + 1024
        lo[0] = double.NegativeInfinity;
        hi[0] = lod0BiasSq > 0f ? lod0BiasSq + 1024f : double.NegativeInfinity;
        // lodLevel 1: distSq < ViewDistanceSq
        lo[1] = double.NegativeInfinity;
        hi[1] = viewDistSq;
        // lodLevel 2: distSq <= lod2BiasSq
        lo[2] = double.NegativeInfinity;
        hi[2] = Math.BitIncrement(lod2BiasSq);
        // lodLevel 3: distSq > lod2BiasSq && distSq < ViewDistanceSq
        lo[3] = lod2BiasSq;
        hi[3] = viewDistSq;
        // Entries 4..255 encode "vanilla's default case returns false" and never change, so
        // they are written once when the table is allocated - filling them on every sweep cost
        // 504 stores per pool per pass, which at ~8000 sweeps a frame was pure overhead.
    }

    private static double[] NewLodTable(double fill)
    {
        var table = new double[256];
        for (var i = 4; i < 256; i++) table[i] = fill;
        return table;
    }

    /// <summary>
    /// The planes only move when FrustumCulling.CalcFrustumEquations runs, which is exactly
    /// what FrustumGeneration counts - the same fact the batch key already relies on. So a
    /// thread that has already converted them for this generation can reuse them as they are.
    /// </summary>
    private static FastPlane[] LoadPlanes(FrustumCulling culler)
    {
        var dst = tlsPlanes;
        if (dst != null && ReferenceEquals(tlsPlaneCuller, culler) && tlsPlaneGen == FrustumGeneration)
            return dst;

        dst = tlsPlanes ??= new FastPlane[6];
        var dstV = Avx.IsSupported ? tlsPlanesV ??= new FastPlaneV[6] : null;
        var frustum = FrustumRef(culler);
        for (var i = 0; i < 6; i++)
        {
            var s = frustum[i];
            ref var d = ref dst[i];
            d.Nx = s.normalX;
            d.Ny = s.normalY;
            d.Nz = s.normalZ;
            d.D = s.D;
            // vanilla: (normalX > 0.0) ? 1 : -1, so a zero normal component maps to -1
            d.Sx = s.normalX > 0.0 ? 1f : -1f;
            d.Sy = s.normalY > 0.0 ? 1f : -1f;
            d.Sz = s.normalZ > 0.0 ? 1f : -1f;

            if (dstV == null) continue;
            ref var v = ref dstV[i];
            v.Nx = Vector256.Create(d.Nx);
            v.Ny = Vector256.Create(d.Ny);
            v.Nz = Vector256.Create(d.Nz);
            v.D = Vector256.Create(d.D);
            // +-1 is exact in both widths, so widening the sign here changes no rounding:
            // ex * (+-1.0) as double is bit for bit (double)(ex * (+-1f)).
            v.Sx = Vector256.Create((double)d.Sx);
            v.Sy = Vector256.Create((double)d.Sy);
            v.Sz = Vector256.Create((double)d.Sz);
        }

        tlsPlaneCuller = culler;
        tlsPlaneGen = FrustumGeneration;
        return dst;
    }

    /// <summary>Four consecutive floats widened to four doubles. CVTPS2PD, always exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Widen(ref float geo, nuint at)
        => Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref geo, at));

    /// <summary>
    /// The frustum test for four parts at once, returning one bit per part.
    ///
    /// Bit for bit the same decision as <see cref="InFrustum5"/> / <see cref="InFrustum6"/>:
    /// every lane performs the same multiplications and additions in the same associativity,
    /// with no FMA contraction - explicit Avx.Multiply/Avx.Add cannot be fused by the JIT, and
    /// a fused multiply-add would round once where the scalar version rounds twice.
    ///
    /// The "inside" test is AndNot of the (d &lt; 0) mask rather than (d &gt;= 0), because those
    /// two differ on NaN and vanilla's is the former: Plane.AABBisOutside returns
    /// <c>dist &lt; 0</c>, so a NaN distance counts as inside.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FrustumMask(FastPlaneV[] p, int planes,
        Vector256<double> x, Vector256<double> y, Vector256<double> z,
        Vector256<double> ex, Vector256<double> ey, Vector256<double> ez)
    {
        var zero = Vector256<double>.Zero;
        var mask = Vector256<double>.AllBitsSet;
        for (var i = 0; i < planes; i++)
        {
            ref var q = ref p[i];
            var ax = Avx.Add(x, Avx.Multiply(ex, q.Sx));
            var ay = Avx.Add(y, Avx.Multiply(ey, q.Sy));
            var az = Avx.Add(z, Avx.Multiply(ez, q.Sz));
            var d = Avx.Add(
                Avx.Add(Avx.Add(Avx.Multiply(ax, q.Nx), Avx.Multiply(ay, q.Ny)), Avx.Multiply(az, q.Nz)),
                q.D);
            mask = Avx.AndNot(Avx.CompareLessThan(d, zero), mask);
        }
        return Avx.MoveMask(mask);
    }

    /// <summary>
    /// Identical to Plane.AABBisOutside. Vanilla computes sign*radius/sqrt(3) as float, which
    /// is exactly +/- (radius/sqrt(3)), so hoisting the division out of the plane loop and
    /// folding the sign into a multiplier reproduces the same rounding, term for term.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Outside(ref FastPlane p, float x, float y, float z, float ex, float ey, float ez)
    {
        return ((double)x + ex * p.Sx) * p.Nx
             + ((double)y + ey * p.Sy) * p.Ny
             + ((double)z + ez * p.Sz) * p.Nz
             + p.D < 0.0;
    }

    /// <summary>
    /// Signed distance of the AABB's near corner to the plane; &lt; 0 means fully outside.
    /// Same expression, same order, same rounding as Plane.AABBisOutside.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Dist(ref FastPlane p, float x, float y, float z, float ex, float ey, float ez)
    {
        return ((double)x + ex * p.Sx) * p.Nx
             + ((double)y + ey * p.Sy) * p.Ny
             + ((double)z + ez * p.Sz) * p.Nz
             + p.D;
    }

    /// <summary>All six planes - FrustumCulling.InFrustum and InFrustumShadowPass.</summary>
    /// <remarks>
    /// Combined with non-short-circuiting &amp; so the five or six independent dot products
    /// pipeline instead of forming a chain of mispredictable branches. The NaN behaviour of
    /// vanilla ("not &lt; 0" counts as inside) is preserved by negating the same comparison.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InFrustum6(FastPlane[] p, float x, float y, float z, float ex, float ey, float ez)
    {
        ref var p0 = ref p[0];
        ref var p1 = ref p[1];
        ref var p2 = ref p[2];
        ref var p3 = ref p[3];
        ref var p4 = ref p[4];
        ref var p5 = ref p[5];
        return !(Dist(ref p0, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p1, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p2, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p3, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p4, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p5, x, y, z, ex, ey, ez) < 0.0);
    }

    /// <summary>Planes 0..4 - InFrustumAndRange deliberately leaves out the far plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InFrustum5(FastPlane[] p, float x, float y, float z, float ex, float ey, float ez)
    {
        ref var p0 = ref p[0];
        ref var p1 = ref p[1];
        ref var p2 = ref p[2];
        ref var p3 = ref p[3];
        ref var p4 = ref p[4];
        return !(Dist(ref p0, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p1, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p2, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p3, x, y, z, ex, ey, ez) < 0.0)
             & !(Dist(ref p4, x, y, z, ex, ey, ez) < 0.0);
    }

    /// <summary>
    /// True when the box is *entirely* on the inside of every plane, so no part inside it can
    /// possibly fail the frustum test.
    ///
    /// Outside() checks the corner furthest along the normal - if even that is behind the
    /// plane, nothing is in front of it. Flipping the sign of the extents checks the opposite
    /// corner instead: if the *nearest* corner is still in front of every plane, the whole box
    /// is, and so is everything it contains. That turns the per-part frustum test into dead
    /// work for every pool and cell sitting well inside the view - which, close to the camera
    /// and in the ortho shadow projections, is most of them.
    ///
    /// Deliberately &gt;= rather than !(&lt; 0): a NaN must answer "not certainly inside" and
    /// fall through to the per-part test, never claim the box is safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllInside5(FastPlane[] p, float x, float y, float z, float ex, float ey, float ez)
    {
        return Dist(ref p[0], x, y, z, -ex, -ey, -ez) >= 0.0
             & Dist(ref p[1], x, y, z, -ex, -ey, -ez) >= 0.0
             & Dist(ref p[2], x, y, z, -ex, -ey, -ez) >= 0.0
             & Dist(ref p[3], x, y, z, -ex, -ey, -ez) >= 0.0
             & Dist(ref p[4], x, y, z, -ex, -ey, -ez) >= 0.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllInside6(FastPlane[] p, float x, float y, float z, float ex, float ey, float ez)
    {
        return AllInside5(p, x, y, z, ex, ey, ez)
             & Dist(ref p[5], x, y, z, -ex, -ey, -ez) >= 0.0;
    }

    /// <summary>
    /// Appends one visible mesh part, extending the previous range instead of starting a new
    /// one when the two are back to back in the index buffer. glMultiDrawElements renders the
    /// concatenation of its ranges in order, so merging two adjacent ones draws exactly the
    /// same triangles in the same sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit(int[] starts, int[] sizes, int[] meta, int i,
                             ref int group, ref int rendered, ref int prevEndByte, bool merge)
    {
        var m = i * 3;
        var startByte = meta[m];
        var len = meta[m + 1];

        if (merge && group > 0 && prevEndByte == startByte)
        {
            sizes[group - 1] += len;
        }
        else
        {
            starts[group * 2] = startByte;
            sizes[group] = len;
            group++;
        }

        prevEndByte = startByte + len * 4;
        rendered += meta[m + 2];
    }

    /// <summary>
    /// Widens the previous draw range across the gap in front of visible part <paramref name="i"/>,
    /// if the gap can be proven safe - see <see cref="GapMergeDrawRanges"/>. The proof is a
    /// tiling walk: starting at the previous range's end byte, each invisible part between the
    /// two visible ones (in list order) that continues the byte chain must have its box fully
    /// outside the frustum. Only when the chain closes the gap exactly is the range extended;
    /// a chain broken by free bytes, an out-of-order layout, or a part inside the frustum
    /// commits nothing. On success Emit's ordinary back-to-back merge then absorbs part i.
    ///
    /// The box test is the same FastPlane math as the sweep's, on the same
    /// FrustumCullSphere-derived extents, so "outside" here never contradicts "visible" there.
    /// </summary>
    private static void TryBridge(int[] meta, ModelDataPoolLocation[] locs, FastPlane[] planes, bool fivePlanes,
                                  int prevI, int i, ref int prevEndByte, int[] sizes, int group, ref Stats st)
    {
        var startByte = meta[i * 3];
        var gapBytes = startByte - prevEndByte;
        if (gapBytes <= 0 || i - prevI - 1 > GapMergeMaxParts) return;

        var cursor = prevEndByte;
        var parts = 0;
        var tris = 0;
        for (var j = prevI + 1; j < i && cursor < startByte; j++)
        {
            var m = j * 3;
            if (meta[m] != cursor) continue;

            var s = locs[j].FrustumCullSphere;
            var ex = s.radius / Sqrt3;
            var ey = s.radiusY / Sqrt3;
            var ez = s.radiusZ / Sqrt3;
            var inFrustum = fivePlanes
                ? InFrustum5(planes, s.x, s.y, s.z, ex, ey, ez)
                : InFrustum6(planes, s.x, s.y, s.z, ex, ey, ez);
            if (inFrustum) return;

            cursor += meta[m + 1] * 4;
            parts++;
            tris += meta[m + 2];
        }
        if (cursor != startByte) return;

        sizes[group - 1] += gapBytes / 4;
        prevEndByte = startByte;
        st.RangesBridged++;
        st.PartsBridged += parts;
        st.TrisBridged += tris;
    }

    // ---- entry point ---------------------------------------------------------------

    public static void Cull(MeshDataPool pool, FrustumCulling culler, EnumFrustumCullMode mode)
    {
        if (!Parallel)
        {
            var ts = MeasureTime ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Stats seq = default;
            CullCore(pool, Lookup(pool), culler, mode, ref seq);
            Flush(ref seq);
            if (MeasureTime)
            {
                FrameStats.AddCullTicks(System.Diagnostics.Stopwatch.GetTimestamp() - ts);
                ReportRebuilds();
            }
            CullVerifier.Maybe(pool, culler, mode);
            return;
        }

        var c = Lookup(pool);
        var visBuf = ModelDataPoolLocation.VisibleBufIndex;

        // A new render stage means new planes, a new mode, or the occlusion culler having
        // flipped its visibility buffer. Any of the three invalidates a batch.
        if (!ReferenceEquals(culler, batchCuller) || mode != batchMode
            || FrustumGeneration != batchGeneration || visBuf != batchVisBuf)
        {
            batchCuller = culler;
            batchMode = mode;
            batchGeneration = FrustumGeneration;
            batchVisBuf = visBuf;
            keyCulls = 0;
            keyBatched = false;

            // Retire the old stamp. Without this the pools culled before the batch fires
            // still carry the *previous* stage's token, which happens to equal the stale
            // batchStamp - so they would be treated as already done and keep the previous
            // stage's visibility. With the shadow pass running just before the opaque one
            // that shows up as holes in the terrain.
            batchStamp = int.MinValue;

            // Drop memo slots belonging to the stage that just ended, so a pool that has since
            // been collected cannot keep its geometry arrays alive. Repopulated by the batch.
            Array.Clear(Memo, 0, MemoSlots);
            Memo[RuntimeHelpers.GetHashCode(pool) & (MemoSlots - 1)] = c;
        }

        var fireBatch = !keyBatched && ++keyCulls >= BatchThreshold;

        // A pool that gained or lost parts since the batch, or was created after it, is stale
        // and has to be redone - otherwise a freshly tesselated chunk would not render until
        // the next stage.
        var stale = c.BatchToken != batchStamp || c.Dirty || c.Count != LocationsRef(pool).Count;

        // Nothing to do. Leaving before the clock is read matters: all but one of the several
        // thousand FrustumCull calls a frame end here, and two Stopwatch reads apiece (a vDSO
        // clock_gettime each) added up to more than the sweep they were supposed to measure.
        //
        // The verifier still gets a look, and this is the most valuable place for it: the pool
        // already holds this stage's ranges, computed by the batch, and these are the calls the
        // engine draws from. Checking only the call that happened to fire the batch would leave
        // ninety pools in ninety-one unverified.
        if (!fireBatch && !stale) { CullVerifier.Maybe(pool, culler, mode); return; }

        var t0 = MeasureTime ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        if (fireBatch)
        {
            keyBatched = true;
            RunBatch(culler, mode);
            batchStamp = batchToken;
            stale = c.BatchToken != batchStamp || c.Dirty || c.Count != LocationsRef(pool).Count;
        }

        if (stale)
        {
            Stats st = default;
            CullCore(pool, c, culler, mode, ref st);
            Flush(ref st);
        }

        if (MeasureTime)
        {
            FrameStats.AddCullTicks(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            ReportRebuilds();
        }

        // Outside the timed region and only ever on this thread: the batch's worker threads
        // must not run a checker that walks the same pool lists they are culling.
        CullVerifier.Maybe(pool, culler, mode);
    }

    private static void CullCore(MeshDataPool pool, PoolCache c, FrustumCulling culler, EnumFrustumCullMode mode, ref Stats st)
    {
        if (!c.Registered) { c.Registered = true; Register(pool); }
        c.BatchToken = batchToken;
        var live = LocationsRef(pool);
        // The Dirty flag is set from the RemoveLocation patch and anything else that can
        // reorder the list. The count check is a belt-and-braces guard in case some other code
        // path ever mutates the list directly - that case has to rebuild, because nothing
        // vouches for the existing indices.
        if (c.Dirty)
        {
            Rebuild(c, live, ref st);
        }
        else if (c.Count != live.Count)
        {
            if (c.Appended && live.Count > c.Count
                && c.OverCount + (live.Count - c.Count) <= OverflowLimit(live.Count))
                Extend(c, live);
            else
                Rebuild(c, live, ref st);
        }

        st.Sweeps++;
        if (!loggedFirstSweep)
        {
            loggedFirstSweep = true;
            Log?.Invoke($"visibility sweep is live ({live.Count} mesh parts in the first pool)");
        }

        var n = c.Count;
        if (n == 0)
        {
            pool.indicesGroupsCount = 0;
            pool.RenderedTriangles = 0;
            pool.AllocatedTris = 0;
            return;
        }

        var starts = pool.indicesStartsByte;
        var sizes = pool.indicesSizes;
        var meta = c.Meta;
        var group = 0;
        var rendered = 0;

        // NoCull: vanilla's IsVisible falls through to "return !Hide".
        if (mode == EnumFrustumCullMode.NoCull)
        {
            var all = c.Locs;
            var mergeNo = MergeDrawRanges;
            var prevNo = -1;
            var rawNo = 0;
            for (var i = 0; i < n; i++)
            {
                if (all[i].Hide) continue;
                rawNo++;
                Emit(starts, sizes, meta, i, ref group, ref rendered, ref prevNo, mergeNo);
            }
            st.RangesRaw += rawNo;
            st.RangesEmitted += group;
            pool.indicesGroupsCount = group;
            pool.RenderedTriangles = rendered;
            pool.AllocatedTris = c.AllocatedTris;
            return;
        }

        var planes = LoadPlanes(culler);

        // set when the pool box sits entirely inside the frustum - see AllInside5
        var poolFullyInside = false;

        // Whole-pool rejection. The cached box is the union of every part's box, so if the
        // box fails a plane every part inside it fails the same plane.
        if (PoolLevelCulling && c.HasBox)
        {
            var cx = (c.MinX + c.MaxX) * 0.5f;
            var cy = (c.MinY + c.MaxY) * 0.5f;
            var cz = (c.MinZ + c.MaxZ) * 0.5f;
            var hx = (c.MaxX - c.MinX) * 0.5f;
            var hy = (c.MaxY - c.MinY) * 0.5f;
            var hz = (c.MaxZ - c.MinZ) * 0.5f;

            var boxVisible = mode == EnumFrustumCullMode.CullNormal
                ? InFrustum5(planes, cx, cy, cz, hx, hy, hz)
                : InFrustum6(planes, cx, cy, cz, hx, hy, hz);

            if (!boxVisible)
            {
                st.PoolsSkipped++;
                pool.indicesGroupsCount = 0;
                pool.RenderedTriangles = 0;
                pool.AllocatedTris = c.AllocatedTris;
                return;
            }

            poolFullyInside = mode == EnumFrustumCullMode.CullNormal
                ? AllInside5(planes, cx, cy, cz, hx, hy, hz)
                : AllInside6(planes, cx, cy, cz, hx, hy, hz);
        }

        var geo = c.Geo;
        var lod = c.Lod;
        var orig = c.Orig;
        var bucketStart = c.BucketStart;
        var cellBox = c.CellBox;
        var locs = c.Locs;
        var bits = c.VisBits;
        var merge = MergeDrawRanges;
        var prevEndByte = -1;
        var rawRanges = 0;

        // Offsets of the six planar blocks in Geo. x sits at 0, so it needs no name.
        var gs = c.GeoStride;
        int oY = gs, oZ = 2 * gs, oEX = 3 * gs, oEY = 4 * gs, oEZ = 5 * gs;

        var words = (n + 63) >> 6;
        Array.Clear(bits, 0, words);

        // Read the double-buffer index once. ChunkCuller flips it from the chunkculling
        // worker thread, so vanilla can observe two different values inside one sweep;
        // sampling it once here is if anything more consistent, not less.
        var visBuf = ModelDataPoolLocation.VisibleBufIndex;
        var normalMode = mode == EnumFrustumCullMode.CullNormal;
        var multiCell = c.CellCount > 1;
        var tested = 0;
        var cellsSkipped = 0;
        var bucketsSkipped = 0;

        // ---- mode-specific setup, hoisted out of the cell loop ----
        var ppos = PlayerPosRef(culler);
        double px = ppos.X, pz = ppos.Z;
        var loBound = tlsLo ??= NewLodTable(0.0);
        var hiBound = tlsHi ??= NewLodTable(double.NegativeInfinity);
        if (normalMode && !(ReferenceEquals(tlsLodCuller, culler)
                            && tlsLod0Bias.Equals(culler.lod0BiasSq)
                            && tlsLod2Bias.Equals(culler.lod2BiasSq)
                            && tlsLodViewDistSq == culler.ViewDistanceSq))
        {
            BuildLodBounds(culler, loBound, hiBound);
            tlsLodCuller = culler;
            tlsLod0Bias = culler.lod0BiasSq;
            tlsLod2Bias = culler.lod2BiasSq;
            tlsLodViewDistSq = culler.ViewDistanceSq;
        }

        var farPass = mode == EnumFrustumCullMode.CullInstantShadowPassFar;
        var shadowMode = farPass || mode == EnumFrustumCullMode.CullInstantShadowPassNear;

        // The LOD 3 stand-in is only ever drawn beyond lod2Bias. If the whole pool is nearer
        // than that, every LOD 3 part in it is geometry the camera pass does not draw, and
        // whose detailed counterpart (LOD 1 or LOD 2) is already in this shadow map. Testing
        // the pool's farthest corner keeps it exact: one comparison for the whole sweep.
        var skipLod3 = false;
        if (shadowMode && ShadowSkipRedundantLod && c.HasBox)
        {
            var fx = Math.Max(Math.Abs(px - c.MinX), Math.Abs(px - c.MaxX));
            var fz = Math.Max(Math.Abs(pz - c.MinZ), Math.Abs(pz - c.MaxZ));
            skipLod3 = fx * fx + fz * fz <= culler.lod2BiasSq;
        }
        float ppx = ppos.X, ppz = ppos.Z;
        var rangeX = culler.shadowRangeX;
        var rangeZ = culler.shadowRangeZ;

        // ---- vector setup, also hoisted out of the cell loop ----
        // LoadPlanes filled the broadcast copies alongside the scalar ones under the same
        // cache key, so these two are never out of step.
        var vector = VectorCulling && Avx.IsSupported;
        var planesV = vector ? tlsPlanesV : null;
        ref var geoRef = ref MemoryMarshal.GetArrayDataReference(geo);
        Vector256<double> pxV = default, pzV = default, rangeXV = default, rangeZV = default;
        Vector128<float> ppxV = default, ppzV = default, absMask = default;
        if (vector)
        {
            if (normalMode) { pxV = Vector256.Create(px); pzV = Vector256.Create(pz); }
            if (shadowMode)
            {
                ppxV = Vector128.Create(ppx);
                ppzV = Vector128.Create(ppz);
                // Math.Abs on a float is exactly a cleared sign bit
                absMask = Vector128.Create(0x7FFFFFFF).AsSingle();
                rangeXV = Vector256.Create(rangeX);
                rangeZV = Vector256.Create(rangeZ);
            }
        }

        // A cell's box bounds every part inside it, so a cell that fails a plane - or whose
        // nearest point is already out of the LOD band or the shadow box - takes all of its
        // parts with it. This is what stops the sweep from touching every mesh part in memory.
        for (int cell = 0, cellCount = c.CellCount; cell < cellCount; cell++)
        {
            var cellFrom = bucketStart[cell << 2];
            var cellTo = bucketStart[(cell << 2) + 4];
            if (cellFrom == cellTo) continue;

            var cb = cell * 6;
            float ccx = cellBox[cb], ccy = cellBox[cb + 1], ccz = cellBox[cb + 2];
            float chx = cellBox[cb + 3], chy = cellBox[cb + 4], chz = cellBox[cb + 5];

            // A cell inside a pool that is itself fully inside needs no test at all.
            //
            // The same "fully inside" test per *cell* was tried and measured slower: it costs
            // five plane evaluations per ~48 parts whether or not it hits, and in a view that
            // cuts across the pools - the normal case - it hits rarely enough that the whole
            // sweep lost 5-10 %. At pool level it is five evaluations per sweep against tens
            // of thousands of parts, so it can only ever pay.
            if (!poolFullyInside)
            {
                // with one cell the box is the pool box, which the caller already tested
                if (multiCell && !(normalMode
                        ? InFrustum5(planes, ccx, ccy, ccz, chx, chy, chz)
                        : InFrustum6(planes, ccx, ccy, ccz, chx, chy, chz)))
                {
                    cellsSkipped++;
                    continue;
                }
            }

            if (shadowMode)
            {
                // nearest distance from the player to the cell box, per axis
                double nearX = Math.Abs(ppx - ccx) - chx;
                double nearZ = Math.Abs(ppz - ccz) - chz;
                // one block of slack so rounding can never reject a cell that still has a part
                if (nearX - 1.0 >= rangeX || nearZ - 1.0 >= rangeZ) { cellsSkipped++; continue; }
            }

            double cellMinSq = 0, cellMaxSq = 0;
            if (normalMode)
            {
                var ndx = Math.Abs(px - ccx) - chx; if (ndx < 0) ndx = 0;
                var ndz = Math.Abs(pz - ccz) - chz; if (ndz < 0) ndz = 0;
                cellMinSq = ndx * ndx + ndz * ndz;
                var fdx = Math.Abs(px - ccx) + chx;
                var fdz = Math.Abs(pz - ccz) + chz;
                cellMaxSq = fdx * fdx + fdz * fdz;
            }

            for (var l = 0; l < 4; l++)
            {
                var bs = bucketStart[(cell << 2) + l];
                var be = bucketStart[(cell << 2) + l + 1];
                if (bs == be) continue;

                // vanilla checks LodLevel >= 1 last in the far shadow pass; a pure AND, so
                // rejecting the whole bucket up front is free
                if (farPass && l < 1) continue;
                if (skipLod3 && l == 3) { bucketsSkipped++; continue; }

                if (normalMode)
                {
                    // every part in this bucket shares the cell's distance band, so if the
                    // band misses the LOD's range entirely the bucket cannot contribute
                    double lo = loBound[l], hi = hiBound[l];
                    if (cellMinSq - 4.0 >= hi || cellMaxSq + 4.0 <= lo) { bucketsSkipped++; continue; }
                }

                tested += be - bs;

                var k = bs;

                // ---- four parts per iteration ----
                // Every operation below mirrors the scalar body underneath term for term and in
                // the same order; the benchmark's equivalence section runs both against vanilla
                // over 1680 camera/mode/layout combinations and compares the emitted byte ranges.
                if (vector)
                {
                    for (; k + 4 <= be; k += 4)
                    {
                        var kk = (nuint)k;
                        int m;

                        if (normalMode)
                        {
                            var vx = Widen(ref geoRef, kk);
                            var vz = Widen(ref geoRef, kk + (nuint)oZ);
                            var dx = Avx.Subtract(vx, pxV);
                            var dz = Avx.Subtract(vz, pzV);
                            // vanilla narrows the sum of squares back to float before comparing
                            var distSq = Avx.ConvertToVector256Double(
                                Avx.ConvertToVector128Single(
                                    Avx.Add(Avx.Multiply(dx, dx), Avx.Multiply(dz, dz))));

                            // Per-part bounds, not the bucket's: the LOD 3 bucket also holds the
                            // out-of-range levels vanilla treats as permanently invisible, and
                            // reading the table per lane needs no argument about what a bucket
                            // can contain. Two loads a lane out of a table that stays in L1.
                            var loV = Vector256.Create(
                                loBound[lod[k]], loBound[lod[k + 1]], loBound[lod[k + 2]], loBound[lod[k + 3]]);
                            var hiV = Vector256.Create(
                                hiBound[lod[k]], hiBound[lod[k + 1]], hiBound[lod[k + 2]], hiBound[lod[k + 3]]);

                            m = Avx.MoveMask(Avx.And(Avx.CompareGreaterThan(distSq, loV),
                                                     Avx.CompareLessThan(distSq, hiV)));
                            if (m != 0 && !poolFullyInside)
                                m &= FrustumMask(planesV, 5, vx, Widen(ref geoRef, kk + (nuint)oY), vz,
                                                 Widen(ref geoRef, kk + (nuint)oEX),
                                                 Widen(ref geoRef, kk + (nuint)oEY),
                                                 Widen(ref geoRef, kk + (nuint)oEZ));
                        }
                        else if (shadowMode)
                        {
                            var fx = Vector128.LoadUnsafe(ref geoRef, kk);
                            var fz = Vector128.LoadUnsafe(ref geoRef, kk + (nuint)oZ);
                            // the difference and its absolute value are computed in float, as
                            // vanilla does, and only then widened for the range comparison
                            var adx = Avx.ConvertToVector256Double(
                                Sse.And(Sse.Subtract(ppxV, fx), absMask));
                            var adz = Avx.ConvertToVector256Double(
                                Sse.And(Sse.Subtract(ppzV, fz), absMask));
                            m = Avx.MoveMask(Avx.And(Avx.CompareLessThan(adx, rangeXV),
                                                     Avx.CompareLessThan(adz, rangeZV)));
                            if (m != 0 && !poolFullyInside)
                                m &= FrustumMask(planesV, 6,
                                                 Avx.ConvertToVector256Double(fx),
                                                 Widen(ref geoRef, kk + (nuint)oY),
                                                 Avx.ConvertToVector256Double(fz),
                                                 Widen(ref geoRef, kk + (nuint)oEX),
                                                 Widen(ref geoRef, kk + (nuint)oEY),
                                                 Widen(ref geoRef, kk + (nuint)oEZ));
                        }
                        else
                        {
                            m = poolFullyInside ? 0xF
                              : FrustumMask(planesV, 6,
                                            Widen(ref geoRef, kk),
                                            Widen(ref geoRef, kk + (nuint)oY),
                                            Widen(ref geoRef, kk + (nuint)oZ),
                                            Widen(ref geoRef, kk + (nuint)oEX),
                                            Widen(ref geoRef, kk + (nuint)oEY),
                                            Widen(ref geoRef, kk + (nuint)oEZ));
                        }

                        while (m != 0)
                        {
                            var i = orig[k + System.Numerics.BitOperations.TrailingZeroCount(m)];
                            m &= m - 1;
                            var loc = locs[i];
                            if (!loc.CullVisible[visBuf] || loc.Hide) continue;
                            if (normalMode) loc.FrustumVisible = true;
                            bits[i >> 6] |= 1UL << (i & 63);
                        }
                    }
                }

                for (; k < be; k++)
                {
                    var x = geo[k];
                    var z = geo[oZ + k];
                    bool visible;

                    if (normalMode)
                    {
                        // BlockPos.HorDistanceSqTo returns float; vanilla widens that to double
                        var dx = (double)x - px;
                        var dz = (double)z - pz;
                        double distSq = (float)(dx * dx + dz * dz);

                        var lv = lod[k];
                        visible = distSq > loBound[lv] & distSq < hiBound[lv];
                        if (visible & !poolFullyInside)
                            visible = InFrustum5(planes, x, geo[oY + k], z, geo[oEX + k], geo[oEY + k], geo[oEZ + k]);
                    }
                    else if (shadowMode)
                    {
                        visible = Math.Abs(ppx - x) < rangeX
                               && Math.Abs(ppz - z) < rangeZ
                               && (poolFullyInside
                                   || InFrustum6(planes, x, geo[oY + k], z, geo[oEX + k], geo[oEY + k], geo[oEZ + k]));
                    }
                    else
                    {
                        visible = poolFullyInside
                               || InFrustum6(planes, x, geo[oY + k], z, geo[oEX + k], geo[oEY + k], geo[oEZ + k]);
                    }

                    if (!visible) continue;

                    var i = orig[k];
                    var loc = locs[i];
                    if (!loc.CullVisible[visBuf] || loc.Hide) continue;
                    // Deliberate deviation, stated rather than hidden: vanilla's
                    // UpdateVisibleFlag writes FrustumVisible either way, this only writes the
                    // true case. Writing false as well would mean running the Hide/CullVisible
                    // gate - a pointer chase - for all ~43k parts tested per frame instead of
                    // the ~15k that pass, which is most of what this sweep exists to avoid. The
                    // field's only reader in the whole engine and in the shipped game mods is
                    // ClientChunk.IsFrustumVisible(), which nothing calls.
                    if (normalMode) loc.FrustumVisible = true;

                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }
        }

        // Parts appended since the last rebuild are not in the grid. They get the same tests,
        // just without cell or bucket pre-rejection - there are at most a few dozen of them,
        // which is the whole point of not rebuilding the grid for every new chunk.
        for (var k = 0; k < c.OverCount; k++)
        {
            var g = k * 6;
            var x = c.OverGeo[g];
            var z = c.OverGeo[g + 2];

            // The LOD level is read off the location, not out of a cached byte. An overflow
            // entry can be a part that TryAdd squeezed into the middle of the list, and at the
            // moment our postfix sees that part the engine has not assigned its LodLevel yet -
            // a cached copy is a 0, which the camera pass reads as "invisible unless the LOD0
            // setting is on". A few dozen entries at most, so the dereference is nothing.
            var i = c.OverOrig[k];
            var loc = locs[i];
            var lodLevel = loc.LodLevel;
            var lv = (byte)((uint)lodLevel < 4u ? lodLevel : 255);
            bool visible;

            if (normalMode)
            {
                var dx = (double)x - px;
                var dz = (double)z - pz;
                double distSq = (float)(dx * dx + dz * dz);

                visible = distSq > loBound[lv] & distSq < hiBound[lv];
                if (visible & !poolFullyInside)
                    visible = InFrustum5(planes, x, c.OverGeo[g + 1], z, c.OverGeo[g + 3], c.OverGeo[g + 4], c.OverGeo[g + 5]);
            }
            else if (shadowMode)
            {
                if (farPass && lodLevel < 1) continue;
                if (skipLod3 && lv == 3) continue;
                visible = Math.Abs(ppx - x) < rangeX
                       && Math.Abs(ppz - z) < rangeZ
                       && (poolFullyInside
                           || InFrustum6(planes, x, c.OverGeo[g + 1], z, c.OverGeo[g + 3], c.OverGeo[g + 4], c.OverGeo[g + 5]));
            }
            else
            {
                visible = poolFullyInside
                       || InFrustum6(planes, x, c.OverGeo[g + 1], z, c.OverGeo[g + 3], c.OverGeo[g + 4], c.OverGeo[g + 5]);
            }

            tested++;
            if (!visible) continue;

            if (!loc.CullVisible[visBuf] || loc.Hide) continue;
            if (normalMode) loc.FrustumVisible = true;

            bits[i >> 6] |= 1UL << (i & 63);
        }

        st.Parts += tested;
        st.Cells += cellsSkipped;
        st.Buckets += bucketsSkipped;

        // Emit in index order - glMultiDrawElements draws its ranges in the order given, and
        // merging back-to-back ranges only works on a sorted list. Scanning the bitmap is
        // 8 bytes per 64 parts against 24 bytes of geometry each, and whole empty words are
        // skipped outright.
        //
        // A pool fully inside the frustum cannot contain a frustum-rejected part, so there is
        // nothing a bridge could legally cross - the flag saves the walk, not correctness.
        var bridging = merge && GapMergeDrawRanges && !poolFullyInside;
        var prevI = -1;
        for (var w = 0; w < words; w++)
        {
            var v = bits[w];
            while (v != 0)
            {
                var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(v);
                v &= v - 1;
                rawRanges++;
                if (bridging && group > 0)
                    TryBridge(meta, locs, planes, normalMode, prevI, i, ref prevEndByte, sizes, group, ref st);
                Emit(starts, sizes, meta, i, ref group, ref rendered, ref prevEndByte, merge);
                prevI = i;
            }
        }

        st.RangesRaw += rawRanges;
        st.RangesEmitted += group;

        pool.indicesGroupsCount = group;
        pool.RenderedTriangles = rendered;
        pool.AllocatedTris = c.AllocatedTris;
    }
}
