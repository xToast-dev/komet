using System;
using System.Collections.Generic;
using HarmonyLib;
using Komet.Culling;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Tells the sweep which chunk render pass it is sweeping for.
///
/// ChunkRenderer keeps one MeshDataPoolManager per (pass, atlas) in poolsByRenderPass and
/// calls Render on each in turn; the manager culls and draws its pools, and neither the manager
/// nor the pool knows the pass it belongs to. The managers are created once in the renderer's
/// constructor, so a lookup by reference, built the first time and rebuilt when a manager is
/// missing (the renderer was recreated with the world), names the pass. The prefix hands it to
/// <see cref="FastCuller.CurrentPass"/> for the pools culled inside, the postfix takes it back
/// so a decal pool or anything else culled outside a manager stays "unknown".
/// </summary>
public static class PoolPassPatches
{
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> ChunkRendererRef =
        AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");

    private static readonly Dictionary<MeshDataPoolManager, int> map = new(ReferenceEqualityComparer.Instance);
    private static ChunkRenderer mappedFor;

    public static long StatUnknown;

    public static void Apply(Harmony harmony)
    {
        if (ChunkRendererRef == null) throw new InvalidOperationException("ClientMain.chunkRenderer not found");
        var render = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render),
                         [typeof(Vintagestory.API.MathTools.Vec3d), typeof(string), typeof(EnumFrustumCullMode)])
                     ?? throw new InvalidOperationException("MeshDataPoolManager.Render not found");
        harmony.Patch(render,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PoolPassPatches), nameof(NotePass))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(PoolPassPatches), nameof(ForgetPass))));
    }

    private static readonly AccessTools.FieldRef<MeshDataPoolManager, List<MeshDataPool>> PoolsRef =
        AccessTools.FieldRefAccess<MeshDataPoolManager, List<MeshDataPool>>("pools");

    public static void NotePass(MeshDataPoolManager __instance, Vintagestory.API.MathTools.Vec3d playerpos,
                                EnumFrustumCullMode frustumCullMode)
    {
        FastCuller.CurrentPass = PassOf(__instance);

        // The camera pass draws this manager's pools in list order: nearest first, so that
        // the depth test rejects what the farther pools would have shaded for nothing. The
        // shadow passes are depth-only and do not care; they keep the list as it is.
        if (FastCuller.FrontToBack && frustumCullMode == EnumFrustumCullMode.CullNormal && playerpos != null)
        {
            try { FastCuller.SortPools(PoolsRef(__instance), playerpos.X, playerpos.Y, playerpos.Z); }
            catch (Exception) { /* an unsorted frame draws the same picture */ }
        }
    }

    public static void ForgetPass()
    {
        FastCuller.CurrentPass = -1;
    }

    private static int PassOf(MeshDataPoolManager manager)
    {
        if (manager == null) return -1;
        if (map.TryGetValue(manager, out var pass)) return pass;

        try
        {
            var game = ShadowCullPatches.Game;
            var renderer = game == null ? null : ChunkRendererRef(game);
            var table = renderer?.poolsByRenderPass;
            if (table == null) { StatUnknown++; return -1; }

            // rebuild only when the renderer changed, or on the first miss for this one
            if (!ReferenceEquals(mappedFor, renderer) || !map.ContainsKey(manager))
            {
                map.Clear();
                for (var p = 0; p < table.Length; p++)
                {
                    var row = table[p];
                    if (row == null) continue;
                    foreach (var m in row) if (m != null) map[m] = p;
                }
                mappedFor = renderer;
            }
            if (map.TryGetValue(manager, out pass)) return pass;
        }
        catch (Exception)
        {
            // an unnamed pass only costs a histogram row
        }
        StatUnknown++;
        // remember the miss so a foreign manager does not rebuild the table every frame
        map[manager] = -1;
        return -1;
    }

    public static void Reset()
    {
        map.Clear();
        mappedFor = null;
        FastCuller.CurrentPass = -1;
    }
}
