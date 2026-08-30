using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Patches;

/// <summary>
/// Fixes the hard edge where distant shadows stop, and lets the shadowed range be scaled.
///
/// SystemRenderShadowMap runs two cascades. The near one is self consistent: at shadow
/// quality 4 it sets the shadowRangeNear uniform to 39 and builds the shadow box for 39.
/// The far one is not - it sets shadowRangeFar to 150 + 120*(quality-1) = 510, but builds the
/// box for only *half* of that:
///
///     game.shUniforms.ShadowRangeFar = (float)num;                       // 510
///     PrepareForShadowRendering((shadowMapQuality > 1) ? (num / 2.0) : num, ...);  // 255
///
/// shadowcoords.vsh fades the shadow out with two terms: a distance term
/// max(0, len/shadowRangeFar - 0.15), and edge terms on the shadow map UVs multiplied by 10.
/// The weight hits zero once their sum reaches 0.75, so with shadowRangeFar = 510 the smooth
/// distance fade would not finish until len = 459 - but the shadow map only holds data to
/// 255. The UV edge terms get there first, and because of the x10 they slam the shadow off
/// over a few metres instead of fading it. That is the visible cutoff.
///
/// Telling the shader the range the map actually covers lets the distance term finish first,
/// at 0.9 * distance, comfortably inside the box: the shadow fades out smoothly and the edge
/// terms never bite. Shadows end slightly nearer, but without a seam.
/// </summary>
public static class ShadowPatches
{
    private static readonly AccessTools.FieldRef<ShadowBox, Camera> CameraRef =
        AccessTools.FieldRefAccess<ShadowBox, Camera>("camera");

    /// <summary>Scales the far cascade's shadow box. 1.0 is vanilla.</summary>
    public static double DistanceMultiplier = 1.0;

    /// <summary>What the config asked for, so a live toggle can return to it.</summary>
    public static double ConfiguredMultiplier = 1.0;

    // All three patches are applied unconditionally and gated here at runtime. They are the
    // only things in this mod that change how shadows look, and until 1.37.0 they were the
    // only ones that could NOT be switched off in a running session - so every shadow
    // complaint cost a restart per hypothesis, which this project has repeatedly learned is
    // the slowest possible way to find a visual culprit.

    /// <summary>
    /// Replace vanilla's cone-shaped shadow box with a camera-centred cube - for the FAR
    /// cascade only, since 1.43.0.
    ///
    /// Far only, for two reasons that took three releases to see clearly. The near cascade has
    /// a safety net: where its map runs out, nearSub drops to zero and the far map takes over
    /// seamlessly, so its coverage gaps are invisible - and widening it to a sphere (which this
    /// patch did until now) only halved its texel density for nothing. The far map has NO net:
    /// where it runs out, shadowcoords.vsh's UV edge terms cut the shadow off, and that cut is
    /// the hard, view-direction-dependent line the user photographed. Only the far cascade has
    /// the problem, so only the far cascade pays for the fix.
    /// </summary>
    public static bool SymmetricBox = true;

    /// <summary>
    /// Set by the <see cref="ScaleDistance"/> prefix, which is the one place that knows which
    /// cascade is being prepared: ShadowBox.update() itself has no idea, and telling the two
    /// apart by SHADOW_DISTANCE would couple this to the engine's quality formulas.
    /// </summary>
    private static bool farCascade;

    // ---- how big the cube really has to be, read off shadowcoords.vsh ----------------
    //
    // The far cascade's weight is w = clamp(1.5 - 2d, 0, 1) with
    //     d = clamp(uvEdgeTerms * 10 + max(0, len / shadowRangeFar - 0.15), 0, 1),
    // so the shadow is fully faded once d reaches 0.75. With the UV terms at zero that is
    // len / shadowRangeFar = 0.90: nothing beyond 0.90 R is shadowed at all, whatever the map
    // holds. The box therefore only has to cover a sphere of 0.90 R, not R.
    //
    // But it has to cover it INSIDE the band where the UV terms are still zero, which is
    // uv in [0.03, 0.97] - i.e. the middle 94 % of each axis. So the half-size must satisfy
    // 0.94 * halfSize >= 0.90 * R.
    //
    // Net: the cube can be 4,3 % smaller per axis than the R it used to use, for 8 % less
    // light-space area at exactly the same visible result. Derived, not guessed - and the
    // verify test checks the property against these two shader constants rather than against
    // a remembered radius.

    /// <summary>Fraction of shadowRangeFar at which shadowcoords.vsh has faded the shadow to zero.</summary>
    internal const double FadeCompleteFraction = 0.90;

    /// <summary>Fraction of the box half-size still free of the shader's UV edge terms (uv &lt;= 0.97).</summary>
    internal const double SafeUvFraction = 0.94;

    /// <summary>Box half-size as a fraction of the fade range. See the block above.</summary>
    internal const double BoxRadiusFactor = FadeCompleteFraction / SafeUvFraction;

    /// <summary>Tell the shader the range the far map really covers.</summary>
    public static bool FadeFix = true;

    /// <summary>Report what the far cascade ended up covering, for the HUD.</summary>
    public static double ShadowDistance { get; private set; }
    public static double ShadowRangeUniform { get; private set; }

    /// <summary>
    /// The far cascade's actual light-space footprint in blocks, longest side - what the shadow
    /// map's texels are really spread over.
    ///
    /// This used to be estimated from ShadowDistance (2x for the sphere box, 0.78x for vanilla's
    /// wedge). The vanilla half of that estimate is a VIEW space width, while the box is the AABB
    /// of eight frustum corners AFTER the light transform - so it depends on where the sun is,
    /// which no formula in ShadowDistance can know. Reading the box the engine actually built
    /// removes the guess: verify measures vanilla at 257 blocks with the sun at 5 degrees, ~450
    /// at 45-65 and 397 in the zenith, against the sphere box's constant 488.
    /// </summary>
    public static double ShadowBoxSpan { get; private set; }

    public static void Apply(Harmony harmony, bool fadeFix, double distanceMultiplier, bool symmetricBox)
    {
        ConfiguredMultiplier = distanceMultiplier;
        DistanceMultiplier = distanceMultiplier;
        SymmetricBox = symmetricBox;
        FadeFix = fadeFix;

        Type type = typeof(SystemRenderShadowMap);

        MethodInfo update = AccessTools.Method(typeof(ShadowBox), nameof(ShadowBox.update))
                            ?? throw new InvalidOperationException("ShadowBox.update not found");
        harmony.Patch(update, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(MakeBoxSymmetric))));

        MethodInfo prepare = AccessTools.Method(type, "PrepareForShadowRendering",
                                 [typeof(double), typeof(EnumFrameBuffer), typeof(float)])
                             ?? throw new InvalidOperationException("PrepareForShadowRendering not found");
        harmony.Patch(prepare, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(ScaleDistance))));

        MethodInfo far = AccessTools.Method(type, "OnRenderShadowFar", [typeof(float)])
                         ?? throw new InvalidOperationException("OnRenderShadowFar not found");
        harmony.Patch(far, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(MatchFadeToBox))));
    }

    /// <summary>Hands every shadow behaviour back to vanilla in one call (safemode).</summary>
    public static void ToVanilla()
    {
        SymmetricBox = false;
        FadeFix = false;
        DistanceMultiplier = 1.0;
    }

    /// <summary>Restores whatever komet.json configured.</summary>
    public static void ToConfigured(bool symmetricBox, bool fadeFix)
    {
        SymmetricBox = symmetricBox;
        FadeFix = fadeFix;
        DistanceMultiplier = ConfiguredMultiplier;
    }

    /// <summary>
    /// Replaces the shadow box's bounds with a cube centred on the camera.
    ///
    /// Vanilla builds the box as a cone along the fixed world -Z axis - getCameraRotationMatrix
    /// returns the identity, so the "camera direction" never enters - sized 0.78 R across and
    /// only 0.45 R tall (FoV 70, 16:9). In every other direction the shadow map ends long
    /// before the smooth distance fade would finish at 0.9 R, and there the UV edge terms
    /// (times ten in shadowcoords.vsh) cut the shadow off hard: a visible line whose distance
    /// depends on which way you look. The near cascade shows no seam precisely because its
    /// coverage matches its fade range - this gives the far cascade the same property.
    ///
    /// A cube of half-size <see cref="BoxRadiusFactor"/> x R contains the whole sphere the fade
    /// lives in, so the distance fade wins in every direction. Why a sphere and not something
    /// cleverer: loadOrthoModeMatrix writes no translation, so the projection is centred on the
    /// camera and only the box's SPANS matter - coverage is camera +- span/2 per axis whatever
    /// the box's min/max say. The sphere is the optimal rotation-invariant shape for exactly
    /// that projection; a camera-oriented wedge was designed and rejected, because with the sun
    /// overhead (both light axes horizontal) it degenerates to the same spans as the sphere at
    /// FoV 70 / 16:9, and unlike the sphere it would go stale whenever the camera turns.
    ///
    /// Measured price with the 1.42.x both-cascades version (`.komet stress`, settled scene at
    /// 6,58 ms): +0,72 ms +-0,08 against vanilla's cone. Far-only is strictly cheaper (the near
    /// pass no longer draws a doubled volume); the next stress run prices the remainder.
    /// </summary>
    public static void MakeBoxSymmetric(ShadowBox __instance)
    {
        // The near cascade is always left exactly vanilla - see SymmetricBox. (The span the
        // HUD reports is captured in MatchFadeToBox, right after the far cascade rendered:
        // capturing it here got overwritten by the near cascade, whose stage runs later, and
        // the HUD then reported 126 texels per block for a 255-block cascade - the near box's
        // number on the far box's row.)
        if (!SymmetricBox || !farCascade) return;
        try
        {
            Camera camera = CameraRef(__instance);
            Vintagestory.API.MathTools.Vec3d origin = camera?.OriginPosition;
            double[] lightView = __instance.lightViewMatrix;
            double r = ShadowBox.SHADOW_DISTANCE * BoxRadiusFactor;
            if (origin == null || lightView == null || lightView.Length < 16 || r <= 0) return;

            SymmetricLightSpaceBounds(lightView, origin.X, origin.Y, origin.Z, r,
                out double minX, out double minY, out double minZ,
                out double maxX, out double maxY, out double maxZ);

            __instance.minX = minX; __instance.maxX = maxX;
            __instance.minY = minY; __instance.maxY = maxY;
            __instance.minZ = minZ;
            // vanilla extends only the light-facing depth, so occluders between the sun and
            // the covered volume still land in the map
            __instance.maxZ = maxZ + ShadowBox.ShadowBoxZExtend;
        }
        catch (Exception)
        {
            // fall back to the vanilla cone rather than risk the shadow pass
        }
    }

    /// <summary>
    /// Light-space bounds of the SPHERE of radius r around the camera - the smallest box that
    /// still covers everything the distance fade can reach.
    ///
    /// The first version took the light-space hull of the cube [-r, r]^3, which contains that
    /// sphere but is up to sqrt(3) = 1.73x larger per axis (worst case: light direction along
    /// a cube diagonal). Since the light view matrix is a rotation plus a translation, a
    /// sphere stays a sphere, so its bounds are exactly centre +- r on every axis, whatever
    /// the sun is doing. Same coverage guarantee, a box that is up to 1.73x smaller in every
    /// direction - and both of the things the user sees follow from that size:
    ///
    /// - texel density: the same 6144^2 map over a smaller area means finer shadows;
    /// - depth bias: the shader subtracts a CONSTANT 0.0009 in normalised depth
    ///   (fogandlight.fsh), which is 0.0009 * the box's light-space depth in blocks. The cube
    ///   hull stretched that depth so far that the bias grew past the thickness of a leaf
    ///   block, and distant foliage stopped casting any shadow at all. A shorter box shrinks
    ///   the bias in world units by the same factor and brings those shadows back.
    ///
    /// Pure, so the containment property is testable without an engine.
    /// </summary>
    internal static void SymmetricLightSpaceBounds(double[] lightView,
        double camX, double camY, double camZ, double r,
        out double minX, out double minY, out double minZ,
        out double maxX, out double maxY, out double maxZ)
    {
        // Mat4d.MulWithVec4 with w = 1, column-major - the same transform vanilla uses for
        // its frustum points, applied to the sphere's centre only.
        double cx = lightView[0] * camX + lightView[4] * camY + lightView[8] * camZ + lightView[12];
        double cy = lightView[1] * camX + lightView[5] * camY + lightView[9] * camZ + lightView[13];
        double cz = lightView[2] * camX + lightView[6] * camY + lightView[10] * camZ + lightView[14];

        minX = cx - r; maxX = cx + r;
        minY = cy - r; maxY = cy + r;
        minZ = cz - r; maxZ = cz + r;
    }

    /// <summary>Only the far cascade is stretched; the near one is already tight around the player.</summary>
    public static void ScaleDistance(ref double shadowDistance, EnumFrameBuffer fb)
    {
        // update() runs inside the body this prefixes, so the flag is always current when the
        // box postfix reads it
        farCascade = fb == EnumFrameBuffer.ShadowmapFar;
        if (farCascade && DistanceMultiplier != 1.0)
            shadowDistance *= DistanceMultiplier;
    }

    private static readonly AccessTools.FieldRef<SystemRenderShadowMap, ShadowBox> ShadowBoxRef =
        AccessTools.FieldRefAccess<SystemRenderShadowMap, ShadowBox>("shadowBox");

    /// <summary>
    /// Runs after the far cascade has built its box, so ShadowBox.SHADOW_DISTANCE holds what
    /// the map really covers. The chunk shaders read shUniforms later in the frame.
    /// </summary>
    public static void MatchFadeToBox(SystemRenderShadowMap __instance, ClientMain ___game)
    {
        double distance = ShadowBox.SHADOW_DISTANCE;
        ShadowDistance = distance;

        // The far cascade has just rendered and the near one has not run yet, so right here -
        // and only here - the box fields are guaranteed to hold the FAR box, whichever shape
        // built it. Width and Height are the two axes the map's texels are spread over.
        try
        {
            ShadowBox box = ShadowBoxRef(__instance);
            double span = box == null ? 0 : Math.Max(box.Width, box.Height);
            if (span > 0 && !double.IsNaN(span)) ShadowBoxSpan = span;
        }
        catch (Exception) { /* a missing span only costs a HUD row */ }

        // Off: vanilla already wrote its own ShadowRangeFar earlier in this very method, so
        // simply not overwriting it is exactly the vanilla behaviour.
        if (!FadeFix) return;
        if (___game?.shUniforms == null || distance <= 0) return;

        // the shader's distance term reaches full fade at 0.9 * range, so range = distance
        // puts the end of the fade just inside the edge of the map
        float range = (float)distance;
        ___game.shUniforms.ShadowRangeFar = range;
        ShadowRangeUniform = range;
    }
}
