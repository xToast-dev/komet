using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Culling;

/// <summary>
/// Puts each chunk's mesh parts into a pool that belongs to the chunk's REGION, so a pool is a
/// place in the world and not an accident of loading order.
///
/// MeshDataPoolManager.AddModel takes the first pool with room. Chunks arrive in the order
/// the server sends and the tesselator finishes them, so every pool ends up holding parts
/// from all over the loaded world, and every pool's draw covers the whole view. That has one
/// consequence this mod could not work around: the ORDER in which the world is drawn. The
/// camera pass draws pool after pool, and with every pool everywhere, a far part of pool 1
/// is drawn before a near part of pool 2 no matter how the parts inside a pool are sorted -
/// so early depth rejection, which would let the GPU skip shading every fragment behind the
/// first leaf canopy, has nothing to work with. A forest scene shades ten fragments per
/// pixel and keeps one.
///
/// With pools routed by region - a 128 x 128 block column of the world per pool set, every
/// height - a pool IS a place, its cached bounding box (FastCuller keeps one) is small, and
/// sorting the pools by distance once per frame is a real front-to-back order. The sweep then
/// emits each pool's cells nearest first (<see cref="FastCuller.FrontToBack"/>), and the
/// depth test does the rest. Nothing about what is drawn changes; only the order.
///
/// The routing is the engine's own AddModel with one difference: the candidate list is the
/// region's pools rather than all of them, and a new pool is created for the region when its
/// pools are full - the same sizing, the same registration with the master pool, the same
/// origin rule. Pools the reclaimer has emptied refuse TryAdd on their own (zero capacity)
/// and are dropped from the region on the next miss. Mini-dimension models (dimension 1) go
/// through vanilla untouched; their pools are looked up by origin in a way this must not
/// disturb.
///
/// Price: a region's pools fill less evenly than a global first-fit, so there are more of
/// them, part-empty. The reclaimer already returns empty ones; the report prints how many
/// pools exist and how full they are, so the price is a number too.
/// </summary>
public static class SpatialPools
{
    /// <summary>Route new models by region. Off is vanilla's first-fit for every model added
    /// from then on; pools already routed stay where they are.</summary>
    public static bool Enabled;

    /// <summary>What komet.json asked for, so safemode has something to come back to.</summary>
    public static bool ConfiguredEnabled;

    /// <summary>Region edge in blocks; a power of two.</summary>
    public static int RegionBlocks = 128;

    public static long StatRouted;      // models placed in their region's pools
    public static long StatNewPools;    // pools created for a region or a lane
    public static long StatFallbacks;   // models handed back to vanilla's path
    public static long StatLaneRouted;  // models placed in a lane's own pools

    /// <summary>
    /// Set around one AddModel call to put the model into a LANE of its own instead of the
    /// shared pools: a set of pools nothing else is added to. Zero is the ordinary path.
    ///
    /// The far LOD needs this and it is what the routing is really for. Its pictures go into
    /// the same managers as the engine's own meshes, so first-fit interleaves them: within
    /// the far distance every picture is an invisible part sitting between two visible engine
    /// parts, which splits the index runs the sweep would otherwise merge (a ground-view
    /// report went from 3,2 to 1,6 emitted ranges per raw range, and every pool held
    /// something visible so every pool was drawn). One lane per representation - the engine's
    /// meshes, tier 1, tier 2 - and each pool holds parts that are visible in the same
    /// distance band, so their ranges merge again and a pool with nothing in the band costs
    /// no draw call at all.
    ///
    /// Main thread only, and only around the one call: AddModel for chunk meshes runs there.
    /// </summary>
    public static int Lane;

    private static readonly AccessTools.FieldRef<MeshDataPoolManager, List<MeshDataPool>> PoolsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, List<MeshDataPool>>("pools");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, ICoreClientAPI> CapiRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, ICoreClientAPI>("capi");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, MeshDataPoolMasterManager> MasterRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, MeshDataPoolMasterManager>("masterPool");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, CustomMeshDataPartFloat> FloatsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, CustomMeshDataPartFloat>("customFloats");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, CustomMeshDataPartShort> ShortsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, CustomMeshDataPartShort>("customShorts");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, CustomMeshDataPartByte> BytesRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, CustomMeshDataPartByte>("customBytes");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, CustomMeshDataPartInt> IntsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, CustomMeshDataPartInt>("customInts");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, int> DefaultVerticesRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, int>("defaultVertexPoolSize");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, int> DefaultIndicesRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, int>("defaultIndexPoolSize");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, int> MaxPartsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, int>("maxPartsPerPool");
    private static readonly AccessTools.FieldRef<MeshDataPool, Vec3i> PoolOriginRef =
        AccessTools.FieldRefAccess<MeshDataPool, Vec3i>("poolOrigin");
    private static readonly AccessTools.FieldRef<MeshDataPool, int> DimensionRef =
        AccessTools.FieldRefAccess<MeshDataPool, int>("dimensionId");

    private sealed class State
    {
        public readonly Dictionary<long, List<MeshDataPool>> Regions = new();
        /// <summary>Pools reserved for a lane; nothing else is ever added to them.</summary>
        public readonly Dictionary<int, List<MeshDataPool>> Lanes = new();
    }

    private static ConditionalWeakTable<MeshDataPoolManager, State> states = new();

    public static void EnsureReady()
    {
        if (PoolsRef == null || CapiRef == null || MasterRef == null || DefaultVerticesRef == null
            || DefaultIndicesRef == null || MaxPartsRef == null || PoolOriginRef == null || DimensionRef == null)
            throw new InvalidOperationException("MeshDataPoolManager internals not found");
    }

    public static void Apply(Harmony harmony, bool enabled, int regionBlocks)
    {
        EnsureReady();
        var add = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
                      [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)])
                  ?? throw new InvalidOperationException("MeshDataPoolManager.AddModel not found");
        harmony.Patch(add, prefix: new HarmonyMethod(AccessTools.Method(typeof(SpatialPools), nameof(AddModel))));

        RegionBlocks = ClampRegion(regionBlocks);
        ConfiguredEnabled = enabled;
        Enabled = enabled;
    }

    /// <summary>A power of two between 32 and 1024 blocks; anything else is rounded to one.</summary>
    internal static int ClampRegion(int blocks)
    {
        if (blocks < 32) blocks = 32;
        if (blocks > 1024) blocks = 1024;
        var p = 32;
        while (p * 2 <= blocks) p *= 2;
        return p;
    }

    /// <summary>
    /// Whether this call is routed at all: a lane always is - that is what a lane is for, and
    /// the far LOD depends on it while the region routing stays off by default - and lane 0
    /// only when the region routing is on.
    /// </summary>
    internal static bool Routes(int lane) => lane != 0 || Enabled;

    /// <summary>The region a block column belongs to, packed into one key. Arithmetic shift,
    /// so negative coordinates floor the way the positive ones do.</summary>
    internal static long RegionKey(int x, int z, int regionBlocks)
    {
        var shift = System.Numerics.BitOperations.Log2((uint)regionBlocks);
        return ((long)(x >> shift) << 32) | (uint)(z >> shift);
    }

    public static bool AddModel(MeshDataPoolManager __instance, MeshData modeldata, Vec3i modelOrigin,
                                int dimension, Sphere frustumCullSphere, ref ModelDataPoolLocation __result)
    {
        var lane = Lane;
        if (!Routes(lane) || dimension != 0 || __instance == null || modeldata == null || modelOrigin == null) return true;

        try
        {
            var capi = CapiRef(__instance);
            var pools = PoolsRef(__instance);
            var master = MasterRef(__instance);
            if (capi == null || pools == null || master == null) return true;

            var state = states.GetOrCreateValue(__instance);
            List<MeshDataPool> region;
            if (lane != 0)
            {
                if (!state.Lanes.TryGetValue(lane, out region))
                {
                    region = new List<MeshDataPool>(2);
                    state.Lanes[lane] = region;
                }
            }
            else
            {
                var key = RegionKey(modelOrigin.X, modelOrigin.Z, RegionBlocks);
                if (!state.Regions.TryGetValue(key, out region))
                {
                    region = new List<MeshDataPool>(2);
                    state.Regions[key] = region;
                }
            }

            // the lane's or region's own pools first - reclaimed ones (capacity zero) fall out here
            for (var i = 0; i < region.Count; i++)
            {
                var pool = region[i];
                if (pool == null || pool.VerticesPoolSize == 0)
                {
                    region.RemoveAt(i--);
                    continue;
                }
                var placed = pool.TryAdd(capi, modeldata, modelOrigin, dimension, frustumCullSphere);
                if (placed != null)
                {
                    __result = placed;
                    if (lane != 0) StatLaneRouted++; else StatRouted++;
                    return false;
                }
            }

            // a new pool for the region: vanilla's sizing, vanilla's registration
            var defaultVertices = DefaultVerticesRef(__instance);
            var defaultIndices = DefaultIndicesRef(__instance);
            var vertices = Math.Max(modeldata.VerticesCount, defaultVertices);
            var indices = Math.Max(modeldata.IndicesCount, defaultIndices);
            if (vertices > defaultVertices)
                capi.World.Logger.Warning(
                    "Chunk (or some other mesh source at origin: {0}) exceeds default geometric complexity maximum of {1} vertices and {2} indices. You must be loading some very complex objects (#v = {3}, #i = {4}). Adjusted Pool size accordingly.",
                    modelOrigin, defaultVertices, defaultIndices, modeldata.VerticesCount, modeldata.IndicesCount);

            var created = MeshDataPool.AllocateNewPool(capi, vertices, indices, MaxPartsRef(__instance),
                FloatsRef?.Invoke(__instance), ShortsRef?.Invoke(__instance), BytesRef?.Invoke(__instance), IntsRef?.Invoke(__instance));
            PoolOriginRef(created) = modelOrigin.Clone();
            DimensionRef(created) = dimension;
            master.AddModelDataPool(created);
            pools.Add(created);
            region.Add(created);
            StatNewPools++;

            var result = created.TryAdd(capi, modeldata, modelOrigin, dimension, frustumCullSphere);
            if (result == null)
            {
                // the same case vanilla logs as Fatal; let it, and let it try the other pools
                StatFallbacks++;
                return true;
            }
            __result = result;
            if (lane != 0) StatLaneRouted++; else StatRouted++;
            return false;
        }
        catch (Exception)
        {
            StatFallbacks++;
            return true;
        }
    }

    /// <summary>How many regions and pools the routing currently knows, for the report.</summary>
    public static void Count(out int regions, out int pools)
    {
        regions = pools = 0;
        foreach (var kv in states)
        {
            regions += kv.Value.Regions.Count;
            foreach (var list in kv.Value.Regions.Values) pools += list.Count;
        }
    }

    /// <summary>How many pools the lanes hold, for the report.</summary>
    public static int LanePools()
    {
        var pools = 0;
        foreach (var kv in states)
            foreach (var list in kv.Value.Lanes.Values) pools += list.Count;
        return pools;
    }

    public static void Reset()
    {
        states = new ConditionalWeakTable<MeshDataPoolManager, State>();
        Lane = 0;
        StatLaneRouted = 0;
    }
}
