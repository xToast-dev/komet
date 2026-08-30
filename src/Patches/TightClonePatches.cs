using System;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;

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

    public static long StatClones;
    public static long StatBytesSaved;

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
    }

    public static bool CloneExtraDataPrefix(MeshData __instance, MeshData dest)
    {
        if (!Enabled) return true;
        MeshData src = __instance;

        if (src.Normals != null) dest.Normals = Copy(src.Normals, src.NormalsCount);
        if (src.XyzFaces != null)
        {
            dest.XyzFaces = Copy(src.XyzFaces, src.XyzFacesCount);
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
            dest.ClimateColorMapIds = Copy(src.ClimateColorMapIds, src.ColorMapIdsCount);
            dest.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.SeasonColorMapIds != null)
        {
            dest.SeasonColorMapIds = Copy(src.SeasonColorMapIds, src.ColorMapIdsCount);
            dest.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.RenderPassesAndExtraBits != null)
        {
            dest.RenderPassesAndExtraBits = Copy(src.RenderPassesAndExtraBits, src.RenderPassCount);
            dest.RenderPassCount = src.RenderPassCount;
        }

        if (src.CustomFloats != null)
        {
            var c = new CustomMeshDataPartFloat();
            CopyTight(src.CustomFloats, c, FloatCustom, FloatAlloc, sizeof(float));
            dest.CustomFloats = c;
        }
        if (src.CustomShorts != null)
        {
            var c = new CustomMeshDataPartShort();
            CopyTight(src.CustomShorts, c, ShortCustom, ShortAlloc, sizeof(short));
            dest.CustomShorts = c;
        }
        if (src.CustomBytes != null)
        {
            var c = new CustomMeshDataPartByte();
            CopyTight(src.CustomBytes, c, ByteCustom, ByteAlloc, sizeof(byte));
            dest.CustomBytes = c;
        }
        if (src.CustomInts != null)
        {
            var c = new CustomMeshDataPartInt();
            CopyTight(src.CustomInts, c, IntCustom, IntAlloc, sizeof(int));
            dest.CustomInts = c;
        }

        Interlocked.Increment(ref StatClones);
        return false;
    }

    /// <summary>Everything SetFrom copies, with Values sized by Count instead of capacity.</summary>
    private static void CopyTight<T>(CustomMeshDataPart<T> src, CustomMeshDataPart<T> dst,
        AccessTools.FieldRef<CustomMeshDataPart<T>, bool> customAlloc,
        AccessTools.FieldRef<CustomMeshDataPart<T>, int> allocSize,
        int elementSize)
    {
        customAlloc(dst) = customAlloc(src);
        allocSize(dst) = allocSize(src);
        dst.Count = src.Count;
        if (src.Values != null)
        {
            dst.Values = new T[src.Count];
            Array.Copy(src.Values, dst.Values, src.Count);
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
    }
}
