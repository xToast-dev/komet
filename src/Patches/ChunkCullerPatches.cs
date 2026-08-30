using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Patches;

/// <summary>Routes the occlusion pass through <see cref="FastChunkCuller"/>.</summary>
[HarmonyPatch(typeof(ChunkCuller))]
public static class ChunkCullerPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ChunkCuller.CullInvisibleChunks))]
    public static bool CullInvisibleChunks(ChunkCuller __instance)
    {
        // returns true when it wants vanilla to handle this call after all
        return FastChunkCuller.Cull(__instance);
    }
}
