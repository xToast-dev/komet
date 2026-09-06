using System;
using HarmonyLib;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Gives the near shadow cascade back the half of its depth range that cannot cast a shadow.
///
/// The near pass is the most expensive thing in the frame - a GPU report has it at 15 to 18 ms
/// of a 20 ms GPU frame - and it did not move when the map went from 4096 to 2048 px, so the
/// cost is the GEOMETRY the volume holds. This is about the part of that volume that cannot
/// hold a caster at all.
///
/// The engine builds the near box for a 39-block cascade and then extends it:
///
///     ShadowBox.ShadowBoxZExtend = 50 + 50*|1 - sunY| + 100;   // 150..200 blocks
///     ...
///     maxZ += ShadowBoxZExtend;                                // ShadowBox.update()
///
/// The intent is plain and correct: light space looks along the sun (LookAt(eye = sunPosition,
/// center = 0), so +z is TOWARDS the sun), and only geometry at a HIGHER light-space z than a
/// receiver can shade it. So the box is stretched up-sun, and only up-sun. maxZ is raised, minZ
/// is left alone.
///
/// And then the projection throws that away:
///
///     projectionMatrix[10] = -2.0 / length;   // and NO translation
///
/// An ortho matrix with no translation clips |z| &lt;= length/2 about the light-space ORIGIN, so
/// the volume the pass actually draws is [-length/2, +length/2] - it uses only the box's LENGTH
/// and never learns where the box sits. The stretch therefore lands half up-sun and half
/// down-sun. The down-sun half is 90-odd blocks of world BELOW the receivers as the sun sees
/// them, drawn into the shadow map every frame, and no fragment of it can darken anything: it
/// is behind every receiver it could be tested against.
///
/// Fixing it is one term. The up-sun plane stays exactly where vanilla put it - so every
/// occluder vanilla drew is still drawn, and nothing about the picture changes - and the
/// down-sun plane moves up to the last receiver the near map can still serve, which
/// shadowcoords.vsh names exactly:
///
///     max(0.0, len / shadowRangeNear - 0.15)      // near weight is 0 beyond 1.15 * range
///
/// Nothing further than 1.15 * 39 = 45 blocks from the camera reads the near map at all, and
/// light space is a rotation, so that Euclidean bound bounds the light-space depth too. Below
/// that plane there is no receiver to shade and no reason to draw.
///
/// How much that is turned out smaller than the first estimate, and the reason is worth
/// writing down. The light view is <c>LookAt(eye = SunPosition, center = 0)</c>, and
/// <c>ClientGameCalendar</c> sets <c>SunPosition = SunPositionNormalized * 50</c> - so the
/// light-space origin the ortho centres on is not the camera but a point FIFTY blocks up-sun
/// of it. Relative to the camera, vanilla's volume is [-length/2 + 50, +length/2 + 50]: on the
/// field numbers (236 blocks) 68 blocks down-sun and 168 up-sun. The receivers need 45 of
/// those 68, so the fit takes about 15 blocks - 6 % - not the quarter the first version of
/// this comment expected from a camera at the origin. It stays, because it is correct, costs
/// nothing, and is what makes <see cref="ShadowPatches.NearDepthExtend"/> safe: the cap used
/// to shorten the volume symmetrically and below ~125 blocks pushed the down-sun plane above
/// receivers the near map still serves; with the fit the down-sun plane is derived from the
/// receivers and the cap only ever moves the up-sun end.
///
/// Two things come free with a shorter box. fogandlight.fsh biases the near lookup by a
/// CONSTANT 0.0005 in normalised depth, which is 0.0005 * the box depth in blocks - a shorter
/// box shrinks the world-space bias by the same factor, which is what peter-panning under
/// foliage is made of. And the depth buffer spreads the same precision over less world.
///
/// FAR CASCADE: deliberately untouched. It has the same defect, but it is 2 to 6 ms amortised
/// against the near cascade's 15 to 18, its map is retained and reprojected across frames
/// (ShadowThrottlePatches), and its box is already replaced wholesale by
/// <see cref="ShadowPatches.MakeBoxSymmetric"/>. One cascade at a time, with a number after it.
/// </summary>
public static class ShadowDepthPatches
{
    /// <summary>Off is exactly vanilla: the projection keeps its untranslated depth range.</summary>
    public static bool Enabled;

    /// <summary>What komet.json asked for, so safemode has something to come back to.</summary>
    public static bool ConfiguredEnabled;

    /// <summary>Whether the patch is on the method at all - the toggle needs to tell
    /// "switched off" apart from "never installed".</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// How far past its own range the near map still serves a receiver, from shadowcoords.vsh:
    /// the weight is clamp(1 - (len/shadowRangeNear - 0.15) - edge terms, 0, 1), which reaches
    /// zero at len = 1.15 * shadowRangeNear. Beyond that the far cascade alone lights the pixel,
    /// whatever the near map holds.
    /// </summary>
    internal const double FadeReach = 1.15;

    /// <summary>
    /// Room to leave below the deepest receiver, as a fraction of the volume's depth.
    ///
    /// shadowcoords.vsh cuts the near map off with <c>max(0, z - 0.98) * 100</c> and drops it
    /// entirely at z >= 0.999 - a hard edge, not a fade, and a receiver pushed onto it would
    /// lose its near shadow in a step. 3 % puts the deepest receiver the map can serve at
    /// z = 0.97, on the flat side of that knee with the whole ramp still to spare.
    /// </summary>
    internal const double KneeFraction = 0.03;

    /// <summary>Floor under <see cref="KneeFraction"/>, in blocks, for a very short volume.</summary>
    internal const double MinBackPad = 8.0;

    /// <summary>Vanilla's near volume depth and the fitted one, in blocks, for the report.</summary>
    public static double VanillaLength { get; private set; }
    public static double FittedLength { get; private set; }

    /// <summary>Depth ranges fitted, so the HUD can show the patch is doing something.</summary>
    public static long StatFits;

    private static readonly AccessTools.FieldRef<SystemRenderShadowMap, double[]> LightViewRef =
        AccessTools.FieldRefAccess<SystemRenderShadowMap, double[]>("lightViewMatrix");

    public static void Apply(Harmony harmony, bool enabled)
    {
        if (LightViewRef == null)
            throw new InvalidOperationException("SystemRenderShadowMap.lightViewMatrix not found");

        var ortho = AccessTools.Method(typeof(SystemRenderShadowMap), "loadOrthoModeMatrix",
                        [typeof(double[]), typeof(double), typeof(double), typeof(double)])
                    ?? throw new InvalidOperationException("loadOrthoModeMatrix not found");

        // A second postfix on the method ShadowStabilityPatches already snaps: that one writes
        // the x and y translation terms, this one the z scale and translation, so they compose
        // in either order.
        harmony.Patch(ortho, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowDepthPatches), nameof(FitDepthRange))));

        Installed = true;
        ConfiguredEnabled = enabled;
        Enabled = enabled;
    }

    /// <summary>
    /// The rule, pure: vanilla's volume in, the fitted one out. Everything the patch decides is
    /// here, so verify can pin the two properties that matter without a running client -
    /// that the up-sun plane never moves, and that the volume only ever shrinks.
    /// </summary>
    /// <param name="length">The box depth the engine sized the projection with.</param>
    /// <param name="camLightZ">The camera's light-space z - the translation term of the light
    /// view matrix, since the engine renders camera-relative and Camera.OriginPosition is the
    /// zero vector.</param>
    /// <param name="shadowDistance">ShadowBox.SHADOW_DISTANCE, the near cascade's range.</param>
    /// <param name="vanillaLength">The depth the box would have had with the engine's own
    /// extend - the same as <paramref name="length"/> unless <see cref="ShadowPatches.NearDepthExtend"/>
    /// capped it. The down-sun plane is never allowed below this volume's, and never above the
    /// receivers': so a cap can shorten the up-sun end as far as it likes and the near map keeps
    /// serving every receiver it did before.</param>
    /// <param name="minZ">The near plane of the fitted range, in light space.</param>
    /// <param name="maxZ">The far plane of it. Both are only meaningful when this returns true.</param>
    internal static bool DepthRangeFor(double length, double camLightZ, double shadowDistance,
                                       double vanillaLength, out double minZ, out double maxZ)
    {
        minZ = maxZ = 0;
        if (!(length > 0) || !(shadowDistance > 0)) return false;
        if (double.IsNaN(length) || double.IsNaN(camLightZ) || double.IsNaN(shadowDistance)) return false;
        if (!(vanillaLength >= length) || double.IsNaN(vanillaLength)) vanillaLength = length;

        var half = length / 2.0;

        // The up-sun end, unchanged. Vanilla's volume is [-half, +half] about the light-space
        // origin because the ortho carries no translation; keeping +half is what makes this
        // free of any visual change - no occluder that used to be drawn stops being drawn.
        maxZ = half;

        // The down-sun end: the deepest receiver the near map can still serve, plus room for
        // the z > 0.98 knee.
        var deepest = camLightZ - FadeReach * shadowDistance;
        var span = maxZ - deepest;
        if (!(span > 0)) return false;
        minZ = deepest - Math.Max(MinBackPad, span * KneeFraction);

        // Never below the UNCAPPED vanilla volume's own plane: where the fitted plane would
        // sit below it - a very low sun, a short extend - vanilla's stands, and the patch is a
        // no-op for that frame rather than a widening. Measured against the capped volume it
        // can be a widening, and that is the point: the cap shortens the up-sun end, the
        // receivers keep their floor.
        if (minZ < -vanillaLength / 2.0) minZ = -vanillaLength / 2.0;
        return maxZ - minZ > 0;
    }

    /// <summary>
    /// Runs right after the ortho projection is built and before it is combined with the light
    /// view matrix - the same window <see cref="ShadowStabilityPatches.SnapToTexelGrid"/> uses,
    /// and the last moment the depth range can still be changed. Everything downstream
    /// (the pushed PMatrix, shadowMvpMatrix, toShadowMapSpaceMatrixNear, and the six frustum
    /// planes CalcFrustumEquations derives from PMatrix.Top) is built from this matrix
    /// afterwards, so the shadow lookup and the CPU cull follow the new range on their own.
    /// </summary>
    public static void FitDepthRange(SystemRenderShadowMap __instance, double[] projectionMatrix,
                                     double length)
    {
        if (!Enabled || projectionMatrix == null || projectionMatrix.Length < 16) return;
        // Near cascade only - see the FAR CASCADE note on the class.
        if (ShadowPatches.PreparingFarCascade) return;

        try
        {
            var lightView = LightViewRef(__instance);
            if (lightView == null || lightView.Length < 16) return;

            // ShadowBox.update() ran a few lines earlier with this same matrix (LookAt writes
            // the new one only AFTER the projection is built), so the box and this camera
            // position are in the same light space - which is the whole reason the fit is
            // allowed to reason about where the box sits.
            // ScaleDistance recorded both extends before the box was built, so the uncapped
            // depth is the capped one with the difference put back.
            var vanillaLength = length - ShadowPatches.NearExtendUsed + ShadowPatches.NearExtendVanilla;
            if (!DepthRangeFor(length, lightView[14], ShadowBox.SHADOW_DISTANCE, vanillaLength,
                               out var minZ, out var maxZ))
                return;

            var fitted = maxZ - minZ;
            projectionMatrix[10] = -2.0 / fitted;
            projectionMatrix[14] = (minZ + maxZ) / fitted;

            VanillaLength = vanillaLength;
            FittedLength = fitted;
            StatFits++;
        }
        catch (Exception)
        {
            // A depth range that cannot be fitted is vanilla's. Switch off for the session
            // rather than risk a shadow pass every frame.
            Enabled = false;
        }
    }
}
