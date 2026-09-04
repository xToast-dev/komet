using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Runtime;
using Vintagestory.API.Client;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Scales down ChunkTesselatorManager.OnBeforeFrame's per-frame chunk upload budget.
/// The timing that drives the throttle lives in the shared MeasurementPatches, which the
/// baseline mod also uses - this only adds the transpiler that acts on the measurement.
/// </summary>
public static class UploadBudgetPatches
{
    public static void Apply(Harmony harmony)
    {
        var target = Measure.MeasurementPatches.UploadMethod
                     ?? throw new InvalidOperationException("measurement patches must be applied first");

        // the throttle needs its own clock around the same method the measurement times
        Measure.MeasurementPatches.UploadBegin += UploadBudget.FrameStart;
        Measure.MeasurementPatches.UploadEnd += UploadBudget.FrameEnd;

        harmony.Patch(target, transpiler: new HarmonyMethod(
            AccessTools.Method(typeof(UploadBudgetPatches), nameof(Transpiler))));
    }

    /// <summary>
    /// Takes the clock back off the shared events on world leave. They are static, so every
    /// rejoin would otherwise stack another FrameStart/FrameEnd pair - and a doubled FrameEnd
    /// applies the controller's correction twice per frame, squaring every adjustment.
    /// </summary>
    public static void Unhook()
    {
        Measure.MeasurementPatches.UploadBegin -= UploadBudget.FrameStart;
        Measure.MeasurementPatches.UploadEnd -= UploadBudget.FrameEnd;
    }


    /// <summary>
    /// Wraps the "ViewDistanceSq / 48 + 350" budget in a call to UploadBudget.Scale.
    ///
    /// The anchor is the load of FrustumCulling.ViewDistanceSq, which appears exactly once in
    /// this method; the call is inserted immediately before the store that ends the
    /// expression. If the shape ever stops matching, the transpiler throws and the mod logs
    /// the failure and runs without this optimisation rather than emitting broken IL.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var viewDistSq = AccessTools.Field(typeof(FrustumCulling), nameof(FrustumCulling.ViewDistanceSq));
        var scale = AccessTools.Method(typeof(UploadBudget), nameof(UploadBudget.Scale));

        var anchor = -1;
        var anchorCount = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].opcode == OpCodes.Ldfld && ReferenceEquals(code[i].operand, viewDistSq))
            {
                if (anchor < 0) anchor = i;
                anchorCount++;
            }
        }
        if (anchorCount != 1)
            throw new InvalidOperationException($"expected exactly one ViewDistanceSq load, found {anchorCount}");

        // walk forward to the store that closes the expression (div, add, then stloc)
        var store = -1;
        for (var i = anchor + 1; i < code.Count && i <= anchor + 8; i++)
        {
            if (IsStoreLocal(code[i].opcode)) { store = i; break; }
        }
        if (store < 0) throw new InvalidOperationException("no local store found after the ViewDistanceSq load");

        code.Insert(store, new CodeInstruction(OpCodes.Call, scale));
        return code;
    }

    private static bool IsStoreLocal(OpCode op) =>
        op == OpCodes.Stloc || op == OpCodes.Stloc_S ||
        op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 ||
        op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;
}
