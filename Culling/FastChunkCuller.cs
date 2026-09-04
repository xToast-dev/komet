using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet;

/// <summary>
/// Replacement for ChunkCuller.CullInvisibleChunks, the occlusion pass that decides which
/// chunks are worth handing to the renderer at all.
///
/// Vanilla shoots three rays at every position on a shell around the player and walks them
/// chunk by chunk. The shell grows with the view distance - about 3 900 positions at 256 but
/// 24 700 at 1536 - and each step costs six plane intersections plus a dictionary lookup, all
/// on a single thread. Measured on a synthetic map that is 79 ms per pass at view distance
/// 1536 against 16 ms at 512, and it re-runs every time the player crosses a chunk boundary.
///
/// Three changes, none of which alter which chunks end up visible:
///   * the per-ray constants come out of the per-step loop (see <see cref="RayTraversal"/>),
///   * the chunk dictionary is snapshotted into a flat array once, under the same lock
///     vanilla already takes to clear the visibility flags, so lookups become array indexing
///     and the walk no longer reads a dictionary another thread may be resizing,
///   * the rays, which are independent, run across all cores.
///
/// Visibility is a union over rays, so the result does not depend on their order. Marking a
/// chunk visible sets one bit that is only ever set (never cleared) during the walk, so the
/// concurrent writes are idempotent.
/// </summary>
public static class FastChunkCuller
{
    public static bool Enabled = true;

    /// <summary>0 = auto (leave two cores for the game).</summary>
    public static int MaxThreads;

    /// <summary>
    /// Its own threads, separate from the cull batch's. Sharing one set would mean an occlusion
    /// walk in flight could hold up a render stage behind it - which is the very stall the
    /// dedicated threads exist to remove, just moved one level in.
    /// </summary>
    public static WorkerSet Workers { get; private set; } = new("komet-occl");

    /// <summary>
    /// Unix nice increment for the walk's threads. Must be set before <see cref="StartWorkers"/>,
    /// since a thread's priority is set once when it starts.
    /// </summary>
    public static int Niceness;

    public static void StartWorkers()
    {
        if (Niceness > 0) Workers = new WorkerSet("komet-occl", Niceness);
        Workers.Start(MaxThreads > 0 ? MaxThreads : WorkerSet.AutoThreads(2));
    }

    /// <summary>Reused across passes; the sink is pure scratch, so each slice gets its own copy
    /// of the template and nothing has to be merged afterwards.</summary>
    private sealed class TraceBody : IWorkBody
    {
        public GridSink Template;
        public Vec3i[] Shell;
        public int FromX, FromY, FromZ;
        public bool AboveHeightLimit;

        public void Run(int from, int to)
        {
            var sink = Template;
            for (var i = from; i < to; i++) TraceThree(ref sink, Shell[i], FromX, FromY, FromZ, AboveHeightLimit);
        }
    }

    private static readonly TraceBody traceBody = new();

    /// <summary>
    /// Minimum milliseconds between two occlusion passes. Vanilla redoes the pass whenever
    /// ten chunk positions changed - while loading at hundreds of chunks per second that is
    /// continuous re-runs, each of which clears and snapshots the whole chunk dictionary
    /// under chunksLock, the same lock the network thread needs to insert arriving chunks
    /// and the tesselation path needs to look chunks up. The gate costs newly loaded distant
    /// chunks at most this much extra latency before they become visible; a camera move
    /// across a chunk border still forces a pass on the vanilla condition.
    /// </summary>
    public static int MinIntervalMs = 200;

    private static long lastPassTicks;
    private static long burstUntilTicks;

    /// <summary>Occlusion passes skipped by the rate limit; teleport bursts show as pauses here.</summary>
    public static long StatRateLimited;

    public static long StatPasses;
    public static double StatLastMs;
    public static double StatPeakMs;
    public static long StatChunksSnapshotted;
    public static long StatGridFallbacks;

    private const long ExtraDimensionsStart = 4503599627370496L;
    private const int MaxGridCells = 12_000_000; // ~96 MB of references; beyond that, bail out

    private static readonly AccessTools.FieldRef<ChunkCuller, ClientMain> GameRef =
        AccessTools.FieldRefAccess<ChunkCuller, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ChunkCuller, Vec3i[]> ShellRef =
        AccessTools.FieldRefAccess<ChunkCuller, Vec3i[]>("cubicShellPositions");
    private static readonly AccessTools.FieldRef<ChunkCuller, Vec3i> CenterRef =
        AccessTools.FieldRefAccess<ChunkCuller, Vec3i>("centerpos");
    private static readonly AccessTools.FieldRef<ChunkCuller, bool> AboveLimitRef =
        AccessTools.FieldRefAccess<ChunkCuller, bool>("isAboveHeightLimit");
    private static readonly AccessTools.FieldRef<ChunkCuller, int> QCountRef =
        AccessTools.FieldRefAccess<ChunkCuller, int>("qCount");
    private static readonly AccessTools.FieldRef<ChunkCuller, bool> NowOffRef =
        AccessTools.FieldRefAccess<ChunkCuller, bool>("nowOff");
    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> ChunksRef =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
    private static readonly AccessTools.FieldRef<ClientWorldMap, object> ChunksLockRef =
        AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");

    // reused between passes so a pass allocates nothing
    private static long[] snapKeys = Array.Empty<long>();
    private static ClientChunk[] snapChunks = Array.Empty<ClientChunk>();
    private static ClientChunk[] grid = Array.Empty<ClientChunk>();
    private static readonly Dictionary<long, ClientChunk> fallbackMap = new();

    public static void EnsureReady()
    {
        if (GameRef == null || ShellRef == null || CenterRef == null || AboveLimitRef == null
            || QCountRef == null || NowOffRef == null || ChunksRef == null || ChunksLockRef == null)
            throw new InvalidOperationException("ChunkCuller/ClientWorldMap internals not found");
    }

    private struct GridSink : RayTraversal.IChunkSink
    {
        public ClientChunk[] Grid;
        public int MinX, MinY, MinZ, SizeX, SizeY, SizeZ;
        public int BackBuf;
        public ClientWorldMap Map;

        public bool Visit(int cx, int cy, int cz, int fromFace, int toFace, bool checkBlocking)
        {
            int gx = cx - MinX, gy = cy - MinY, gz = cz - MinZ;
            if ((uint)gx >= (uint)SizeX || (uint)gy >= (uint)SizeY || (uint)gz >= (uint)SizeZ) return true;

            var c = Grid[(gy * SizeZ + gz) * SizeX + gx];
            if (c == null) return true;

            c.CullVisible[BackBuf] = true;
            if (!checkBlocking) return true;
            return c.IsTraversable(BlockFacing.ALLFACES[fromFace], BlockFacing.ALLFACES[toFace]);
        }

        public bool IsValidChunkPos(int cx, int cy, int cz) => Map.IsValidChunkPosFast(cx, cy, cz);
    }

    /// <summary>
    /// Fallback for a chunk set too spread out for a dense grid. Uses a private copy of the
    /// snapshot rather than the live dictionary, so it is still safe to read while the main
    /// thread mutates the real one.
    /// </summary>
    private struct SnapshotDictSink : RayTraversal.IChunkSink
    {
        public Dictionary<long, ClientChunk> Chunks;
        public int MulX, MulZ;
        public int BackBuf;
        public ClientWorldMap Map;

        public bool Visit(int cx, int cy, int cz, int fromFace, int toFace, bool checkBlocking)
        {
            var key = ((long)cy * MulZ + cz) * MulX + cx;
            if (!Chunks.TryGetValue(key, out var c) || c == null) return true;

            c.CullVisible[BackBuf] = true;
            if (!checkBlocking) return true;
            return c.IsTraversable(BlockFacing.ALLFACES[fromFace], BlockFacing.ALLFACES[toFace]);
        }

        public bool IsValidChunkPos(int cx, int cy, int cz) => Map.IsValidChunkPosFast(cx, cy, cz);
    }

    /// <summary>Returns true if vanilla should run instead.</summary>
    public static bool Cull(ChunkCuller self)
    {
        if (!Enabled) return true;

        var game = GameRef(self);
        var map = game?.WorldMap;
        if (game == null || map == null) return true;

        var chunks = ChunksRef(map);
        var chunksLock = ChunksLockRef(map);
        if (chunks == null || chunksLock == null) return true;

        // The "occlusion culling is off" path marks everything visible and then does nothing
        // on later calls. It is cheap and rarely taken; leave it to vanilla.
        if (!ClientSettings.Occlusionculling || chunks.Count < 100) return true;

        var shell = ShellRef(self);
        if (shell == null || shell.Length == 0) return true;

        // everything below mutates culler state, so bail out before that point if the world
        // is not fully up yet
        var centerpos = CenterRef(self);
        if (game.player?.Entity == null || centerpos == null || game.chunkPositionsForRegenTrav == null) return true;

        NowOffRef(self) = false;

        var cameraPos = game.player.Entity.CameraPos;
        var samePosition = centerpos.Equals((int)cameraPos.X / 32, (int)cameraPos.Y / 32, (int)cameraPos.Z / 32);
        if (samePosition && Math.Abs(game.chunkPositionsForRegenTrav.Count - QCountRef(self)) < 10)
        {
            return false; // nothing moved enough to be worth redoing
        }

        var startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // A teleport-sized jump opens a burst window in which the rate limit below stands
        // down. Right after arriving somewhere unvisited, the world assembles from nothing:
        // rays get blocked by half-loaded terrain one pass and pass through it the next, so
        // chunks flip between visible and invisible until the area settles. Vanilla re-runs
        // the pass every ten arriving chunks and corrects each flip within ~120 ms; the rate
        // limit stretched the wrong states to 200-1000 ms - a visible blink for the first
        // seconds after every teleport. The limit exists to protect chunksLock during
        // SUSTAINED streaming; a three-second arrival burst is not what it was for.
        if (!samePosition && IsTeleportJump(centerpos, (int)(cameraPos.X / 32.0), (int)(cameraPos.Y / 32.0), (int)(cameraPos.Z / 32.0)))
            burstUntilTicks = startTicks + 3L * System.Diagnostics.Stopwatch.Frequency;

        // Re-runs triggered purely by chunks streaming in are rate limited; a pass whose
        // trigger is the camera crossing a chunk border still runs immediately, because that
        // one changes what is visible *now* rather than adding terrain at the horizon.
        //
        // The limit scales with what the pass actually costs, so it cannot eat more than
        // ~20 % of its worker core: at 40k loaded chunks a pass takes ~50 ms and its snapshot
        // holds chunksLock - the same lock the network thread needs to insert the hundreds of
        // chunks per second that caused the re-runs in the first place.
        if (RateLimitApplies(samePosition, MinIntervalMs, startTicks, burstUntilTicks))
        {
            var interval = Math.Max(MinIntervalMs, StatLastMs * 5.0);
            if ((startTicks - lastPassTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < interval)
            {
                StatRateLimited++;
                return false;
            }
        }
        lastPassTicks = startTicks;

        QCountRef(self) = game.chunkPositionsForRegenTrav.Count;
        centerpos.Set((int)(cameraPos.X / 32.0), (int)(cameraPos.Y / 32.0), (int)(cameraPos.Z / 32.0));
        var aboveHeightLimit = centerpos.Y >= map.ChunkMapSizeY;
        AboveLimitRef(self) = aboveHeightLimit;

        var backBuf = (ClientChunk.bufIndex + 1) % 2;
        var mulX = map.index3dMulX;
        var mulZ = map.index3dMulZ;

        // ---- one pass under the lock: clear visibility and snapshot the map ----
        int count;
        lock (chunksLock)
        {
            var n = chunks.Count;
            if (snapKeys.Length < n)
            {
                var cap = n + (n >> 2) + 64;
                snapKeys = new long[cap];
                snapChunks = new ClientChunk[cap];
            }

            count = 0;
            foreach (var kv in chunks)
            {
                var c = kv.Value;
                c.CullVisible[backBuf] = false;
                snapKeys[count] = kv.Key;
                snapChunks[count] = c;
                count++;
            }

            // the player's immediate neighbourhood is always visible
            for (var i = -1; i <= 1; i++)
            for (var j = -1; j <= 2; j++)
            for (var k = -1; k <= 1; k++)
            {
                var key = map.ChunkIndex3D(i + centerpos.X, j + centerpos.Y, k + centerpos.Z);
                if (chunks.TryGetValue(key, out var c)) c.CullVisible[backBuf] = true;
            }
        }

        StatChunksSnapshotted = count;

        // ---- build the flat lookup grid, outside the lock ----
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        for (var i = 0; i < count; i++)
        {
            var key = snapKeys[i];
            if (key >= ExtraDimensionsStart) continue; // mini dimensions render unconditionally
            var cx = (int)(key % mulX);
            var rest = key / mulX;
            var cz = (int)(rest % mulZ);
            var cy = (int)(rest / mulZ);
            if (cy >= 1024) continue; // dimension-shifted coordinate, not a world chunk

            if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
            if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
            if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
        }

        if (minX > maxX)
        {
            // only mini-dimension chunks are loaded; those render unconditionally
            Array.Clear(snapChunks, 0, count);
            Swap();
            return false;
        }

        var sizeX = (long)maxX - minX + 1;
        var sizeY = (long)maxY - minY + 1;
        var sizeZ = (long)maxZ - minZ + 1;
        var cells = sizeX * sizeY * sizeZ;
        if (cells > MaxGridCells)
        {
            // Pathological chunk spread - a dense grid would be gigabytes. Handing back to
            // vanilla here is NOT an option: the visibility flags have already been cleared
            // and centerpos/qCount already advanced, so vanilla's own early-out would fire
            // and leave the whole world marked invisible. Trace from the snapshot instead.
            StatGridFallbacks++;
            TraceFromSnapshot(count, mulX, mulZ, backBuf, map, shell, centerpos, aboveHeightLimit);
            Array.Clear(snapChunks, 0, count);
            Swap();
            return false;
        }

        if (grid.Length < cells) grid = new ClientChunk[cells];
        else Array.Clear(grid, 0, (int)cells);

        int sx = (int)sizeX, sy = (int)sizeY, sz = (int)sizeZ;
        for (var i = 0; i < count; i++)
        {
            var key = snapKeys[i];
            if (key >= ExtraDimensionsStart) continue;
            var cx = (int)(key % mulX);
            var rest = key / mulX;
            var cz = (int)(rest % mulZ);
            var cy = (int)(rest / mulZ);
            if (cy >= 1024) continue;
            grid[((long)(cy - minY) * sz + (cz - minZ)) * sx + (cx - minX)] = snapChunks[i];
        }

        // release the snapshot's chunk references so a later pass with fewer chunks does not
        // keep unloaded ones alive
        Array.Clear(snapChunks, 0, count);

        // ---- trace, in parallel ----
        var template = new GridSink
        {
            Grid = grid,
            MinX = minX, MinY = minY, MinZ = minZ,
            SizeX = sx, SizeY = sy, SizeZ = sz,
            BackBuf = backBuf,
            Map = map
        };

        int fromX = centerpos.X, fromY = centerpos.Y, fromZ = centerpos.Z;

        if (Workers.ThreadCount < 1 || shell.Length < 256)
        {
            var sink = template;
            for (var i = 0; i < shell.Length; i++) TraceThree(ref sink, shell[i], fromX, fromY, fromZ, aboveHeightLimit);
        }
        else
        {
            traceBody.Template = template;
            traceBody.Shell = shell;
            traceBody.FromX = fromX;
            traceBody.FromY = fromY;
            traceBody.FromZ = fromZ;
            traceBody.AboveHeightLimit = aboveHeightLimit;
            try
            {
                // A ray is short and their cost varies with how far it gets before hitting
                // something opaque, so slices are small enough to even out and still amortise
                // the hand-out over 64 traces.
                Workers.Run(traceBody, shell.Length, 64);
            }
            finally
            {
                traceBody.Shell = null;
                traceBody.Template = default;
            }
        }

        Swap();

        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        StatLastMs = ms;
        if (ms > StatPeakMs) StatPeakMs = ms;
        StatPasses++;
        return false;
    }

    private static void TraceFromSnapshot(int count, int mulX, int mulZ, int backBuf, ClientWorldMap map,
                                          Vec3i[] shell, Vec3i centerpos, bool aboveHeightLimit)
    {
        fallbackMap.Clear();
        for (var i = 0; i < count; i++) fallbackMap[snapKeys[i]] = snapChunks[i];

        var sink = new SnapshotDictSink { Chunks = fallbackMap, MulX = mulX, MulZ = mulZ, BackBuf = backBuf, Map = map };
        for (var i = 0; i < shell.Length; i++)
        {
            var rel = shell[i];
            RayTraversal.Trace(ref sink, centerpos.X, centerpos.Y, centerpos.Z, rel.X, rel.Y, rel.Z, 0.5, 0.25, aboveHeightLimit);
            RayTraversal.Trace(ref sink, centerpos.X, centerpos.Y, centerpos.Z, rel.X, rel.Y, rel.Z, 0.5, 0.75, aboveHeightLimit);
            RayTraversal.Trace(ref sink, centerpos.X, centerpos.Y, centerpos.Z, rel.X, rel.Y, rel.Z, 0.0, 0.75, aboveHeightLimit);
        }
    }

    private static void TraceThree(ref GridSink sink, Vec3i rel, int fromX, int fromY, int fromZ, bool aboveHeightLimit)
    {
        RayTraversal.Trace(ref sink, fromX, fromY, fromZ, rel.X, rel.Y, rel.Z, 0.5, 0.25, aboveHeightLimit);
        RayTraversal.Trace(ref sink, fromX, fromY, fromZ, rel.X, rel.Y, rel.Z, 0.5, 0.75, aboveHeightLimit);
        RayTraversal.Trace(ref sink, fromX, fromY, fromZ, rel.X, rel.Y, rel.Z, 0.0, 0.75, aboveHeightLimit);
    }

    /// <summary>A camera jump too big for flying: 8+ chunks (256+ blocks) in one step.</summary>
    internal static bool IsTeleportJump(Vec3i oldCenter, int newX, int newY, int newZ)
    {
        if (oldCenter == null) return true;
        var dx = Math.Abs(newX - oldCenter.X);
        var dy = Math.Abs(newY - oldCenter.Y);
        var dz = Math.Abs(newZ - oldCenter.Z);
        return Math.Max(dx, Math.Max(dy, dz)) >= 8;
    }

    /// <summary>The rate limit stands down while a teleport burst window is open.</summary>
    internal static bool RateLimitApplies(bool samePosition, int minIntervalMs, long now, long burstUntil)
        => samePosition && minIntervalMs > 0 && now >= burstUntil;

    /// <summary>ChunkRenderer.SwapVisibleBuffers is internal; both fields it touches are public.</summary>
    private static void Swap()
    {
        ClientChunk.bufIndex = (ClientChunk.bufIndex + 1) % 2;
        ModelDataPoolLocation.VisibleBufIndex = ClientChunk.bufIndex;
    }
}
