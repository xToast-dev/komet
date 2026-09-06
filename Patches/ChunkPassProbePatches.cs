using System;
using HarmonyLib;
using Komet.Measure;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// The camera pass's bracket for <see cref="GpuPassProbe"/>: ChunkRenderer.RenderOpaque, the
/// 17-million-triangle pass whose GPU time the stage timestamps booked as 0,0. The shadow
/// passes' brackets sit on the transpiled boundary ShadowCullPatches owns.
/// </summary>
public static class ChunkPassProbePatches
{
    public static void Apply(Harmony harmony)
    {
        var opaque = AccessTools.Method(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)])
                     ?? throw new InvalidOperationException("ChunkRenderer.RenderOpaque not found");
        harmony.Patch(opaque,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ChunkPassProbePatches), nameof(BeforeOpaque))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkPassProbePatches), nameof(AfterOpaque))));
    }

    public static void BeforeOpaque() => GpuPassProbe.Begin(GpuPassProbe.Pass.CameraOpaque);
    public static void AfterOpaque() => GpuPassProbe.End(GpuPassProbe.Pass.CameraOpaque);
}
