using System;
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
/// Draws into the near shadow map only what can shadow something the camera can see.
///
/// The near cascade's box is built around a wedge that does not follow the view - ShadowBox.
/// getCameraRotationMatrix returns the identity, so it is a fixed world-axis shape around the
/// player - and the map it fills serves receivers in every direction: the ground behind the
/// camera is shadowed just as carefully as the ground in front, and nobody ever samples it,
/// because it is not on screen. Every caster whose shadow lands only on such ground is drawn
/// for nothing, and in a forest that is most of the casters behind you and beside you.
///
/// What CAN reach a visible receiver is easy to say exactly. The near map serves a receiver
/// only within 1.15 x the cascade's range of the camera (shadowcoords.vsh, see
/// <see cref="ShadowDepthPatches.FadeReach"/>), and only receivers inside the view frustum are
/// drawn at all - so the receivers that matter lie in the frustum slice out to that distance,
/// clipped to the ball of that radius. A caster shades a receiver only along the light
/// direction, which in light space is the z axis. So a caster matters only if its light-space
/// (x, y) is one some receiver in that region also has - and the shadow projection's four
/// lateral clip planes are exactly planes of constant light-space x and y. Tightening them to
/// the receiver region's extent keeps every caster that can reach a visible receiver and drops
/// every one that cannot.
///
/// The tightening is done on the planes the engine itself built. PrepareForShadowRendering
/// hands CalcFrustumEquations the ortho projection and a look-at along the light, and the six
/// planes that come out are the clip volume's faces in world space; the four whose normals are
/// perpendicular to the light are the lateral ones (identified by that property, not by index).
/// For each, the receiver region's signed distance is bounded below by the minimum over the
/// slice's five corners (the region is convex, the distance is linear) and separately by the
/// ball's centre minus its radius; the larger bound is still a bound, and the plane moves in by
/// that much less a pad. The ranges the culler pre-filters on are untouched (they are looser
/// than the planes anyway), and vanilla's InFrustumShadowPass and <see cref="FastCuller"/> read
/// the same plane fields, so both paths follow and the cull verifier stays valid.
///
/// What does NOT change: the map's coverage. The projection is the same, the texel grid is the
/// same, the lookup is the same - the map simply holds "no caster" where nothing visible could
/// have read a caster anyway. The near map is redrawn every frame, so a turn of the camera is
/// served by the very next frame; the receiver region is padded by a few degrees plus twice
/// the last frame's turn, so the frame the camera is turning IN is covered as well. Where the
/// near cascade is retained across frames instead ('.komet shadownearskip'), a retained map
/// cut to one view would be wrong for the next, so the cull steps aside there.
///
/// FAR CASCADE: untouched, deliberately. Its map IS retained across frames and reprojected
/// (ShadowThrottlePatches), and a rotation is free for it precisely because it covers every
/// direction. This trades that property for geometry on the one cascade that redraws anyway.
/// </summary>
public static class ShadowFootprintPatches
{
    /// <summary>Off is exactly vanilla: the planes are left as CalcFrustumEquations built them.</summary>
    public static bool Enabled;

    /// <summary>What komet.json asked for, so safemode has something to come back to.</summary>
    public static bool ConfiguredEnabled;

    public static bool Installed { get; private set; }

    /// <summary>Angular slack on the receiver slice, degrees. Covers the third-person eye
    /// sitting a few blocks off the render origin and the frame's own turn.</summary>
    internal const double PadDegrees = 8.0;

    /// <summary>Blocks left between the receiver region and a tightened plane, on top of the
    /// angular pad - the 3x3 PCF reads a texel either side, and the range test rounds.</summary>
    internal const double PadBlocks = 4.0;

    /// <summary>A plane counts as lateral when its normal is this close to perpendicular to
    /// the light. The ortho's lateral normals are exactly perpendicular; this is float slack.</summary>
    internal const double LateralDot = 0.02;

    /// <summary>The area of the tightened footprint over the box's, smoothed - what the report
    /// prints. 1.0 means nothing was cut this frame.</summary>
    public static double FootprintFraction { get; private set; } = 1.0;

    /// <summary>Frames in which at least one plane moved.</summary>
    public static long StatTightened;

    /// <summary>Frames the cull stepped aside because the near map is retained.</summary>
    public static long StatYielded;

    private static readonly AccessTools.FieldRef<FrustumCulling, Plane[]> FrustumRef =
        AccessTools.FieldRefAccess<FrustumCulling, Plane[]>("frustum");

    // last frame's view direction, for the turn-rate pad
    private static double lastFx, lastFy, lastFz;
    private static bool haveLast;

    private static readonly double[] cornerBuf = new double[15];
    private static readonly double[] sunBuf = new double[3];
    private static readonly double[] camBuf = new double[3];

    public static void Apply(Harmony harmony, bool enabled)
    {
        if (FrustumRef == null) throw new InvalidOperationException("FrustumCulling.frustum not found");

        var prepare = AccessTools.Method(typeof(SystemRenderShadowMap), "PrepareForShadowRendering",
                          [typeof(double), typeof(EnumFrameBuffer), typeof(float)])
                      ?? throw new InvalidOperationException("PrepareForShadowRendering not found");
        // A postfix, after CalcFrustumEquations inside the body has built the planes. Harmony
        // runs it alongside ShadowPatches.PadCullRange, which touches the ranges, not the planes.
        harmony.Patch(prepare, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowFootprintPatches), nameof(CullToVisibleReceivers))));

        Installed = true;
        ConfiguredEnabled = enabled;
        Enabled = enabled;
    }

    /// <summary>
    /// The five corners of the frustum slice, world space: the apex at the camera and the far
    /// face at <paramref name="reach"/> along the view axis, each half-extent widened by the
    /// angular pad. Pure. <paramref name="view"/> is the camera's view matrix (rows = camera
    /// axes: right, up, back), <paramref name="proj"/> the perspective matrix, from which
    /// [0] = 1/(aspect*tan(fov/2)) and [5] = 1/tan(fov/2) give the half-extents per unit depth.
    /// </summary>
    internal static bool SliceCorners(double[] view, double[] proj, double camX, double camY, double camZ,
                                      double reach, double padDegrees, double[] into)
    {
        if (view == null || proj == null || view.Length < 16 || proj.Length < 16 || into.Length < 15) return false;
        if (!(reach > 0) || !(proj[0] > 0) || !(proj[5] > 0)) return false;

        double rx = view[0], ry = view[4], rz = view[8];    // right
        double ux = view[1], uy = view[5], uz = view[9];    // up
        double fx = -view[2], fy = -view[6], fz = -view[10]; // forward = -back

        // tan of the padded half angles: tan(a + p) from tan(a) and tan(p)
        var tp = Math.Tan(padDegrees * Math.PI / 180.0);
        var th = (1.0 / proj[0] + tp) / (1.0 - tp / proj[0]);
        var tv = (1.0 / proj[5] + tp) / (1.0 - tp / proj[5]);
        if (!(th > 0) || !(tv > 0)) return false;

        var hw = reach * th;
        var hh = reach * tv;

        into[0] = camX; into[1] = camY; into[2] = camZ;
        var i = 3;
        for (var sx = -1; sx <= 1; sx += 2)
            for (var sy = -1; sy <= 1; sy += 2)
            {
                into[i++] = camX + fx * reach + rx * hw * sx + ux * hh * sy;
                into[i++] = camY + fy * reach + ry * hw * sx + uy * hh * sy;
                into[i++] = camZ + fz * reach + rz * hw * sx + uz * hh * sy;
            }
        return true;
    }

    /// <summary>
    /// The rule, pure. Moves every lateral plane in to the receiver region's extent and reports
    /// how many moved. <paramref name="corners"/> holds the slice's five world-space points,
    /// <paramref name="ball"/> is the receiver ball's radius around the camera. Returns the
    /// tightened footprint's area as a fraction of the original, or 1 when nothing moved.
    /// </summary>
    internal static double Tighten(Plane[] planes, double sunX, double sunY, double sunZ,
                                   double camX, double camY, double camZ, double[] corners,
                                   double ball, double padBlocks, out int moved)
    {
        moved = 0;
        if (planes == null || planes.Length < 6 || corners == null || corners.Length < 15) return 1.0;

        // the original and tightened extents per lateral axis pair, for the fraction
        double widthBefore = 1, widthAfter = 1;
        var pairs = 0;

        for (var i = 0; i < 6; i++)
        {
            ref var p = ref planes[i];
            var along = p.normalX * sunX + p.normalY * sunY + p.normalZ * sunZ;
            if (Math.Abs(along) > LateralDot) continue;          // a depth plane - never touched

            // the slice: min over its corners (convex region, linear distance)
            var m = double.MaxValue;
            for (var c = 0; c < 15; c += 3)
            {
                var d = p.normalX * corners[c] + p.normalY * corners[c + 1] + p.normalZ * corners[c + 2] + p.D;
                if (d < m) m = d;
            }
            // the ball: its centre minus the radius. The region is inside both, so the larger
            // of the two lower bounds is still a lower bound.
            var mb = p.normalX * camX + p.normalY * camY + p.normalZ * camZ + p.D - ball;
            if (mb > m) m = mb;

            var cut = m - padBlocks;
            if (!(cut > 0) || double.IsNaN(cut) || double.IsInfinity(cut)) continue;

            // For the fraction: pair this plane with its opposite (normal negated), whose D
            // plus this D is the extent between them.
            for (var j = 0; j < 6; j++)
            {
                if (j == i) continue;
                ref var q = ref planes[j];
                if (Math.Abs(q.normalX + p.normalX) > 1e-6 || Math.Abs(q.normalY + p.normalY) > 1e-6
                    || Math.Abs(q.normalZ + p.normalZ) > 1e-6) continue;
                var before = p.D + q.D;
                if (before > 0)
                {
                    widthBefore *= before;
                    widthAfter *= before - cut;
                    pairs++;
                }
                break;
            }

            p.D -= cut;
            moved++;
        }

        if (moved == 0 || pairs == 0 || !(widthBefore > 0)) return 1.0;
        var fraction = widthAfter / widthBefore;
        return fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
    }

    public static void CullToVisibleReceivers(SystemRenderShadowMap __instance, ClientMain ___game, EnumFrameBuffer fb)
    {
        if (!Enabled || fb != EnumFrameBuffer.ShadowmapNear || ___game == null) return;
        if (ShadowThrottlePatches.NearInterval > 1) { StatYielded++; return; }

        try
        {
            var culler = ___game.frustumCuller;
            var planes = culler == null ? null : FrustumRef(culler);
            var cam = ___game.EntityPlayer?.CameraPos;
            var view = ___game.MainCamera?.CameraMatrixOrigin;
            var proj = ___game.PerspectiveProjectionMat;
            var sun = ___game.Calendar?.SunPositionNormalized;
            if (planes == null || cam == null || view == null || proj == null || sun == null) return;

            // the moon lights the night's shadows: same rule PrepareForShadowRendering uses
            if (___game.Calendar.MoonLightStrength > ___game.Calendar.SunLightStrength)
                sun = ___game.Calendar.MoonPosition;
            var len = Math.Sqrt(sun.X * sun.X + sun.Y * sun.Y + sun.Z * sun.Z);
            if (!(len > 0)) return;
            sunBuf[0] = sun.X / len; sunBuf[1] = sun.Y / len; sunBuf[2] = sun.Z / len;

            // the frame's own turn, so a receiver that comes into view during it is covered
            double fx = -view[2], fy = -view[6], fz = -view[10];
            var pad = PadDegrees;
            if (haveLast)
            {
                var dot = Math.Clamp(fx * lastFx + fy * lastFy + fz * lastFz, -1.0, 1.0);
                pad += 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
            }
            lastFx = fx; lastFy = fy; lastFz = fz; haveLast = true;
            if (pad > 60) pad = 60;

            var reach = ShadowDepthPatches.FadeReach * ShadowBox.SHADOW_DISTANCE;
            camBuf[0] = cam.X; camBuf[1] = cam.Y; camBuf[2] = cam.Z;
            if (!SliceCorners(view, proj, cam.X, cam.Y, cam.Z, reach, pad, cornerBuf)) return;

            var fraction = Tighten(planes, sunBuf[0], sunBuf[1], sunBuf[2],
                                   cam.X, cam.Y, cam.Z, cornerBuf, reach + PadBlocks, PadBlocks, out var moved);
            if (moved > 0)
            {
                StatTightened++;
                // the sweep caches converted planes per frustum generation
                FastCuller.FrustumGeneration++;
            }
            FootprintFraction += (fraction - FootprintFraction) / 16.0;
        }
        catch (Exception)
        {
            // planes that cannot be tightened are the engine's; never a missing shadow
            Enabled = false;
        }
    }

    public static void Reset()
    {
        haveLast = false;
        FootprintFraction = 1.0;
    }
}
