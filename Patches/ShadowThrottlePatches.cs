using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Re-renders the far shadow cascade only when it would actually look different.
///
/// The shadow stages draw the terrain a second and third time from the light's point of view.
/// At view distance 1536 that measured 8.4 ms (far) plus 7.2 ms (near) out of a 36.5 ms frame -
/// 43 % of the frame on shadows alone, and the far cascade is the half that changes slowly.
///
/// The skip happens at the render *stage* level rather than inside SystemRenderShadowMap.
/// That matters: OnRenderShadowFar pushes matrices that OnRenderShadowFarDone pops, and other
/// renderers (terrain, entities) are registered on those same stages. Skipping the whole
/// stage pair leaves the push/pop balanced, keeps every renderer out, and leaves the shadow
/// framebuffer holding the previous frame's result - which is still valid, just slightly old.
///
/// The one thing a skipped frame must NOT keep unchanged is the sampling matrix. The chunk
/// shaders work in camera-relative coordinates (truePos = vertexPosition + origin, with
/// origin = poolOrigin - cameraPos), and toShadowMapSpaceMatrix* was built for the camera
/// position of the frame that rendered the map. Move the camera and keep the matrix, and the
/// same world point lands on a different texel: the shadows swim with the camera for a frame
/// and snap back on the next re-render - visible as flicker while flying. So every skipped
/// frame rewrites the retained matrix as M * T(delta), delta being how far the camera has
/// moved since the map was rendered; that resolves to the exact texel the point had before,
/// and the shadows genuinely stay put. Always computed from the render-time snapshot, never
/// incrementally, so there is nothing to drift.
///
/// What decides a re-render:
///   * immediately once the camera has moved <see cref="MoveThreshold"/> blocks, whatever the
///     interval says - a retained map covers only the volume it was drawn for, and flying out
///     of that volume shows a hard cut-off line,
///   * otherwise never more often than <see cref="FarInterval"/> frames - the floor on cost,
///   * and never less often than <see cref="FarMaxSkip"/> frames - the cap on staleness.
///
/// The movement rule is what a fixed interval cannot do: standing still, the far shadow map
/// is bit-for-bit what it would have been, so skipping is free; flying, the camera outruns the
/// map in two frames and it updates at the floor rate. When the near cascade is throttled too
/// it yields any frame the far cascade is already drawing and takes the next one, so a frame
/// carries at most one cascade - two of them landing in the same frame is what turns
/// throttling into visible judder even when the average cost is unchanged.
/// </summary>
public static class ShadowThrottlePatches
{
    /// <summary>Minimum frames between two far-cascade renders. 1 = every frame (vanilla).</summary>
    public static int FarInterval = 1;

    /// <summary>Maximum frames the far cascade may go without a re-render.</summary>
    public static int FarMaxSkip = 1;

    /// <summary>
    /// Camera movement, in blocks, that forces a re-render - overriding even FarInterval.
    /// Small on purpose: at 85 fps this is about three frames of walking but less than one of
    /// flying, so a walking player still gets the saving and a flying one never outruns the
    /// map's coverage.
    /// </summary>
    public static double MoveThreshold = 0.15;

    /// <summary>
    /// Extra coverage the far cascade was drawn with, in blocks - read straight from
    /// <see cref="ShadowPatches.EffectiveFarBoxMargin"/> rather than copied, so the two can
    /// never disagree. Safemode and '.komet toggle shadowbox' put the vanilla cone back, and a
    /// movement limit that outlived the box it was derived from would be exactly the cut-off
    /// line the margin exists to prevent.
    /// </summary>
    public static double CoverageMargin => ShadowPatches.EffectiveFarBoxMargin;

    /// <summary>
    /// How much of the margin is actually spent before a redraw. The containment is exact up
    /// to 0.94 x margin (the shader's UV safe band is 94 % of the box), so 0.9 keeps a band
    /// that no rounding, no mid-frame camera step and no odd field of view can eat into.
    /// </summary>
    internal const double MarginSafety = 0.9;

    /// <summary>
    /// The distance the camera may drift from the retained map before it must be redrawn.
    /// Pure, because it is the one number that decides whether the map still covers what the
    /// shader will sample from it - and the whole saving hangs off it being right.
    /// </summary>
    internal static double MoveLimitFor(double threshold, double margin)
        => margin > 0 ? Math.Max(threshold, margin * MarginSafety) : threshold;

    /// <summary>The limit in effect, for the report and the HUD.</summary>
    public static double MoveLimit => MoveLimitFor(MoveThreshold, CoverageMargin);

    /// <summary>Interval for the near cascade. 1 = every frame.</summary>
    public static int NearInterval = 1;

    /// <summary>Frames actually rendered into the far cascade, and frames saved, for the HUD.</summary>
    public static long FarRendered;
    public static long FarSkipped;

    private static long frameCounter;
    /// <summary>-1, not long.MinValue: "frames since" would then overflow on the very first
    /// frame and come out negative, and the whole rule would rest on that accident.</summary>
    private static long lastFarFrame = -1;

    /// <summary>Starts at zero, not at "infinitely overdue" - otherwise the two cascades both
    /// fire on the very first frame and stay in lockstep from there.</summary>
    private static long lastNearFrame;

    /// <summary>Camera and light the retained shadow map was built for.</summary>
    private static double refX, refY, refZ;
    private static float refLx, refLy, refLz;
    private static bool haveReference;

    // The sampling matrices and camera positions of the frames that actually rendered each
    // cascade - the base every skipped frame's compensation is computed from.
    private static readonly float[] FarMatrixSnap = new float[16];
    private static double farCamX, farCamY, farCamZ;
    private static bool haveFarSnap;
    private static readonly float[] NearMatrixSnap = new float[16];
    private static double nearCamX, nearCamY, nearCamZ;
    private static bool haveNearSnap;

    private static readonly AccessTools.FieldRef<ClientMain, float[]> FarMatrixRef =
        AccessTools.FieldRefAccess<ClientMain, float[]>("toShadowMapSpaceMatrixFar");
    private static readonly AccessTools.FieldRef<ClientMain, float[]> NearMatrixRef =
        AccessTools.FieldRefAccess<ClientMain, float[]>("toShadowMapSpaceMatrixNear");

    /// <summary>
    /// Latched so ShadowFarDone always gives the same answer as ShadowFar. Recomputing would
    /// let the two disagree the moment the decision depends on anything but the frame number,
    /// and a Done stage without its opening stage pops a matrix nobody pushed.
    /// </summary>
    private static bool renderFar = true;
    private static bool renderNear = true;

    /// <summary>Whether the far cascade was drawn in the current frame - the GPU stage timer
    /// reads it at the frame's end, so its "far" figure can be told per drawn frame and not
    /// only as an average that the skipped frames dilute.</summary>
    public static bool FarDrawnThisFrame => renderFar;

    /// <summary>Cosine of the light rotation that counts as "the sun has moved" (~0.1 degrees).</summary>
    private const float LightEpsilon = 0.9999985f;

    /// <summary>Live control, shared by Apply and the runtime toggle. 1/1/1 is exactly vanilla.</summary>
    public static void SetIntervals(int farInterval, int nearInterval, int farMaxSkip)
    {
        FarInterval = Math.Max(1, farInterval);
        NearInterval = Math.Max(1, nearInterval);
        FarMaxSkip = Math.Max(FarInterval, farMaxSkip);
    }

    /// <summary>
    /// Forgets the retained map's reference, so the next frame redraws the far cascade.
    /// Needed whenever the box GEOMETRY changes under a retained map - switching the margin
    /// on makes the map that is currently held too small for the new movement limit.
    /// </summary>
    public static void Invalidate()
    {
        haveReference = false;
        haveFarSnap = false;
        lastFarFrame = -1;
    }

    /// <summary>Whether any skipping can currently happen - the toggle's state line.</summary>
    public static bool Throttling => FarInterval > 1 || FarMaxSkip > 1 || NearInterval > 1;

    public static void Apply(Harmony harmony, int farInterval, int nearInterval, int farMaxSkip, double moveThreshold)
    {
        SetIntervals(farInterval, nearInterval, farMaxSkip);
        MoveThreshold = Math.Max(0.0, moveThreshold);

        // Applied even at 1/1/1 (which decides "render" for every frame, exactly vanilla):
        // '.komet toggle shadowthrottle' has to be able to switch throttling on mid-session,
        // and a patch that was never applied cannot be enabled by a field write.
        var stage = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage),
                        [typeof(EnumRenderStage), typeof(float)])
                    ?? throw new InvalidOperationException("ClientMain.TriggerRenderStage not found");

        // Priority.Last so the measurement prefix still gets to start its clock; a prefix
        // returning false only skips the original method, not the other patches. The postfix
        // snapshots the freshly built sampling matrix right after a cascade has rendered.
        harmony.Patch(stage,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(ShadowThrottlePatches), nameof(SkipStage))) { priority = Priority.Last },
            postfix: new HarmonyMethod(
                AccessTools.Method(typeof(ShadowThrottlePatches), nameof(SnapshotStage))));
    }

    public static bool SkipStage(ClientMain __instance, EnumRenderStage stage)
    {
        switch (stage)
        {
            case EnumRenderStage.Before:
                frameCounter++;
                return true;

            case EnumRenderStage.ShadowFar:
                renderFar = DecideFar(__instance);
                if (renderFar)
                {
                    FarRendered++;
                    lastFarFrame = frameCounter;
                    Remember(__instance);
                }
                else
                {
                    FarSkipped++;
                    Compensate(__instance, far: true);
                }
                return renderFar;

            case EnumRenderStage.ShadowFarDone:
                return renderFar;

            case EnumRenderStage.ShadowNear:
                renderNear = DecideNear();
                if (renderNear) lastNearFrame = frameCounter;
                else Compensate(__instance, far: false);
                return renderNear;

            case EnumRenderStage.ShadowNearDone:
                return renderNear;

            default:
                return true;
        }
    }

    /// <summary>
    /// Runs after a render stage completed. When a cascade has just been redrawn, its
    /// sampling matrix and the camera position it was built for are copied aside - the base
    /// for compensating every skipped frame until the next redraw.
    /// </summary>
    public static void SnapshotStage(ClientMain __instance, EnumRenderStage stage)
    {
        if (__instance == null) return;

        if (stage == EnumRenderStage.ShadowFar && renderFar)
        {
            var cam = __instance.EntityPlayer?.CameraPos;
            var m = FarMatrixRef(__instance);
            if (cam == null || m == null || m.Length < 16) { haveFarSnap = false; return; }
            Array.Copy(m, FarMatrixSnap, 16);
            farCamX = cam.X; farCamY = cam.Y; farCamZ = cam.Z;
            haveFarSnap = true;
        }
        else if (stage == EnumRenderStage.ShadowNear && renderNear)
        {
            var cam = __instance.EntityPlayer?.CameraPos;
            var m = NearMatrixRef(__instance);
            if (cam == null || m == null || m.Length < 16) { haveNearSnap = false; return; }
            Array.Copy(m, NearMatrixSnap, 16);
            nearCamX = cam.X; nearCamY = cam.Y; nearCamZ = cam.Z;
            haveNearSnap = true;
        }
    }

    /// <summary>
    /// Rewrites the retained sampling matrix for the camera's current position, so a skipped
    /// frame samples the exact texels the rendered frame would have. Written into the game's
    /// own array - shUniforms holds the same reference, so the shaders pick it up as is.
    /// </summary>
    private static void Compensate(ClientMain game, bool far)
    {
        if (game == null) return;
        var cam = game.EntityPlayer?.CameraPos;
        if (cam == null) return;

        if (far)
        {
            if (!haveFarSnap) return;
            var target = FarMatrixRef(game);
            if (target == null || target.Length < 16) return;
            OffsetShadowMatrix(FarMatrixSnap, target, cam.X - farCamX, cam.Y - farCamY, cam.Z - farCamZ);
        }
        else
        {
            if (!haveNearSnap) return;
            var target = NearMatrixRef(game);
            if (target == null || target.Length < 16) return;
            OffsetShadowMatrix(NearMatrixSnap, target, cam.X - nearCamX, cam.Y - nearCamY, cam.Z - nearCamZ);
        }
    }

    /// <summary>
    /// into = snapshot * T(dx, dy, dz), column major.
    ///
    /// The shadow map holds world content in coordinates relative to the camera position it
    /// was rendered at; a vertex now arrives relative to the current camera, which moved by
    /// delta. M * (p + delta) restores the render-time coordinates, and for a homogeneous
    /// point (w = 1) that is the original matrix with M_lin * delta folded into the
    /// translation column. Rotation of the camera does not enter anywhere: the light-space
    /// transform never depended on it.
    /// </summary>
    internal static void OffsetShadowMatrix(float[] snap, float[] into, double dx, double dy, double dz)
    {
        for (var i = 0; i < 12; i++) into[i] = snap[i];
        into[12] = (float)(snap[12] + snap[0] * dx + snap[4] * dy + snap[8] * dz);
        into[13] = (float)(snap[13] + snap[1] * dx + snap[5] * dy + snap[9] * dz);
        into[14] = (float)(snap[14] + snap[2] * dx + snap[6] * dy + snap[10] * dz);
        into[15] = (float)(snap[15] + snap[3] * dx + snap[7] * dy + snap[11] * dz);
    }

    /// <summary>
    /// The near cascade keeps its own interval, but yields the frame whenever the far cascade
    /// is drawing and takes the next one instead. That is the whole anti-judder rule: the far
    /// cascade decides adaptively and cannot be scheduled against, so the near one gives way.
    /// A frame that carries one cascade every time beats frames that alternate between two
    /// and none, even though the average is identical.
    /// </summary>
    private static bool DecideNear()
    {
        if (NearInterval <= 1) return true;

        var since = frameCounter - lastNearFrame;
        if (since <= 0) return renderNear;
        if (since < NearInterval) return false;
        if (renderFar && since <= NearInterval) return false;
        return true;
    }

    /// <summary>
    /// Test seam for the rule itself: the camera drift and staleness that would be read from a
    /// live client, without needing one.
    /// </summary>
    internal static bool WouldRenderFar(double driftBlocks, long framesSince,
                                        int farInterval, int farMaxSkip, double moveThreshold)
    {
        if (framesSince <= 0) return true;
        if (driftBlocks >= moveThreshold) return true;
        if (framesSince < farInterval) return false;
        return framesSince >= farMaxSkip;
    }

    private static bool DecideFar(ClientMain game)
    {
        var since = frameCounter - lastFarFrame;

        // The same frame asking twice (or a frame counter that has not advanced) must not
        // flip the answer half way through a stage pair.
        if (since <= 0) return renderFar;

        // How far the camera has drifted from the map we are still showing. This has to be
        // asked FIRST, before the interval floor: a floor applied while the camera is moving
        // is what put a visible cut-off line on screen when flying up and down.
        //
        // Compensating the sampling matrix keeps a retained map correctly *positioned*, but it
        // cannot extend what the map *covers* - the depth texture only holds the volume that
        // was rendered into it. Fly out of that volume and the sample coordinates leave
        // [0, 1], where shadowcoords.vsh's edge terms (multiplied by ten) cut the shadow off
        // over a couple of metres instead of fading it. That edge then jumps every time the
        // cascade is finally redrawn.
        var read = TryRead(game, out var x, out var y, out var z, out var light);
        var known = haveReference && read;

        if (known)
        {
            // MoveLimit, not MoveThreshold: a far cascade drawn with a coverage margin still
            // covers what the shader samples until the camera has spent that margin, and
            // spending it is the entire point of drawing the margin.
            var limit = MoveLimit;
            double dx = x - refX, dy = y - refY, dz = z - refZ;
            if (dx * dx + dy * dy + dz * dz >= limit * limit) return true;
        }

        if (since < FarInterval) return false;
        if (since >= FarMaxSkip) return true;

        // No camera or no calendar means no way to tell whether anything moved. Falling back
        // to "render" degrades to the plain fixed-interval throttle rather than to a shadow
        // map that is stale for reasons nobody can see.
        if (!known) return true;

        // Light direction: the shadow projection is a LookAt along this vector, so a rotation
        // invalidates the map even when the camera has not moved at all.
        var len = MathF.Sqrt(light.X * light.X + light.Y * light.Y + light.Z * light.Z);
        if (len <= 1e-6f) return true;
        float nx = light.X / len, ny = light.Y / len, nz = light.Z / len;
        return nx * refLx + ny * refLy + nz * refLz < LightEpsilon;
    }

    private static void Remember(ClientMain game)
    {
        if (!TryRead(game, out var x, out var y, out var z, out var light))
        {
            haveReference = false;
            return;
        }

        refX = x; refY = y; refZ = z;

        var len = MathF.Sqrt(light.X * light.X + light.Y * light.Y + light.Z * light.Z);
        if (len <= 1e-6f) { haveReference = false; return; }
        refLx = light.X / len; refLy = light.Y / len; refLz = light.Z / len;
        haveReference = true;
    }

    /// <summary>
    /// Camera position and light direction exactly as PrepareForShadowRendering would take
    /// them. Anything missing means "re-render", never "guess".
    /// </summary>
    private static bool TryRead(ClientMain game, out double x, out double y, out double z, out Vec3f light)
    {
        x = y = z = 0;
        light = null;
        if (game == null) return false;

        var cam = game.EntityPlayer?.CameraPos;
        var cal = game.Calendar;
        if (cam == null || cal == null) return false;

        light = cal.MoonLightStrength > cal.SunLightStrength ? cal.MoonPosition : cal.SunPosition;
        if (light == null) return false;

        x = cam.X; y = cam.Y; z = cam.Z;
        return true;
    }

    /// <summary>Test seam: lets the harness drive the decision without a live ClientMain.</summary>
    internal static void ResetForTests()
    {
        frameCounter = 0;
        lastFarFrame = -1;
        lastNearFrame = 0;
        haveReference = false;
        haveFarSnap = haveNearSnap = false;
        renderFar = true;
        renderNear = true;
        FarRendered = FarSkipped = 0;
    }
}
