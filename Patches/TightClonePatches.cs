using System;
using System.Threading;
using HarmonyLib;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Makes MeshData.CloneExtraData copy content instead of capacity.
///
/// The finding (1.51.7 alloc split, user machine, 1536-block load burst): 217 of 255 MB/s
/// tesselation-thread allocation sat in populateTesselatedChunkPart while the mesh recycler
/// reported 100% hits - so the churn was not the recycled base arrays but the extra data
/// cloned per part. Mechanism, from the decompile: every chunk render pass accumulation mesh
/// carries CustomInts (liquid also CustomFloats, topsoil CustomShorts) sized to the pass's
/// buffer capacity, and CustomMeshDataPart.SetFrom clones with
/// <c>Values.Clone()</c> - the FULL array. Two multipliers make that expensive: the
/// accumulation buffers grow to the high-water of the biggest chunk ever meshed and never
/// shrink, and the non-liquid passes never add a single value to their CustomInts - so every
/// part clone copied a high-water-sized array of zeroes, forever.
///
/// The prefix replaces CloneExtraData with a field-for-field faithful copy whose arrays are
/// sized by their counts (exactly what the per-face arrays in the original already do via
/// FastCopy). Uploads are driven by Count/AllocationSize, not Values.Length, and a later Add
/// on a tight clone grows the buffer like any other, so the only observable change is the
/// garbage that no longer exists. CloneExtraData is patched rather than the typed
/// CustomMeshDataPart Clone() methods because it is one non-generic ~50-line method - too big
/// to have been inlined into its callers (the dead-profiler lesson), and no generic-patching
/// caveats.
/// </summary>
public static class TightClonePatches
{
    public static bool Enabled = true;

    /// <summary>
    /// Second half, for the garbage that is content-sized but still garbage: the per-face
    /// extras (XyzFaces, RenderPassesAndExtraBits, the two colour-map id arrays) and the
    /// custom-part value arrays of every chunk part clone. The mesh recycler keeps the BASIC
    /// arrays (xyz, uv, rgba, flags, indices) alive across tesselations, but the engine's own
    /// comment on CloneExtraData says these "cannot sensibly be retained" and allocates them
    /// fresh for every part - then nulls them in Dispose right after the upload. Measured as
    /// the remaining "klone" share after the capacity fix: ~18 MB/s on the tesselation thread
    /// while streaming, and it is the worst kind of garbage the collector can get - allocated
    /// on one thread, alive across a few frames while the part waits in the upload queue
    /// (promoted to gen1 meanwhile), dead on another thread.
    ///
    /// Here they cycle through a size-class pool instead: rented in CloneExtraData when the
    /// destination is a recycler mesh (Recyclable - i.e. this is a chunk part, the only
    /// caller of CloneUsingRecycler), returned by a postfix on TesselatedChunkPart.AddToPools,
    /// the one place where every part's mesh has just been uploaded and disposed. The prefix
    /// captures the array references before the upload, the postfix returns exactly those -
    /// by then the mesh no longer holds them (DisposeExtraData nulled the fields), so the
    /// arrays are provably unreferenced. MeshData.Dispose itself is not patched on purpose:
    /// it is small enough to be inlined into its callers at tier 1, which is the class of
    /// silently dead patch this project has met before.
    ///
    /// Rented arrays are longer than the count (power of two classes). Nothing in the engine
    /// treats the length of these arrays as a count: the uploads read Count/BaseOffset, the
    /// growth paths compare Count against Length and resize when full, and AddMeshData walks
    /// by count. A part that grows one of these arrays after the clone (Array.Resize) simply
    /// hands a non-pool array back, which the pool accepts only when its length is a class
    /// size - and even then it is exclusive to the mesh, so pooling it is harmless.
    /// </summary>
    public static bool PoolExtras = true;

    /// <summary>Upper bound on bytes held across all element types; beyond it returns are dropped.</summary>
    public static int ExtrasPoolBudgetMb
    {
        get => budgetMb;
        set
        {
            budgetMb = value;
            Bytes.BudgetMb = Shorts.BudgetMb = Ints.BudgetMb = Floats.BudgetMb = value;
        }
    }
    private static int budgetMb = 64;

    public static long StatClones;
    public static long StatBytesSaved;

    internal static readonly ArrayPoolByClass<byte> Bytes = new(sizeof(byte));
    internal static readonly ArrayPoolByClass<short> Shorts = new(sizeof(short));
    internal static readonly ArrayPoolByClass<int> Ints = new(sizeof(int));
    internal static readonly ArrayPoolByClass<float> Floats = new(sizeof(float));

    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData> Lod0Ref =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData>("modelDataLod0");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData> Lod1Ref =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData>("modelDataLod1");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData> NotLod2FarRef =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData>("modelDataNotLod2Far");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData> Lod2FarRef =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData>("modelDataLod2Far");

    // customAllocationSize/allocationSize are private on the generic base; SetFrom copies
    // them, so a faithful replacement must too. One pair of accessors per closed type.
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<int>, bool> IntCustom =
        AccessTools.FieldRefAccess<CustomMeshDataPart<int>, bool>("customAllocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<int>, int> IntAlloc =
        AccessTools.FieldRefAccess<CustomMeshDataPart<int>, int>("allocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<float>, bool> FloatCustom =
        AccessTools.FieldRefAccess<CustomMeshDataPart<float>, bool>("customAllocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<float>, int> FloatAlloc =
        AccessTools.FieldRefAccess<CustomMeshDataPart<float>, int>("allocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<short>, bool> ShortCustom =
        AccessTools.FieldRefAccess<CustomMeshDataPart<short>, bool>("customAllocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<short>, int> ShortAlloc =
        AccessTools.FieldRefAccess<CustomMeshDataPart<short>, int>("allocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<byte>, bool> ByteCustom =
        AccessTools.FieldRefAccess<CustomMeshDataPart<byte>, bool>("customAllocationSize");
    private static readonly AccessTools.FieldRef<CustomMeshDataPart<byte>, int> ByteAlloc =
        AccessTools.FieldRefAccess<CustomMeshDataPart<byte>, int>("allocationSize");

    public static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(MeshData), "CloneExtraData")
                     ?? throw new InvalidOperationException("MeshData.CloneExtraData not found");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(TightClonePatches), nameof(CloneExtraDataPrefix)));

        var addToPools = AccessTools.Method(typeof(TesselatedChunkPart), "AddToPools")
                         ?? throw new InvalidOperationException("TesselatedChunkPart.AddToPools not found");
        harmony.Patch(addToPools,
            prefix: new HarmonyMethod(typeof(TightClonePatches), nameof(AddToPoolsPrefix)),
            postfix: new HarmonyMethod(typeof(TightClonePatches), nameof(AddToPoolsPostfix)));
    }

    /// <summary>
    /// The extras arrays of one part's up to four meshes, captured before the upload so the
    /// postfix can return exactly them. A struct in __state: no allocation per part.
    /// </summary>
    public struct Captured
    {
        public byte[] Faces0, Faces1, Faces2, Faces3;
        public byte[] Climate0, Climate1, Climate2, Climate3;
        public byte[] Season0, Season1, Season2, Season3;
        public short[] Passes0, Passes1, Passes2, Passes3;
        public int[] Ints0, Ints1, Ints2, Ints3;
        public float[] Floats0, Floats1, Floats2, Floats3;
        public short[] Shorts0, Shorts1, Shorts2, Shorts3;
        public byte[] Bytes0, Bytes1, Bytes2, Bytes3;
        public bool Any;
    }

    public static void AddToPoolsPrefix(TesselatedChunkPart __instance, out Captured __state)
    {
        __state = default;
        if (!PoolExtras) return;
        Capture(Lod0Ref(__instance), ref __state.Faces0, ref __state.Climate0, ref __state.Season0, ref __state.Passes0,
                ref __state.Ints0, ref __state.Floats0, ref __state.Shorts0, ref __state.Bytes0, ref __state.Any);
        Capture(Lod1Ref(__instance), ref __state.Faces1, ref __state.Climate1, ref __state.Season1, ref __state.Passes1,
                ref __state.Ints1, ref __state.Floats1, ref __state.Shorts1, ref __state.Bytes1, ref __state.Any);
        Capture(NotLod2FarRef(__instance), ref __state.Faces2, ref __state.Climate2, ref __state.Season2, ref __state.Passes2,
                ref __state.Ints2, ref __state.Floats2, ref __state.Shorts2, ref __state.Bytes2, ref __state.Any);
        Capture(Lod2FarRef(__instance), ref __state.Faces3, ref __state.Climate3, ref __state.Season3, ref __state.Passes3,
                ref __state.Ints3, ref __state.Floats3, ref __state.Shorts3, ref __state.Bytes3, ref __state.Any);
    }

    private static void Capture(MeshData m, ref byte[] faces, ref byte[] climate, ref byte[] season, ref short[] passes,
                                ref int[] ints, ref float[] floats, ref short[] shorts, ref byte[] bytes, ref bool any)
    {
        // Only meshes the recycler handed out: their extras are exclusively ours. A part
        // mesh that came through the small-mesh Clone() path is not Recyclable and keeps
        // vanilla's count-exact arrays, which is fine - nothing captured, nothing returned.
        if (m == null || !m.Recyclable) return;
        faces = m.XyzFaces;
        climate = m.ClimateColorMapIds;
        season = m.SeasonColorMapIds;
        passes = m.RenderPassesAndExtraBits;
        ints = m.CustomInts?.Values;
        floats = m.CustomFloats?.Values;
        shorts = m.CustomShorts?.Values;
        bytes = m.CustomBytes?.Values;
        any = true;
    }

    /// <summary>Runs only when AddToPools completed - i.e. every mesh was uploaded and disposed.</summary>
    public static void AddToPoolsPostfix(Captured __state)
    {
        if (!__state.Any) return;
        Bytes.Return(__state.Faces0); Bytes.Return(__state.Faces1); Bytes.Return(__state.Faces2); Bytes.Return(__state.Faces3);
        Bytes.Return(__state.Climate0); Bytes.Return(__state.Climate1); Bytes.Return(__state.Climate2); Bytes.Return(__state.Climate3);
        Bytes.Return(__state.Season0); Bytes.Return(__state.Season1); Bytes.Return(__state.Season2); Bytes.Return(__state.Season3);
        Shorts.Return(__state.Passes0); Shorts.Return(__state.Passes1); Shorts.Return(__state.Passes2); Shorts.Return(__state.Passes3);
        Ints.Return(__state.Ints0); Ints.Return(__state.Ints1); Ints.Return(__state.Ints2); Ints.Return(__state.Ints3);
        Floats.Return(__state.Floats0); Floats.Return(__state.Floats1); Floats.Return(__state.Floats2); Floats.Return(__state.Floats3);
        Shorts.Return(__state.Shorts0); Shorts.Return(__state.Shorts1); Shorts.Return(__state.Shorts2); Shorts.Return(__state.Shorts3);
        Bytes.Return(__state.Bytes0); Bytes.Return(__state.Bytes1); Bytes.Return(__state.Bytes2); Bytes.Return(__state.Bytes3);
    }

    /// <summary>Frees every held array. World leave: the pool is world-agnostic, the memory is not free.</summary>
    public static void ClearPools()
    {
        Bytes.Clear();
        Shorts.Clear();
        Ints.Clear();
        Floats.Clear();
    }

    public static long PooledBytes => Bytes.HeldBytes + Shorts.HeldBytes + Ints.HeldBytes + Floats.HeldBytes;
    public static long StatExtrasHits => Bytes.StatHits + Shorts.StatHits + Ints.StatHits + Floats.StatHits;
    public static long StatExtrasMisses => Bytes.StatMisses + Shorts.StatMisses + Ints.StatMisses + Floats.StatMisses;
    public static long StatExtrasDropped => Bytes.StatDropped + Shorts.StatDropped + Ints.StatDropped + Floats.StatDropped;

    public static bool CloneExtraDataPrefix(MeshData __instance, MeshData dest)
    {
        if (!Enabled) return true;
        var src = __instance;
        // Recyclable marks a mesh the recycler handed out - a chunk part clone, whose extras
        // come back through AddToPools. Every other destination keeps fresh, exact arrays.
        var pooled = PoolExtras && dest.Recyclable;

        if (src.Normals != null) dest.Normals = Copy(src.Normals, src.NormalsCount);
        if (src.XyzFaces != null)
        {
            dest.XyzFaces = pooled ? Bytes.Rent(src.XyzFacesCount, src.XyzFaces) : Copy(src.XyzFaces, src.XyzFacesCount);
            dest.XyzFacesCount = src.XyzFacesCount;
        }
        if (src.TextureIndices != null)
        {
            dest.TextureIndices = Copy(src.TextureIndices, src.TextureIndicesCount);
            dest.TextureIndicesCount = src.TextureIndicesCount;
            dest.TextureIds = (int[])src.TextureIds.Clone();
        }
        if (src.ClimateColorMapIds != null)
        {
            dest.ClimateColorMapIds = pooled ? Bytes.Rent(src.ColorMapIdsCount, src.ClimateColorMapIds) : Copy(src.ClimateColorMapIds, src.ColorMapIdsCount);
            dest.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.SeasonColorMapIds != null)
        {
            dest.SeasonColorMapIds = pooled ? Bytes.Rent(src.ColorMapIdsCount, src.SeasonColorMapIds) : Copy(src.SeasonColorMapIds, src.ColorMapIdsCount);
            dest.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.RenderPassesAndExtraBits != null)
        {
            dest.RenderPassesAndExtraBits = pooled ? Shorts.Rent(src.RenderPassCount, src.RenderPassesAndExtraBits) : Copy(src.RenderPassesAndExtraBits, src.RenderPassCount);
            dest.RenderPassCount = src.RenderPassCount;
        }

        if (src.CustomFloats != null)
        {
            var c = new CustomMeshDataPartFloat();
            CopyTight(src.CustomFloats, c, FloatCustom, FloatAlloc, sizeof(float), pooled ? Floats : null);
            dest.CustomFloats = c;
        }
        if (src.CustomShorts != null)
        {
            var c = new CustomMeshDataPartShort();
            CopyTight(src.CustomShorts, c, ShortCustom, ShortAlloc, sizeof(short), pooled ? Shorts : null);
            dest.CustomShorts = c;
        }
        if (src.CustomBytes != null)
        {
            var c = new CustomMeshDataPartByte();
            CopyTight(src.CustomBytes, c, ByteCustom, ByteAlloc, sizeof(byte), pooled ? Bytes : null);
            dest.CustomBytes = c;
        }
        if (src.CustomInts != null)
        {
            var c = new CustomMeshDataPartInt();
            CopyTight(src.CustomInts, c, IntCustom, IntAlloc, sizeof(int), pooled ? Ints : null);
            dest.CustomInts = c;
        }

        Interlocked.Increment(ref StatClones);
        return false;
    }

    /// <summary>Everything SetFrom copies, with Values sized by Count instead of capacity.</summary>
    private static void CopyTight<T>(CustomMeshDataPart<T> src, CustomMeshDataPart<T> dst,
        AccessTools.FieldRef<CustomMeshDataPart<T>, bool> customAlloc,
        AccessTools.FieldRef<CustomMeshDataPart<T>, int> allocSize,
        int elementSize, ArrayPoolByClass<T> pool)
    {
        customAlloc(dst) = customAlloc(src);
        allocSize(dst) = allocSize(src);
        dst.Count = src.Count;
        if (src.Values != null)
        {
            dst.Values = pool != null ? pool.Rent(src.Count, src.Values) : Copy(src.Values, src.Count);
            Interlocked.Add(ref StatBytesSaved, (long)(src.Values.Length - src.Count) * elementSize);
        }
        if (src.InterleaveSizes != null) dst.InterleaveSizes = (int[])src.InterleaveSizes.Clone();
        if (src.InterleaveOffsets != null) dst.InterleaveOffsets = (int[])src.InterleaveOffsets.Clone();
        dst.InterleaveStride = src.InterleaveStride;
        dst.Instanced = src.Instanced;
        dst.StaticDraw = src.StaticDraw;
        dst.BaseOffset = src.BaseOffset;
    }

    private static T[] Copy<T>(T[] source, int count)
    {
        var result = new T[count];
        Array.Copy(source, result, count);
        return result;
    }

    public static void ResetStats()
    {
        StatClones = 0;
        StatBytesSaved = 0;
        Bytes.ResetStats();
        Shorts.ResetStats();
        Ints.ResetStats();
        Floats.ResetStats();
    }
}
