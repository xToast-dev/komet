using HarmonyLib;
using Vintagestory.API.Client;

namespace Komet.Patches;

/// <summary>
/// Routes the per-frame visibility sweep through <see cref="FastCuller"/> and keeps its
/// per-pool cache in sync. TryAdd and RemoveLocation are the only two methods in the engine
/// that change a pool's location list.
/// </summary>
[HarmonyPatch(typeof(MeshDataPool))]
public static class MeshDataPoolPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MeshDataPool.FrustumCull))]
    public static bool FrustumCull(MeshDataPool __instance, FrustumCulling frustumCuller, EnumFrustumCullMode frustumCullMode)
    {
        if (!FastCuller.Enabled) return true; // safemode: vanilla does the whole sweep

        FastCuller.Cull(__instance, frustumCuller, frustumCullMode);
        return false; // skip the original
    }

    private static readonly HarmonyLib.AccessTools.FieldRef<MeshDataPool, System.Collections.Generic.List<ModelDataPoolLocation>> LocationsRef =
        HarmonyLib.AccessTools.FieldRefAccess<MeshDataPool, System.Collections.Generic.List<ModelDataPoolLocation>>("poolLocations");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MeshDataPool.TryAdd))]
    public static void TryAdd(MeshDataPool __instance, ModelDataPoolLocation __result)
    {
        // A null result means the pool had no room and nothing changed.
        if (__result == null) return;

        // TryAdd either appends or, above 3 % fragmentation, squeezes the part into a gap in
        // the middle - and the second case shifts every following index, which invalidates the
        // spatial index built on them. Telling the two apart is just asking whether the new
        // part is the last one: an append can be folded into the cache without rebuilding it.
        var locations = LocationsRef(__instance);
        if (locations != null && locations.Count > 0 && ReferenceEquals(locations[locations.Count - 1], __result))
            FastCuller.NoteAppended(__instance);
        else
            FastCuller.NoteInserted(__instance, __result);
    }

    /// <summary>
    /// The only thing that moves the frustum planes. Batched culling keys off this, so it has
    /// to know when the camera or the shadow projection changed.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FrustumCulling), nameof(FrustumCulling.CalcFrustumEquations),
                  new[] { typeof(Vintagestory.API.MathTools.BlockPos), typeof(double[]), typeof(double[]) })]
    public static void CalcFrustumEquations() => FastCuller.FrustumGeneration++;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MeshDataPool.RemoveLocation))]
    public static void RemoveLocation(MeshDataPool __instance)
    {
        FastCuller.Invalidate(__instance);
    }

}
