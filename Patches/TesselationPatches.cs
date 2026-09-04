using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Komet.Runtime;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Makes the terrain tesselation thread spend its time tesselating.
///
/// The client meshes chunks on exactly one thread (ClientMain starts it as "tesselateterrain"
/// and TextureAtlasManager checks its id, so adding more is not an option for a Harmony mod).
/// With a long queue - entering new terrain at view distance 1536 puts 1500+ chunks in it -
/// three things keep that thread from meshing at full rate:
///
/// 1. ClientThread.Process sleeps 5 ms after every tick unless a system reports a negative
///    tick interval, and ChunkTesselatorManager reports 0. The queue waits out that nap.
/// 2. The thread runs at normal priority against the render thread, our cull workers and the
///    other seven worker threads.
/// 3. Each tesselation starts by unpacking up to 27 neighbouring chunks - decompressing them
///    on the critical path if they were packed, even though any other core could have done it
///    ahead of time.
/// </summary>
public static class TesselationPatches
{
    public static bool NoIdleSleep = true;
    public static bool RaiseThreadPriority = true;
    public static bool NeighbourPrefetch = true;

    /// <summary>Chunks the prefetcher found packed and unpacked ahead of the tesselator.</summary>
    public static long StatPrefetchedUnpacks;

    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> ChunksRef =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
    private static readonly AccessTools.FieldRef<ClientWorldMap, object> ChunksLockRef =
        AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");

    private const long ExtraDimensionsStart = 4503599627370496L;

    public static void EnsureReady()
    {
        if (ClientQueues.GameOf == null || ClientQueues.Dirty == null || ClientQueues.DirtyPrio == null
            || ClientQueues.DirtyLock == null || ChunksRef == null || ChunksLockRef == null)
            throw new InvalidOperationException("ClientMain/ClientWorldMap internals not found");
    }

    public static void Apply(Harmony harmony, bool noIdleSleep, bool raisePriority, bool prefetch)
    {
        ShuttingDown = false; // static state must not survive a rejoin into the next world
        NoIdleSleep = noIdleSleep;
        RaiseThreadPriority = raisePriority;
        NeighbourPrefetch = prefetch;

        // The per-chunk teardown guard, applied regardless of the speed toggles: ONE
        // tesselation tick drains the whole dirty queue, so a tick that is already running
        // when the world starts dying outlives the engine's 200 ms thread-exit window by
        // seconds when thousands of chunks are queued - a guard at the tick boundary alone
        // can never catch it (it did not: the exit NRE came back with an 11k queue).
        var tessChunk = AccessTools.Method(typeof(ChunkTesselatorManager), "TesselateChunk",
                        [typeof(int), typeof(int), typeof(int), typeof(bool),
                            typeof(bool), typeof(bool).MakeByRefType()])
                        ?? throw new InvalidOperationException("TesselateChunk not found");
        harmony.Patch(tessChunk, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(TesselationPatches), nameof(TesselateChunkPrefix)))
            { priority = HarmonyLib.Priority.High });

        if (!noIdleSleep && !raisePriority && !prefetch) return;

        EnsureReady();

        if (noIdleSleep)
        {
            var interval = AccessTools.Method(typeof(ChunkTesselatorManager),
                               nameof(ChunkTesselatorManager.SeperateThreadTickIntervalMs))
                           ?? throw new InvalidOperationException("SeperateThreadTickIntervalMs not found");
            harmony.Patch(interval, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(TesselationPatches), nameof(TickIntervalPostfix))));
        }

        if (raisePriority || prefetch)
        {
            var tick = AccessTools.Method(typeof(ChunkTesselatorManager), "OnSeperateThreadGameTick")
                       ?? throw new InvalidOperationException("OnSeperateThreadGameTick not found");
            harmony.Patch(tick, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TesselationPatches), nameof(TesselationTickPrefix))));
        }
    }

    /// <summary>
    /// ClientThread.Process sleeps 5 ms after a tick unless some system returned a negative
    /// interval. Vanilla's 0 means "run every tick, but nap in between" - fine when nothing
    /// is queued, a brake when 1500 chunks are. Negative only while there is work, so an idle
    /// thread still naps instead of spinning a core.
    /// </summary>
    /// <summary>
    /// Set the moment the world starts leaving, cleared on the next world's Apply. While set,
    /// the tesselation thread gets its vanilla naps back, skips whole ticks, and - the part
    /// that actually closes the hole - skips every remaining chunk of a tick that was already
    /// running (see <see cref="TesselateChunkPrefix"/>).
    ///
    /// Timing matters more than the flag itself: DestroyGameSession fires the LeaveWorld
    /// event FIRST, then tells the client threads to exit and waits 200 ms, and only long
    /// after that disposes the mod systems. Setting the flag from Dispose - the first attempt
    /// - was therefore always too late, and the NRE in BuildExtendedChunkData (chunk data
    /// freed under the tesselator) came back. KometModSystem now sets it from the LeaveWorld
    /// event, before the teardown window even opens.
    /// </summary>
    public static volatile bool ShuttingDown;

    /// <summary>
    /// Skips a single chunk's tesselation during teardown. Priority.High so it runs before
    /// the measurement prefix; the measurement postfix ignores skipped chunks by their zero
    /// result, so the per-chunk stats stay clean. requeue is the engine's out parameter and
    /// must be assigned on the skip path - the caller's variable is uninitialised.
    /// </summary>
    public static bool TesselateChunkPrefix(ref int __result, ref bool requeue)
    {
        if (!ShuttingDown) return true;
        requeue = false;
        __result = 0;
        return false;
    }

    public static void TickIntervalPostfix(ChunkTesselatorManager __instance, ref int __result)
    {
        if (ShuttingDown) return;
        if (__result != 0) return;
        var game = ClientQueues.GameOf(__instance);
        if (game == null) return;
        // racy reads of Count, and that is fine: a stale answer means one 5 ms nap too many
        // or a single extra empty tick, both harmless
        if (ClientQueues.DirtyPrio(game)?.Count > 0 || ClientQueues.Dirty(game)?.Count > 0) __result = -1;
    }

    /// <summary>
    /// Runs on the tesselation thread itself, which is the cleanest way to reach it - no
    /// scanning ClientMain's thread list. First call raises the priority and starts the
    /// prefetcher; after that it is two boolean tests.
    /// </summary>
    public static bool TesselationTickPrefix(ChunkTesselatorManager __instance)
    {
        // The world is going away: no new chunk may enter tesselation. The engine gives the
        // thread 200 ms to exit; a tick skipped here is a tick that cannot NRE on data the
        // teardown already freed.
        if (ShuttingDown) return false;

        if (RaiseThreadPriority && Thread.CurrentThread.Priority == ThreadPriority.Normal)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
            catch { RaiseThreadPriority = false; } // the OS said no; asking again won't help
        }

        if (NeighbourPrefetch) Prefetcher.EnsureRunning(ClientQueues.GameOf(__instance));
        return true;
    }

    /// <summary>
    /// Unpacks the neighbourhood of the chunks at the front of the tesselation queue before
    /// the tesselation thread gets to them.
    ///
    /// BuildExtendedChunkData calls Unpack() on all 27 chunks around the one being meshed.
    /// Unpack decompresses under the chunk's own packUnpackLock and is idempotent - the
    /// compresschunks thread already exercises exactly this concurrency - so doing it a few
    /// queue entries ahead moves the decompression to a spare core and off the critical path.
    /// A chunk the pool repacks in between just gets unpacked again by the tesselator; the
    /// only cost of being wrong is work the tesselator would have done anyway.
    /// </summary>
    private static class Prefetcher
    {
        /// <summary>
        /// Queue entries to look ahead. At ~4 ms per tesselation, 32 entries are ~130 ms of
        /// runway - enough that the worker stays ahead even when a whole neighbourhood turns
        /// out to be packed, while a full pass is still only ~860 dictionary lookups.
        /// </summary>
        private const int LookAhead = 32;

        private static Thread worker;
        private static volatile ClientMain game;
        private static volatile bool stop;
        private static readonly long[] ahead = new long[LookAhead];
        private static readonly ClientChunk[] hood = new ClientChunk[27];
        private static readonly Vec3i pos = new();

        public static void EnsureRunning(ClientMain current)
        {
            if (current == null) return;
            if (ReferenceEquals(game, current) && worker is { IsAlive: true }) return;

            game = current; // rebind after a world change; the loop re-reads it every pass
            if (worker is { IsAlive: true }) return;

            stop = false;
            worker = new Thread(Loop)
            {
                Name = "komet-unpack-prefetch",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal // never compete with the tesselator itself
            };
            worker.Start();
        }

        public static void Stop()
        {
            stop = true;
            game = null;
        }

        private static void Loop()
        {
            while (!stop)
            {
                try
                {
                    var g = game;
                    if (g == null || g.disposed) { Thread.Sleep(50); continue; }

                    var copied = Snapshot(g);
                    if (copied == 0) { Thread.Sleep(25); continue; }

                    // The decompression this thread does off the critical path allocates the
                    // same arrays the tesselator otherwise would - measured here so the
                    // alloc-attribution row can name this thread's share instead of leaving
                    // it in "rest".
                    var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < copied && !stop; i++) UnpackNeighbourhood(g, ahead[i]);
                    Komet.Measure.FrameStats.AddPrefetchAllocBytes(
                        GC.GetAllocatedBytesForCurrentThread() - alloc0);
                    Thread.Sleep(2);
                }
                catch (Exception)
                {
                    // A stale ClientMain mid-shutdown can throw almost anywhere. The
                    // prefetcher is an accelerator, never a dependency - back off and retry.
                    Thread.Sleep(250);
                }
            }
        }

        /// <summary>Copies the first entries of the dirty-chunk queue under its own lock.</summary>
        private static int Snapshot(ClientMain g)
        {
            var dirty = ClientQueues.Dirty(g);
            var dirtyLock = ClientQueues.DirtyLock(g);
            if (dirty == null || dirtyLock == null) return 0;
            if (dirty.Count < 2) return 0; // a queue this short is cheaper to leave alone

            var n = 0;
            lock (dirtyLock)
            {
                foreach (var key in dirty)
                {
                    ahead[n++] = key;
                    if (n >= LookAhead) break;
                }
            }
            return n;
        }

        private static void UnpackNeighbourhood(ClientMain g, long key)
        {
            key &= 0x7FFFFFFFFFFFFFFFL; // the queue uses the sign bit as an edge-only flag
            if (key >= ExtraDimensionsStart) return;

            var map = g.WorldMap;
            if (map == null) return;
            var chunks = ChunksRef(map);
            var chunksLock = ChunksLockRef(map);
            if (chunks == null || chunksLock == null) return;

            int mulX = map.index3dMulX, mulZ = map.index3dMulZ;
            MapUtil.PosInt3d(key, mulX, mulZ, pos);

            // One lock acquisition for the whole neighbourhood, exactly like the engine's
            // GetNeighbouringChunks - 27 separate lock round trips per queue entry were a
            // measurable contribution to chunksLock contention while chunks stream in.
            var found = 0;
            lock (chunksLock)
            {
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nKey = MapUtil.Index3dL(pos.X + dx, pos.Y + dy, pos.Z + dz, mulX, mulZ);
                    if (chunks.TryGetValue(nKey, out var c) && c != null) hood[found++] = c;
                }
            }

            for (var i = 0; i < found; i++)
            {
                var c = hood[i];
                // IsPacked without the pack lock is a hint, not a truth - Unpack itself takes
                // the lock and re-checks. All the race costs is a pointless call.
                if (c.IsPacked())
                {
                    c.Unpack();
                    Interlocked.Increment(ref StatPrefetchedUnpacks);
                }
                hood[i] = null;
            }
        }
    }

    public static void Shutdown()
    {
        ShuttingDown = true;
        Prefetcher.Stop();
    }
}
