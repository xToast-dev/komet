using System;
using HarmonyLib;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Stops the sun's occlusion query from syncing with the GL driver thread every frame.
///
/// SystemRenderSunMoon registers twice in the opaque stage. The second registration, at render
/// order 999 so it runs last, draws the sun quad with <c>glColorMask(false, false, false,
/// false)</c> - it writes no pixels at all. Its entire purpose is an occlusion query that
/// measures how much of the sun is covered, which becomes SunSpecularIntensity, the glare.
///
/// The cost is not the quad. It is <c>glGetQueryObject</c>, called twice per frame: with
/// mesa_glthread enabled - the default for radeonsi - every GL call that returns a value has to
/// flush the command queue and wait for the driver thread, exactly like the per-frame
/// glGetError this mod already offers to skip. Measured here at **1.86 ms of an 11.9 ms
/// frame**, for two textured quads.
///
/// So the query runs every Nth frame instead. Two things make that free rather than a trade:
/// the pass writes no colour, so skipping it cannot change a pixel; and the result is fed
/// through <c>SunSpecularIntensity + (target - current) * dt * 20</c>, a time-based smoothing
/// that already lags it by about 50 ms. Begin and End are paired inside the one call, so a
/// skipped frame leaves nothing half-open - it just gives the query longer to finish, which
/// makes the availability check *more* likely to return immediately.
///
/// What a skipped frame must NOT skip is the pass's GL state. This runs at render order 999,
/// dead last in the opaque stage, and the OIT stage - sky, clouds, transparency - begins right
/// after it with no state reset in between. Vanilla therefore always enters OIT with the state
/// this pass leaves behind: depth test on, blend on, cull face off, depth mask and colour mask
/// restored. The first version of this throttle skipped the whole method, so on three frames
/// out of four OIT inherited whatever the second-to-last opaque renderer left instead - and the
/// sky flickered on a four-frame beat. A hidden contract, honoured explicitly now: a skipped
/// frame still sets those end states, four cheap state calls with no return values and
/// therefore no driver sync.
/// </summary>
public static class SunQueryPatches
{
    /// <summary>Run the query every Nth frame. 1 restores vanilla.</summary>
    public static int Interval = 4;

    public static long StatSkipped;

    private static long frames;

    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> GameRef =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");

    public static void Apply(Harmony harmony, int interval)
    {
        Interval = Math.Max(1, interval);
        if (Interval == 1) return;

        if (GameRef == null) throw new InvalidOperationException("ClientSystem.game not found");

        var post = AccessTools.Method(typeof(SystemRenderSunMoon), "OnRenderFrame3DPost",
                       new[] { typeof(float) })
                   ?? throw new InvalidOperationException("SystemRenderSunMoon.OnRenderFrame3DPost not found");

        harmony.Patch(post, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(SunQueryPatches), nameof(ThrottleQuery))));
    }

    /// <summary>Returns false to skip the pass on frames that are not sampled.</summary>
    public static bool ThrottleQuery(SystemRenderSunMoon __instance)
    {
        if (Interval <= 1) return true;
        if (frames++ % Interval == 0) return true;

        // No platform means no way to leave the stage in the state the OIT pass inherits from
        // this method - then the only safe answer is to not skip at all.
        var platform = __instance == null ? null : GameRef(__instance)?.Platform;
        if (platform == null) return true;

        // The end states of the real pass, in its order. Everything the pass sets in between
        // (depth mask false, colour mask false) it restores itself before returning, so these
        // four are the complete visible contract.
        platform.GlEnableDepthTest();
        platform.GlToggleBlend(on: true);
        platform.GlDisableCullFace();
        platform.GlDepthMask(flag: true);

        StatSkipped++;
        return false;
    }

    internal static void ResetForTests()
    {
        frames = 0;
        StatSkipped = 0;
    }
}
