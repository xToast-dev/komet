using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Patches;

/// <summary>
/// Wires <see cref="WindowPrebuilder"/> into the tesselation thread.
///
/// Three patches on BuildExtendedChunkData: a prefix that takes the global window-build lock
/// and swaps in a prebuilt window when one matches (skipping the original), a postfix that
/// finishes validation and asks the worker for the next window, and a finalizer that releases
/// the lock however the method exits. The lock is the load-bearing part: vanilla's window
/// build and the worker's both go through the static BlockChunkDataLayer.blocksByPaletteIndex,
/// and two builds at once corrupt each other silently.
///
/// The prefix runs at low priority so the measurement prefix on the same method has always
/// captured its timestamp first - on a hit the neighbour-cost row then honestly reports the
/// near-zero time the window phase actually took.
///
/// A postfix on SunRelightChunk timestamps every relight: relight runs at pop time BEFORE
/// vanilla's window build, so a window built earlier would bake pre-relight light values -
/// the prebuilder rejects itself when a relight happened after its build started.
/// </summary>
public static class WindowPipelinePatches
{
    [ThreadStatic] private static int lockDepth;

    public static void Apply(Harmony harmony, int validateFirstN)
    {
        WindowPrebuilder.EnsureReady();
        WindowPrebuilder.ValidateRemaining = Math.Max(0, validateFirstN);
        WindowPrebuilder.Enabled = true;

        MethodInfo build = AccessTools.Method(typeof(ChunkTesselator), "BuildExtendedChunkData")
                           ?? throw new InvalidOperationException("BuildExtendedChunkData not found");

        harmony.Patch(build,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(WindowPipelinePatches), nameof(BuildPrefix)))
                { priority = Priority.Low },
            postfix: new HarmonyMethod(AccessTools.Method(typeof(WindowPipelinePatches), nameof(BuildPostfix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(WindowPipelinePatches), nameof(BuildFinalizer))));

        MethodInfo relight = AccessTools.Method(typeof(TerrainIlluminator), "SunRelightChunk",
                                 [typeof(ClientChunk), typeof(Vintagestory.Common.Database.ChunkPos)])
                             ?? throw new InvalidOperationException("SunRelightChunk not found");
        harmony.Patch(relight, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(WindowPipelinePatches), nameof(RelightPostfix))));

        // The staleness guard: every mutation that can change a window's content marks its
        // chunk dirty, so both marking funnels feed a per-chunk timestamp. Without this the
        // only thing standing between a stale window and a wrongly lit chunk mesh was the
        // element-wise validation - which stops after the first N windows.
        Type map = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientWorldMap")
                   ?? throw new InvalidOperationException("ClientWorldMap not found");
        harmony.Patch(AccessTools.Method(map, "SetChunkDirty"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(WindowPipelinePatches), nameof(NoteSetDirty))));
        harmony.Patch(AccessTools.Method(map, "MarkChunkDirty"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(WindowPipelinePatches), nameof(NoteMarkDirty))));
        ChunkMarkClock.Enabled = true;
    }

    private static readonly AccessTools.FieldRef<object, int> MulXRef =
        AccessTools.FieldRefAccess<int>(AccessTools.TypeByName("Vintagestory.Common.WorldMap"), "index3dMulX");
    private static readonly AccessTools.FieldRef<object, int> MulZRef =
        AccessTools.FieldRefAccess<int>(AccessTools.TypeByName("Vintagestory.Common.WorldMap"), "index3dMulZ");

    /// <summary>SetChunkDirty already speaks index3d - the clock's own key.</summary>
    public static void NoteSetDirty(long index3d) => ChunkMarkClock.Note(index3d);

    /// <summary>MarkChunkDirty speaks chunk coordinates; same key, same formula as the engine.</summary>
    public static void NoteMarkDirty(object __instance, int cx, int cy, int cz)
        => ChunkMarkClock.Note(ChunkMarkClock.Key(cx, cy, cz, MulXRef(__instance), MulZRef(__instance)));

    /// <summary>False = a prebuilt window was copied in and the original build is skipped.</summary>
    public static bool BuildPrefix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, bool skipChunkCenter)
    {
        Monitor.Enter(WindowPrebuilder.BuildLock);
        lockDepth++;
        return !WindowPrebuilder.TryUse(__instance, chunkX, chunkY, chunkZ, skipChunkCenter);
    }

    public static void BuildPostfix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, bool skipChunkCenter)
        => WindowPrebuilder.AfterBuild(__instance, chunkX, chunkY, chunkZ, skipChunkCenter);

    /// <summary>Runs on every exit path, exception included - the lock must never leak.</summary>
    public static void BuildFinalizer()
    {
        if (lockDepth > 0)
        {
            lockDepth--;
            Monitor.Exit(WindowPrebuilder.BuildLock);
        }
    }

    public static void RelightPostfix() => WindowPrebuilder.NoteRelight();
}
