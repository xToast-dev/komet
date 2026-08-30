using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Patches;

/// <summary>
/// ClientMain calls Platform.CheckGlErrorAlways() twice per frame - after final composition
/// and after the blit. Each one is a glGetError, which with mesa_glthread forces the render
/// thread to drain and synchronise with the GL worker thread instead of running ahead.
///
/// Opt-in only: that same call is how the engine turns GL_OUT_OF_MEMORY into a readable
/// "reduce your view distance" message instead of an opaque driver crash.
///
/// The patch is always applied but gated on <see cref="SkipEnabled"/>, so '.komet toggle
/// glerror' can A/B the two driver syncs live: watch the stage times and the swap share of
/// "ausserhalb" move while the picture stays identical.
/// </summary>
public static class GlErrorPatches
{
    /// <summary>When false (the default), the original glGetError runs - exact vanilla.</summary>
    public static bool SkipEnabled;

    public static void Apply(Harmony harmony)
    {
        // The call site is virtual, so the abstract declaration on ClientPlatformAbstract is
        // not what actually runs - patch the concrete override.
        MethodInfo target = AccessTools.Method(
            typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.CheckGlErrorAlways), [typeof(string)]);

        if (target == null || target.IsAbstract)
            throw new InvalidOperationException("ClientPlatformWindows.CheckGlErrorAlways(string) not found");

        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(GlErrorPatches), nameof(Skip))));
    }

    public static bool Skip() => !SkipEnabled;
}
