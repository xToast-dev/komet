using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Frustum gate for <see cref="AnimatableRenderer"/> - the engine's renderer for animated
/// block entities (windmill rotors, pulverizers, bellows, fruit presses, doors and trapdoors
/// while they swing, and every modded block that uses BlockEntityAnimationUtil).
///
/// Each instance registers itself in FOUR stages (Opaque or OIT, ShadowFar, ShadowNear) and
/// its OnRenderFrame has no distance or visibility test of any kind: it declares
/// RenderRange 99, which the engine never reads, and then does a shader switch, a
/// GetLightRGBs chunk lookup, ~15 uniform uploads, a UBO update and a draw - wherever the
/// block is, for as long as any of its animations runs. A windmill three thousand blocks
/// behind the camera costs exactly as much as one in front of it, three times a frame.
///
/// The gate is exact: it skips a call only when the mesh's bounding sphere lies entirely
/// outside the frustum the engine is rendering with at that moment - the camera frustum in
/// Opaque and OIT, the light-space box in the shadow stages (SystemRenderShadowMap calls
/// CalcFrustumEquations with the shadow projection before those stages' renderers run) -
/// where the GPU would have produced no fragments anyway. Nothing that would have reached the
/// screen or the shadow map is dropped, and no renderer that follows can tell the difference:
/// an idle instance (ShouldRender false) already returns before its first GL call, so
/// successors could never rely on this renderer's GL state to begin with.
///
/// The sphere is computed once from the mesh handed to the constructor, around the pivot the
/// model matrix rotates and scales about (block corner + 0.5, 0, 0.5), and padded for
/// animation: every keyframe moves elements by rotations about joint origins inside the shape
/// and by offsets, and |v'-C| &lt;= |v-C| + 2|P-C| bounds a rotation about any point P, so
/// three times the rest radius plus two blocks covers anything a vanilla animation does with
/// room to spare. Scale is read live (the fields are public and mutable); a CustomTransform
/// is an arbitrary matrix the renderer's owner set, so such instances are never gated.
/// </summary>
public static class AnimatableCullPatches
{
    public static bool Enabled = true;

    public static long StatCalls;
    public static long StatSkipped;

    /// <summary>Rest radius multiplier and the block margin added on top - see the class note.</summary>
    public const float RadiusFactor = 3f;
    public const float RadiusMargin = 2f;

    private sealed class Bounds
    {
        public float Radius;
    }

    private static readonly ConditionalWeakTable<AnimatableRenderer, Bounds> BoundsOf = new();

    private static readonly AccessTools.FieldRef<AnimatableRenderer, Vec3d> PosRef =
        AccessTools.FieldRefAccess<AnimatableRenderer, Vec3d>("pos");

    private static readonly AccessTools.FieldRef<AnimatableRenderer, ICoreClientAPI> CapiRef =
        AccessTools.FieldRefAccess<AnimatableRenderer, ICoreClientAPI>("capi");

    public static void Apply(Harmony harmony)
    {
        var ctor = AccessTools.Constructor(typeof(AnimatableRenderer),
                       new[] { typeof(ICoreClientAPI), typeof(Vec3d), typeof(Vec3f), typeof(AnimatorBase),
                               typeof(Dictionary<string, AnimationMetaData>), typeof(MeshData), typeof(EnumRenderStage) })
                   ?? throw new InvalidOperationException("AnimatableRenderer constructor not found");
        harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(AnimatableCullPatches), nameof(CtorPostfix)));

        var render = AccessTools.Method(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame),
                         new[] { typeof(float), typeof(EnumRenderStage) })
                     ?? throw new InvalidOperationException("AnimatableRenderer.OnRenderFrame not found");
        harmony.Patch(render, prefix: new HarmonyMethod(typeof(AnimatableCullPatches), nameof(RenderPrefix)));
    }

    /// <summary>The mesh is only available here; the renderer keeps a GPU handle without bounds.</summary>
    public static void CtorPostfix(AnimatableRenderer __instance, MeshData meshdata)
    {
        var rest = RestRadius(meshdata);
        if (float.IsNaN(rest)) return; // no usable geometry - this instance is never gated
        BoundsOf.AddOrUpdate(__instance, new Bounds { Radius = GateRadius(rest) });
    }

    /// <summary>
    /// Largest distance of any vertex from the pivot (0.5, 0, 0.5) in mesh space, or NaN when
    /// there is no geometry to measure. Pure, for the verify harness.
    /// </summary>
    public static float RestRadius(MeshData mesh)
    {
        var xyz = mesh?.xyz;
        if (xyz == null) return float.NaN;
        var n = Math.Min(mesh.VerticesCount, xyz.Length / 3);
        if (n <= 0) return float.NaN;

        var maxSq = 0f;
        for (var i = 0; i < n; i++)
        {
            var dx = xyz[3 * i] - 0.5f;
            var dy = xyz[3 * i + 1];
            var dz = xyz[3 * i + 2] - 0.5f;
            var d = dx * dx + dy * dy + dz * dz;
            if (d > maxSq) maxSq = d;
        }
        var r = MathF.Sqrt(maxSq);
        return float.IsNaN(r) || float.IsInfinity(r) ? float.NaN : r;
    }

    public static float GateRadius(float restRadius) => restRadius * RadiusFactor + RadiusMargin;

    public static bool RenderPrefix(AnimatableRenderer __instance, EnumRenderStage stage)
    {
        if (!Enabled) return true;
        // vanilla's own early-outs come first - an idle instance stays exactly vanilla
        if (!__instance.ShouldRender || __instance.CustomTransform != null) return true;
        if (!BoundsOf.TryGetValue(__instance, out var b)) return true;

        var pos = PosRef(__instance);
        var culler = CapiRef(__instance)?.Render?.DefaultFrustumCuller;
        if (pos == null || culler == null) return true;

        StatCalls++;
        if (!ShouldSkip(culler, pos.X, pos.Y, pos.Z, b.Radius,
                        __instance.ScaleX, __instance.ScaleY, __instance.ScaleZ))
            return true;

        StatSkipped++;
        return false;
    }

    /// <summary>
    /// The rule: skip only when the scaled sphere around the pivot is outside every frustum
    /// plane. Anything degenerate (zero, negative-and-zero, NaN or infinite scale, no radius)
    /// means "not certainly outside" and hands the call to vanilla.
    /// </summary>
    public static bool ShouldSkip(FrustumCulling culler, double x, double y, double z, float radius,
                                  float scaleX, float scaleY, float scaleZ)
    {
        var s = Math.Max(Math.Abs(scaleX), Math.Max(Math.Abs(scaleY), Math.Abs(scaleZ)));
        if (!(s > 0f) || float.IsInfinity(s)) return false;
        if (!(radius > 0f) || float.IsInfinity(radius)) return false;
        return !culler.SphereInFrustum(x + 0.5, y, z + 0.5, radius * s);
    }

    public static void ResetStats()
    {
        StatCalls = 0;
        StatSkipped = 0;
    }
}
