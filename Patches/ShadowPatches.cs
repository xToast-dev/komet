using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
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

    /// <summary>What komet.json asked for, so safemode has something to come back to.</summary>
    public static bool ConfiguredTightCull = true;

    // ---- the near cascade's depth ------------------------------------------------------

    /// <summary>
    /// Cap on the NEAR cascade's <c>ShadowBoxZExtend</c>, in blocks. 0 = vanilla's.
    ///
    /// This is the one number that decides how much geometry the near pass draws, and the
    /// reason took a wrong turn to find. The near pass does not submit a loose band that the
    /// GPU then clips: the six planes it culls against ARE the shadow projection's clip volume
    /// (PrepareForShadowRendering feeds CalcFrustumEquations the ortho matrix and a look-at
    /// along the light), so what it draws is exactly what the volume holds - and the volume's
    /// depth is <c>maxZ += ShadowBoxZExtend</c>.
    ///
    /// Vanilla sets that to <c>50 + 50*|1-sunY| + 100</c> for the near cascade, i.e. 150 to 200
    /// blocks - MORE than the far cascade's own <c>100 + 60*|1-sunY|</c>, for a cascade covering
    /// 39 blocks instead of 255. So the near volume is a 60-block-wide column of the world two
    /// hundred blocks deep, and in a forest that column is foliage from the ground to the top of
    /// every tree in it. A GPU report has that pass at 20 of 24 ms, and it did not move when the
    /// map went from 4096 to 2048 px - it is the geometry, not the fill.
    ///
    /// What a shorter depth costs is occluders further up-sun than the cap: they stop casting
    /// into the NEAR map. They still cast in the far map, whose box is six times larger, so the
    /// result is a shadow at half strength rather than none (fogandlight.fsh adds the two
    /// cascades' contributions). Flat forest never notices; a mountain up-sun does. That is a
    /// judgement about a particular world, so the default is vanilla and
    /// '.komet shadowneardepth &lt;blocks&gt;' prices it live.
    ///
    /// It composes with <see cref="ShadowDepthPatches"/>, which is the half of this problem
    /// that costs nothing: the ortho carries no translation, so vanilla spends the extend half
    /// up-sun and half DOWN-sun, where no fragment can reach a receiver. The fit removes that
    /// half outright; this cap trades away up-sun reach on top of it, and only this one is a
    /// judgement call.
    /// </summary>
    public static double NearDepthExtend;

    /// <summary>What komet.json asked for - safemode comes back to it.</summary>
    public static double ConfiguredNearDepthExtend;

    /// <summary>The extend the near cascade last used, and vanilla's, for the report.</summary>
    public static double NearExtendUsed { get; private set; }
    public static double NearExtendVanilla { get; private set; }

    /// <summary>The rule, pure: vanilla's extend and the cap in, the extend to use out. A cap
    /// of zero or less is "leave it alone", and it never lengthens the box.</summary>
    internal static double NearExtendFor(double vanilla, double cap)
        => cap > 0 && cap < vanilla ? cap : vanilla;

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

    /// <summary>
    /// Which cascade PrepareForShadowRendering is currently building - true for the far one.
    /// Valid inside that method (the shadow box update, the ortho matrix and its texel
    /// snapping all run there); the texel snapping needs it now that the two cascades' maps
    /// can differ in size, and a grid quantised to the wrong map is no snap at all.
    /// </summary>
    public static bool PreparingFarCascade => farCascade;

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

    /// <summary>
    /// Extra blocks of far-cascade coverage, on top of what the fade needs - the room a
    /// RETAINED shadow map needs to survive the camera moving.
    ///
    /// The throttle already keeps the far map for several frames and reprojects it exactly
    /// (ShadowThrottlePatches.OffsetShadowMatrix), but compensation can only keep a map
    /// correctly POSITIONED - it cannot extend what the map COVERS. So the throttle has to
    /// redraw as soon as the camera has moved 0,15 blocks, which at 85 fps is every frame
    /// while walking and every frame while flying: the whole saving existed only for a player
    /// standing still, i.e. exactly when nobody needs it.
    ///
    /// This buys the missing room. The box is a sphere around the camera (see
    /// MakeBoxSymmetric), so a box drawn with radius r+m at C0 contains the sphere of radius r
    /// around every camera position within m of C0 - a containment property, not an estimate,
    /// and verify pins it. The throttle's movement limit is raised to 0.9*m in step
    /// (ShadowThrottlePatches.MoveLimitFor), so the far cascade then updates at the staleness
    /// cap instead of every frame, whatever the player is doing.
    ///
    /// The price is texel density: the same map over a box that is m blocks wider per side.
    /// At the default far distance that is about 6 % coarser shadows beyond the near cascade,
    /// against roughly a quarter of the far cascade's GPU cost while moving. It deliberately
    /// does NOT go into ShadowBox.SHADOW_DISTANCE: MatchFadeToBox derives the shader's fade
    /// range from that, the fade would grow with the box, and the extra coverage would be
    /// consumed by the very fade it is supposed to outlive.
    ///
    /// Only meaningful with the symmetric box - vanilla's cone has no containment property to
    /// build on, and its shape depends on where the sun is.
    /// </summary>
    public static double FarBoxMargin;

    /// <summary>The margin that is actually in effect - zero without the symmetric box.</summary>
    public static double EffectiveFarBoxMargin => SymmetricBox && FarBoxMargin > 0 ? FarBoxMargin : 0.0;

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

    /// <summary>The near cascade's range and light-space footprint, captured the same way
    /// right after its pass - the pair the HUD's "near map ... texels per block" row needs.
    /// 0 until the near cascade has rendered once (quality 2 and up).</summary>
    public static double NearShadowDistance { get; private set; }
    public static double NearBoxSpan { get; private set; }

    // ---- the cull range, cut down to what the projection actually keeps ------------------

    /// <summary>
    /// Cull the shadow passes against the volume the shadow projection really covers, instead
    /// of vanilla's world-axis estimate. Off is exactly vanilla.
    /// </summary>
    public static bool TightCullBox = true;

    /// <summary>
    /// Slack on the tightened range, in blocks.
    ///
    /// Two things have to fit in it. The engine's range test compares the part's bounding
    /// SPHERE CENTRE against the player's block position, so a part whose centre sits just
    /// outside the box can still reach into it - a chunk-sized part's half-diagonal is
    /// sqrt(3) * 16 = 27,7 blocks. And the test measures from the player, which in third
    /// person is a few blocks from the camera.
    ///
    /// What is NOT slack, and is added separately (<see cref="LightEyeOffset"/>): the box is
    /// not centred on the camera. The frustum look-at's eye is <c>CameraPos + SunPosition</c>,
    /// and SunPosition is the normalised direction times 50 - so the clip volume sits fifty
    /// blocks up-sun of the camera, and its world-axis footprint is offset from the player by
    /// 50 * |sun.x| and 50 * |sun.z|. The first version of this range called that "the light
    /// matrix's own unit eye offset" and folded it into the pad; at a 35-degree sun the offset
    /// is 41 blocks, the pad minus the part radius is 20, and a band of casters at the up-sun
    /// edge of the volume was range-culled that the planes would have kept - the long shadows
    /// of a hill up-sun, missing from the near map. verify now places the eye where the game
    /// does and fails without the term.
    ///
    /// 48 covers the part radius and the third-person offset with room to spare. It is slack on
    /// a range that is otherwise exact, and the range is only ever narrowed, never widened - so
    /// over-estimating it costs a little of the saving and can never cost a shadow.
    /// </summary>
    internal const double TightCullPad = 48.0;

    /// <summary>|SunPosition|: ClientGameCalendar sets it to the normalised direction times 50,
    /// and both light look-ats put their eye there.</summary>
    internal const double LightEyeOffset = 50.0;

    /// <summary>The world-axis distance from the player to the clip volume's centre, per axis:
    /// the light eye's offset projected onto that axis. Pure; the light view's third row is the
    /// light direction.</summary>
    internal static void EyeOffsets(double[] lightView, out double offX, out double offZ)
    {
        offX = offZ = 0;
        if (lightView == null || lightView.Length < 16) return;
        offX = Math.Abs(lightView[2]) * LightEyeOffset;
        offZ = Math.Abs(lightView[10]) * LightEyeOffset;
    }

    /// <summary>What the tightened ranges came out as, and vanilla's, for the report.</summary>
    public static double TightRangeX { get; private set; }
    public static double TightRangeZ { get; private set; }
    public static double VanillaRangeX { get; private set; }
    public static double VanillaRangeZ { get; private set; }

    /// <summary>
    /// The world-axis half-extents of the box the shadow projection keeps.
    ///
    /// loadOrthoModeMatrix writes 2/width, 2/height, -2/length and NO translation, so the clip
    /// volume is exactly |x| &lt;= width/2, |y| &lt;= height/2, |z| &lt;= length/2 in light space,
    /// centred on the camera. That is an oriented box in world space, and the smallest
    /// world-axis box around it has half-extents sum(h_j * |axis_j . world_i|) - the standard
    /// projection of an OBB onto an axis. The light view matrix is a rotation, so its columns
    /// are exactly those dot products (column-major: element [i*4 + j] is row j, column i).
    ///
    /// Pure, so verify can pin the containment property without an engine: no point that the
    /// projection keeps can lie outside what this returns.
    /// </summary>
    internal static void TightCullExtents(double[] lightView, double width, double height, double length,
                                          out double halfX, out double halfZ)
    {
        halfX = halfZ = 0;
        if (lightView == null || lightView.Length < 16) return;

        double hx = width / 2.0, hy = height / 2.0, hz = length / 2.0;
        if (!(hx > 0) || !(hy > 0) || !(hz > 0)) return;

        halfX = Math.Abs(lightView[0]) * hx + Math.Abs(lightView[1]) * hy + Math.Abs(lightView[2]) * hz;
        halfZ = Math.Abs(lightView[8]) * hx + Math.Abs(lightView[9]) * hy + Math.Abs(lightView[10]) * hz;
    }

    public static void Apply(Harmony harmony, bool fadeFix, double distanceMultiplier, bool symmetricBox,
                             bool tightCullBox = true, double nearDepthExtend = 0)
    {
        ConfiguredNearDepthExtend = nearDepthExtend;
        NearDepthExtend = nearDepthExtend;
        ConfiguredMultiplier = distanceMultiplier;
        DistanceMultiplier = distanceMultiplier;
        SymmetricBox = symmetricBox;
        FadeFix = fadeFix;
        ConfiguredTightCull = tightCullBox;
        TightCullBox = tightCullBox;

        var type = typeof(SystemRenderShadowMap);

        var update = AccessTools.Method(typeof(ShadowBox), nameof(ShadowBox.update))
                     ?? throw new InvalidOperationException("ShadowBox.update not found");
        harmony.Patch(update, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(MakeBoxSymmetric))));

        var prepare = AccessTools.Method(type, "PrepareForShadowRendering",
                          [typeof(double), typeof(EnumFrameBuffer), typeof(float)])
                      ?? throw new InvalidOperationException("PrepareForShadowRendering not found");
        harmony.Patch(prepare,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ShadowPatches), nameof(ScaleDistance))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ShadowPatches), nameof(PadCullRange))));

        var far = AccessTools.Method(type, "OnRenderShadowFar", [typeof(float)])
                  ?? throw new InvalidOperationException("OnRenderShadowFar not found");
        harmony.Patch(far, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(MatchFadeToBox))));

        var near = AccessTools.Method(type, "OnRenderShadowNear", [typeof(float)])
                   ?? throw new InvalidOperationException("OnRenderShadowNear not found");
        harmony.Patch(near, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowPatches), nameof(CaptureNearBox))));
    }

    /// <summary>
    /// Runs after the near cascade prepared its box, which is the only moment the box fields
    /// hold the NEAR box (the far one overwrote them a stage earlier and will again next
    /// frame). Pure bookkeeping for the HUD; nothing about the pass changes.
    /// </summary>
    public static void CaptureNearBox(SystemRenderShadowMap __instance)
    {
        try
        {
            var box = ShadowBoxRef(__instance);
            if (box == null) return;
            var span = Math.Max(box.Width, box.Height);
            if (span > 0 && !double.IsNaN(span))
            {
                NearBoxSpan = span;
                NearShadowDistance = ShadowBox.SHADOW_DISTANCE;
            }
        }
        catch (Exception) { /* a missing span only costs a HUD row */ }
    }

    /// <summary>Hands every shadow behaviour back to vanilla in one call (safemode).</summary>
    public static void ToVanilla()
    {
        SymmetricBox = false;
        FadeFix = false;
        TightCullBox = false;
        NearDepthExtend = 0;
        DistanceMultiplier = 1.0;
        // EffectiveFarBoxMargin follows SymmetricBox on its own, so the throttle's movement
        // limit drops back with it - but the map currently retained was drawn for the wide
        // box, and the next one will not be. One forced redraw, and the two agree again.
        ShadowThrottlePatches.Invalidate();
    }

    /// <summary>Restores whatever komet.json configured.</summary>
    public static void ToConfigured(bool symmetricBox, bool fadeFix)
    {
        SymmetricBox = symmetricBox;
        FadeFix = fadeFix;
        TightCullBox = ConfiguredTightCull;
        NearDepthExtend = ConfiguredNearDepthExtend;
        DistanceMultiplier = ConfiguredMultiplier;
        ShadowThrottlePatches.Invalidate();
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
            var camera = CameraRef(__instance);
            var origin = camera?.OriginPosition;
            var lightView = __instance.lightViewMatrix;
            // The fade needs BoxRadiusFactor x R; the margin is the room on top of it that a
            // retained map needs while the camera moves (see FarBoxMargin).
            var r = ShadowBox.SHADOW_DISTANCE * BoxRadiusFactor + EffectiveFarBoxMargin;
            if (origin == null || lightView == null || lightView.Length < 16 || r <= 0) return;

            SymmetricLightSpaceBounds(lightView, origin.X, origin.Y, origin.Z, r,
                out var minX, out var minY, out var minZ,
                out var maxX, out var maxY, out var maxZ);

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
        var cx = lightView[0] * camX + lightView[4] * camY + lightView[8] * camZ + lightView[12];
        var cy = lightView[1] * camX + lightView[5] * camY + lightView[9] * camZ + lightView[13];
        var cz = lightView[2] * camX + lightView[6] * camY + lightView[10] * camZ + lightView[14];

        minX = cx - r; maxX = cx + r;
        minY = cy - r; maxY = cy + r;
        minZ = cz - r; maxZ = cz + r;
    }

    /// <summary>
    /// Lets the sweep see the margin ring too.
    ///
    /// PrepareForShadowRendering derives the culler's shadowRangeX/Z from the shadow distance,
    /// a step BEFORE the box postfix widens the box - so without this, every part in the ring
    /// the margin just added would be culled away and the ring would be empty. An empty ring
    /// is the exact cut-off line the margin exists to prevent, only immediately instead of
    /// after a few frames.
    ///
    /// The engine's own values are the tight ones for a box without margin, so adding the
    /// margin is the same widening on both sides of the same box.
    /// </summary>
    public static void PadCullRange(SystemRenderShadowMap __instance, ClientMain ___game, EnumFrameBuffer fb)
    {
        var culler = ___game?.frustumCuller;
        if (culler == null) return;

        if (fb == EnumFrameBuffer.ShadowmapFar)
        {
            var margin = EffectiveFarBoxMargin;
            if (margin > 0)
            {
                culler.shadowRangeX += margin;
                culler.shadowRangeZ += margin;
            }
        }

        TightenCullRange(__instance, culler, fb);
    }

    /// <summary>
    /// Cuts the cull range down to the volume the projection keeps.
    ///
    /// What this is and - just as important - what it is NOT.
    ///
    /// Vanilla derives the range from the shadow DISTANCE and the depth extend:
    /// <c>shadowRangeX = distance + ShadowBoxZExtend + extra</c>, <c>shadowRangeZ = distance +
    /// extra</c>. The depth extend exists so that occluders between the sun and the covered
    /// volume are drawn - a depth-axis quantity - and it is spent on the world X axis whatever
    /// the sun is doing. For the NEAR cascade that is a 205 to 255-block band against a 49-block
    /// box, so it is a loose test by a wide margin.
    ///
    /// It is a PRE-FILTER, not the cull. InFrustumShadowPass runs this range test and then six
    /// plane tests, and during a shadow pass those planes are not the camera's: PrepareForShadow-
    /// Rendering calls CalcFrustumEquations with the ORTHO projection and a look-at along the
    /// light, so the six planes already bound the shadow projection's clip volume exactly. The
    /// range test only decides how many parts reach them.
    ///
    /// So tightening it costs the sweep less and draws exactly the same geometry. The first
    /// version of this comment claimed the pass was submitting a band an order of magnitude
    /// larger than the map could hold and that the GPU paid for the vertices - it was not, the
    /// planes had already dropped them, and the field measurement said so: the near cascade did
    /// not move. What is saved is real but it is CPU: six plane evaluations per part that the
    /// two range compares now answer on their own.
    ///
    /// The projection's own volume is known exactly (see <see cref="TightCullExtents"/>), so the
    /// range can be the world box around it plus <see cref="TightCullPad"/> of slack. Two rules
    /// keep this from changing what is drawn:
    ///
    ///   * it only ever NARROWS. Where vanilla is already tighter than the projection - which is
    ///     the normal case for Z, and for both axes with the sun low - vanilla's value stands.
    ///   * the pad keeps it conservative against the plane test, which is the thing that decides:
    ///     nothing the planes would have kept is dropped by the range.
    ///
    /// It feeds the culler's own fields, so vanilla's cull path and FastCuller both see it and
    /// cannot disagree - the cull verifier compares them against each other and is untouched.
    /// </summary>
    private static void TightenCullRange(SystemRenderShadowMap sys, FrustumCulling culler, EnumFrameBuffer fb)
    {
        // Only the near cascade's numbers are kept: it is the one whose band is out of all
        // proportion to its box, it is the last of the two to run, and a row that mixed the
        // two would report whichever cascade happened to render last.
        var near = fb == EnumFrameBuffer.ShadowmapNear;
        if (near)
        {
            VanillaRangeX = culler.shadowRangeX;
            VanillaRangeZ = culler.shadowRangeZ;
            TightRangeX = culler.shadowRangeX;
            TightRangeZ = culler.shadowRangeZ;
        }

        if (!TightCullBox || sys == null) return;

        try
        {
            var box = ShadowBoxRef(sys);
            var lightView = box?.lightViewMatrix;
            if (box == null) return;

            TightCullExtents(lightView, box.Width, box.Height, box.Length, out var halfX, out var halfZ);
            if (!(halfX > 0) || !(halfZ > 0) || double.IsNaN(halfX) || double.IsNaN(halfZ)) return;
            EyeOffsets(lightView, out var offX, out var offZ);

            var rx = halfX + offX + TightCullPad;
            var rz = halfZ + offZ + TightCullPad;
            if (rx < culler.shadowRangeX) culler.shadowRangeX = rx;
            if (rz < culler.shadowRangeZ) culler.shadowRangeZ = rz;

            if (near)
            {
                TightRangeX = culler.shadowRangeX;
                TightRangeZ = culler.shadowRangeZ;
            }
        }
        catch (Exception)
        {
            // a range that cannot be computed is simply vanilla's - never a missing shadow
        }
    }

    /// <summary>Only the far cascade is stretched; the near one is already tight around the player.</summary>
    public static void ScaleDistance(ref double shadowDistance, EnumFrameBuffer fb)
    {
        // update() runs inside the body this prefixes, so the flag is always current when the
        // box postfix reads it
        farCascade = fb == EnumFrameBuffer.ShadowmapFar;
        if (farCascade)
        {
            if (Math.Abs(DistanceMultiplier - 1.0) > double.Epsilon)
                shadowDistance *= DistanceMultiplier;
            return;
        }

        // The caller set ShadowBoxZExtend a line before this prefix and shadowBox.update() reads
        // it a line after, so this is the one place where capping it reaches the near box and
        // nothing else. See NearDepthExtend.
        NearExtendVanilla = ShadowBox.ShadowBoxZExtend;
        NearExtendUsed = NearExtendFor(ShadowBox.ShadowBoxZExtend, NearDepthExtend);
        ShadowBox.ShadowBoxZExtend = NearExtendUsed;
    }

    private static readonly AccessTools.FieldRef<SystemRenderShadowMap, ShadowBox> ShadowBoxRef =
        AccessTools.FieldRefAccess<SystemRenderShadowMap, ShadowBox>("shadowBox");

    /// <summary>
    /// Runs after the far cascade has built its box, so ShadowBox.SHADOW_DISTANCE holds what
    /// the map really covers. The chunk shaders read shUniforms later in the frame.
    /// </summary>
    public static void MatchFadeToBox(SystemRenderShadowMap __instance, ClientMain ___game)
    {
        var distance = ShadowBox.SHADOW_DISTANCE;
        ShadowDistance = distance;

        // The far cascade has just rendered and the near one has not run yet, so right here -
        // and only here - the box fields are guaranteed to hold the FAR box, whichever shape
        // built it. Width and Height are the two axes the map's texels are spread over.
        try
        {
            var box = ShadowBoxRef(__instance);
            var span = box == null ? 0 : Math.Max(box.Width, box.Height);
            if (span > 0 && !double.IsNaN(span)) ShadowBoxSpan = span;
        }
        catch (Exception) { /* a missing span only costs a HUD row */ }

        // Off: vanilla already wrote its own ShadowRangeFar earlier in this very method, so
        // simply not overwriting it is exactly the vanilla behaviour.
        if (!FadeFix) return;
        if (___game?.shUniforms == null || distance <= 0) return;

        // the shader's distance term reaches full fade at 0.9 * range, so range = distance
        // puts the end of the fade just inside the edge of the map
        var range = (float)distance;
        ___game.shUniforms.ShadowRangeFar = range;
        ShadowRangeUniform = range;
    }
}
