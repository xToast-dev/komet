using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Two allocations that happen once per animated entity per frame on the client.
/// Neither is expensive on its own; together they are a steady stream of gen0 garbage
/// that scales with the number of visible creatures.
/// </summary>
public static class AnimationPatches
{
    // ---- 1: AnimatorBase.OnFrame allocates a lowercase copy of every active anim code ----

    private static readonly AccessTools.FieldRef<AnimatorBase, Dictionary<string, RunningAnimation>> AnimsByCodeRef =
        AccessTools.FieldRefAccess<AnimatorBase, Dictionary<string, RunningAnimation>>("animsByCode");

    /// <summary>
    /// The constructor lowercases every animation code and stores it in an ordinal
    /// dictionary, so OnFrame and GetAnimationState have to call ToLowerInvariant() on the
    /// lookup key - one string allocation per active animation per entity per frame.
    /// Rebuilding the dictionary with an ordinal-ignore-case comparer makes the raw key work
    /// and lets the transpiler below delete those calls.
    /// </summary>
    public static void SwitchToCaseInsensitiveLookup(AnimatorBase animator)
    {
        ref var byCode = ref AnimsByCodeRef(animator);
        if (byCode == null || ReferenceEquals(byCode.Comparer, StringComparer.OrdinalIgnoreCase)) return;
        byCode = new Dictionary<string, RunningAnimation>(byCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Deletes every call to String.ToLowerInvariant, leaving the receiver on the stack.</summary>
    public static IEnumerable<CodeInstruction> DropToLowerInvariant(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.Method(typeof(string), nameof(string.ToLowerInvariant), Type.EmptyTypes);
        foreach (var ins in instructions)
        {
            if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ReferenceEquals(ins.operand, target))
            {
                // A call that pops one value and pushes one can be replaced by a nop without
                // unbalancing the stack; the original string simply stays where the lowercase
                // copy would have been.
                CodeInstruction nop = new(OpCodes.Nop);
                nop.labels.AddRange(ins.labels);
                nop.blocks.AddRange(ins.blocks);
                yield return nop;
                continue;
            }
            yield return ins;
        }
    }

    // ---- 2: AnimationManager.OnClientFrame runs LINQ over a dictionary every frame -------

    /// <summary>
    /// Replaces "ActiveAnimationsByAnimCode.Any(anim =&gt; anim.Value.AdjustCollisionBox)".
    /// Enumerable.Any goes through IEnumerable&lt;T&gt;, which boxes Dictionary's struct
    /// enumerator; the foreach below does not.
    /// </summary>
    public static bool AnyAdjustCollisionBox(Dictionary<string, AnimationMetaData> anims)
    {
        foreach (var anim in anims)
        {
            if (anim.Value.AdjustCollisionBox) return true;
        }
        return false;
    }

    /// <summary>Rewrites the Enumerable.Any call site to the allocation free helper above.</summary>
    public static IEnumerable<CodeInstruction> ReplaceAnyWithLoop(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(AnimationPatches), nameof(AnyAdjustCollisionBox));
        var buffer = new List<CodeInstruction>();

        foreach (var ins in instructions) buffer.Add(ins);

        for (var i = 0; i < buffer.Count; i++)
        {
            var ins = buffer[i];
            if (ins.opcode != OpCodes.Call || ins.operand is not MethodInfo m) continue;
            if (m.Name != "Any" || m.DeclaringType != typeof(System.Linq.Enumerable)) continue;

            // Stack at this point: IEnumerable<KVP>, Func<KVP,bool>.
            // Drop the delegate, then call our helper on the dictionary. The dictionary was
            // pushed as IEnumerable<KVP>, so cast it back before the call.
            buffer[i] = new CodeInstruction(OpCodes.Pop) { labels = ins.labels, blocks = ins.blocks };
            buffer.Insert(i + 1, new CodeInstruction(OpCodes.Castclass, typeof(Dictionary<string, AnimationMetaData>)));
            buffer.Insert(i + 2, new CodeInstruction(OpCodes.Call, replacement));
            break;
        }

        return buffer;
    }
}

[HarmonyPatch]
public static class AnimatorBaseCtorPatch
{
    public static MethodBase TargetMethod() => AccessTools.Constructor(
        typeof(AnimatorBase),
        [typeof(WalkSpeedSupplierDelegate), typeof(Animation[]), typeof(Action<string>)]);

    [HarmonyPostfix]
    public static void Postfix(AnimatorBase __instance) => AnimationPatches.SwitchToCaseInsensitiveLookup(__instance);
}
