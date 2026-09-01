using HarmonyLib;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
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
