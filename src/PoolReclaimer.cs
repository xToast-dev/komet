using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;

namespace Komet;

/// <summary>
/// Gives back the video memory of chunk mesh pools that have run empty.
///
/// A MeshDataPool allocates its GPU buffers up front at full size - 500 000 vertices plus
/// 750 000 indices, roughly 10 MB - and the HUD's "terrain vram" is simply the pool count
/// times that. Pools are created on demand while terrain streams in, but nothing ever gives
/// one back: MeshDataPoolMasterManager.AddModelDataPool assigns
/// <c>poolId = modelPools.Count</c> and RemoveLocationsNow looks pools up as
/// <c>modelPools[location.PoolId]</c>, so the list index *is* the id and removing an entry
/// would repoint every live location at the wrong pool. Which is why the engine never tries.
///
/// So the slot stays and only the memory goes. An empty pool that has stayed empty for a
/// while gets its buffers deleted and its capacity set to zero, which is a state the engine
/// already handles cleanly:
///   * TryAppend returns null when the mesh does not fit, and zero capacity never fits, so
///     MeshDataPoolManager.AddModel just moves on to the next pool exactly as it does for a
///     full one,
///   * TrySqueezeInbetween is only entered above 3 % fragmentation and CurrentFragmentation
///     is forced to 0 while verticesPosition is 0,
///   * RenderMesh is only called when indicesGroupsCount != 0, and a pool with no locations
///     always culls to zero groups - so the deleted MeshRef is never dereferenced.
///
/// The delay matters: pools empty out and refill constantly while flying, and reclaiming one
/// that is about to be reused would trade video memory for a fresh allocation. Only pools
/// that stay empty across the whole window - i.e. terrain the player has left behind - are
/// worth taking.
/// </summary>
public static class PoolReclaimer
{
    public static bool Enabled = true;

    /// <summary>How long a pool has to stay empty before its buffers are released.</summary>
    public static double AfterSeconds = 20.0;

    public static long StatPoolsReclaimed;
    public static long StatBytesReclaimed;

    /// <summary>Pools currently holding nothing, whether or not they have been reclaimed yet.</summary>
    public static int StatEmptyPools;

    private static readonly AccessTools.FieldRef<MeshDataPoolMasterManager, List<MeshDataPool>> PoolsRef =
        AccessTools.FieldRefAccess<MeshDataPoolMasterManager, List<MeshDataPool>>("modelPools");
    private static readonly AccessTools.FieldRef<MeshDataPool, MeshRef> MeshRefRef =
        AccessTools.FieldRefAccess<MeshDataPool, MeshRef>("modelRef");
    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> LocationsRef =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");
    private static readonly AccessTools.FieldRef<MeshDataPool, int> DimensionRef =
        AccessTools.FieldRefAccess<MeshDataPool, int>("dimensionId");
    private static readonly AccessTools.FieldRef<Vintagestory.Client.NoObf.ChunkRenderer, MeshDataPoolMasterManager> MasterRef =
        AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.ChunkRenderer, MeshDataPoolMasterManager>("masterPool");
    private static readonly AccessTools.FieldRef<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ChunkRenderer> RendererRef =
        AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.ClientMain, Vintagestory.Client.NoObf.ChunkRenderer>("chunkRenderer");

    /// <summary>How long each pool has been empty, keyed weakly so a pool can still be collected.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshDataPool, EmptySince> Seen = new();

    private sealed class EmptySince { public double At = -1; }

    public static void EnsureReady()
    {
        if (PoolsRef == null || MeshRefRef == null || LocationsRef == null || DimensionRef == null
            || MasterRef == null || RendererRef == null)
            throw new InvalidOperationException("MeshDataPool/MasterManager internals not found");
    }

    /// <summary>
    /// Decides what to do with one pool. Split out from the GL work so the rule - and in
    /// particular "never reclaim a pool that still holds geometry" - is testable without a
    /// graphics context.
    /// </summary>
    internal static bool ShouldReclaim(int locationCount, int verticesPoolSize, int dimensionId,
                                       double emptySinceSeconds, double now, double afterSeconds)
    {
        if (locationCount != 0) return false;      // holds geometry - never touch it
        if (verticesPoolSize <= 0) return false;   // already reclaimed
        if (dimensionId != 0) return false;        // mini dimensions render through another path
        if (emptySinceSeconds < 0) return false;   // first time seen empty; start the clock
        return now - emptySinceSeconds >= afterSeconds;
    }

    /// <summary>Called once a second from the render thread, where deleting a mesh is legal.</summary>
    public static void Run(ICoreClientAPI capi, MeshDataPoolMasterManager master, double now)
    {
        if (!Enabled || master == null) return;

        List<MeshDataPool> pools = PoolsRef(master);
        if (pools == null) return;

        int empty = 0;
        for (int i = 0; i < pools.Count; i++)
        {
            MeshDataPool pool = pools[i];
            if (pool == null) continue;

            int count = LocationsRef(pool)?.Count ?? 0;
            if (count != 0)
            {
                if (Seen.TryGetValue(pool, out EmptySince used)) used.At = -1;
                continue;
            }

            if (pool.VerticesPoolSize > 0) empty++;

            EmptySince state = Seen.GetOrCreateValue(pool);
            if (!ShouldReclaim(count, pool.VerticesPoolSize, DimensionRef(pool), state.At, now, AfterSeconds))
            {
                if (state.At < 0) state.At = now;
                continue;
            }

            Reclaim(capi, pool);
            state.At = -1;
        }
        StatEmptyPools = empty;
    }

    private static void Reclaim(ICoreClientAPI capi, MeshDataPool pool)
    {
        long bytes = EstimateBytes(capi, pool);

        ref MeshRef mesh = ref MeshRefRef(pool);
        if (mesh != null)
        {
            capi.Render.DeleteMesh(mesh);
            mesh = null;
        }

        // Zero capacity is what makes TryAppend refuse this pool from now on. Both positions
        // are already zero for an empty pool, but set them anyway so nothing can read a stale
        // offset into buffers that no longer exist.
        pool.VerticesPoolSize = 0;
        pool.IndicesPoolSize = 0;
        pool.indicesPosition = 0;
        pool.verticesPosition = 0;
        pool.indicesGroupsCount = 0;
        pool.RenderedTriangles = 0;
        pool.AllocatedTris = 0;
        pool.UsedVertices = 0;
        pool.CurrentFragmentation = 0f;

        // the draw range scratch arrays are sized by MaxPartsPerPool and are pure heap
        pool.indicesStartsByte = Array.Empty<int>();
        pool.indicesSizes = Array.Empty<int>();

        // our own per-pool cull cache is keyed weakly and rebuilt on demand
        FastCuller.Invalidate(pool);

        StatPoolsReclaimed++;
        StatBytesReclaimed += bytes;
    }

    /// <summary>
    /// The same arithmetic MeshDataPoolManager.GetStats uses, so the reclaimed figure and the
    /// HUD's "terrain vram" are in the same units. The per-vertex stride of the custom parts
    /// is not reachable from here, so this is the floor of what was freed, never an overstatement.
    /// </summary>
    private static long EstimateBytes(ICoreClientAPI capi, MeshDataPool pool)
    {
        long perVertex = capi.Render.UseSSBOs ? 16 : 28;
        long perIndex = capi.Render.UseSSBOs ? 0 : 4;
        return pool.VerticesPoolSize * perVertex + pool.IndicesPoolSize * perIndex;
    }

    /// <summary>
    /// Drives the reclaimer from a render stage, which is the only place where deleting a
    /// mesh is legal. Registering a renderer rather than a game tick listener is deliberate:
    /// tick listeners are main thread but not contractually inside the GL context.
    /// </summary>
    public sealed class Renderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private float accum;
        private int failures;

        public Renderer(ICoreClientAPI capi) => this.capi = capi;

        public double RenderOrder => 0.99; // after everything has drawn for this frame
        public int RenderRange => 0;

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled) return;
            accum += dt;
            if (accum < 1f) return;
            accum = 0;

            try
            {
                if (capi.World is not Vintagestory.Client.NoObf.ClientMain game) return;
                Vintagestory.Client.NoObf.ChunkRenderer renderer = RendererRef(game);
                if (renderer == null) return;
                Run(capi, MasterRef(renderer), game.ElapsedMilliseconds / 1000.0);
            }
            catch (Exception e)
            {
                // Reclaiming memory must never be the reason a frame dies. Log once, then
                // switch off - the game runs perfectly well keeping the pools it has.
                if (++failures == 1) capi.Logger.Error("komet pool reclaimer failed, switching it off:\n{0}", e);
                if (failures >= 3) Enabled = false;
            }
        }

        public void Dispose() { }
    }

    public static void Reset()
    {
        StatPoolsReclaimed = 0;
        StatBytesReclaimed = 0;
    }
}
