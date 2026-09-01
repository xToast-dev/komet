using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Enlarges the two shadow map framebuffers beyond what the graphics menu offers.
///
/// The engine sizes them as <c>Math.Max(4, ShadowMapQuality + 2) * 1024</c>, and the settings
/// slider stops at quality 4 - so 6144 squared is the ceiling the UI can reach. That ceiling is
/// what limits shadow sharpness once the shadow box covers more ground (our symmetric box and
/// any distance multiplier both spread the same texels over more world), and a GPU sitting at
/// a fraction of its capacity is exactly the place to spend on it: a depth-only pass costs
/// bandwidth and video memory, not the geometry work that dominates the frame.
///
/// Raising the *setting* instead would be wrong: ClientSettings.ShadowMapQuality also feeds
/// the shader define SHADOWQUALITY, the near cascade's distance (30 + 3*(q-1)) and the far
/// cascade's range - all tuned for the value the user picked. This patch changes the
/// framebuffer size and nothing else. Everything downstream follows automatically, both
/// verified in the engine source: ClearFrameBuffer sets the shadow viewport from
/// frameBuffer.Width/Height, and ShaderProgramBase feeds shadowMapWidthInv/HeightInv from the
/// same field, so the PCF sample offsets scale themselves.
///
/// Cost, at 4 bytes per depth texel and two cascades: 6144 -> 288 MB, 7168 -> 411 MB,
/// 8192 -> 537 MB of video memory. Two steps is the default since 1.42.0, because the symmetric
/// box's cost was finally measured rather than estimated: 1,48-1,59x wider per axis at a normal
/// sun elevation, which one step (1,17x) did not cover and two (1,33x) very nearly do.
/// </summary>
public static class ShadowResPatches
{
    /// <summary>Quality steps added on top of the setting, for the framebuffer size only.
    /// Each step is 1024 texels per axis. 0 = exactly vanilla.</summary>
    public static int ExtraSteps;

    /// <summary>Edge length the shadow maps ended up with, 0 until the patched setup has run.</summary>
    public static int ShadowMapSize { get; private set; }

    /// <summary>
    /// The edge length actually in use, patched or not. One definition, because two places need
    /// it and they must not disagree: the HUD's texels-per-block row, and the texel snapping -
    /// which quantises to a grid that does not exist if it guesses the size wrong.
    /// </summary>
    public static int EffectiveMapSize
        => ShadowMapSize > 0 ? ShadowMapSize : Math.Max(4, ClientSettings.ShadowMapQuality + 2) * 1024;

    public static void Apply(Harmony harmony, int extraSteps)
    {
        ExtraSteps = Math.Clamp(extraSteps, 0, 4);
        if (ExtraSteps == 0) return;

        var setup = AccessTools.Method(typeof(ClientPlatformWindows), "SetupDefaultFrameBuffers")
                    ?? throw new InvalidOperationException("SetupDefaultFrameBuffers not found");

        harmony.Patch(setup, transpiler: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowResPatches), nameof(EnlargeShadowMaps))));
    }

    /// <summary>
    /// The two shadow map sizes are the only <c>Math.Max(...) * 1024</c> expressions in the
    /// method; a call to <see cref="AddSteps"/> is spliced between the Max and the multiply.
    /// Anything else - a different count, a changed expression - throws rather than silently
    /// resizing the wrong buffer.
    /// </summary>
    public static IEnumerable<CodeInstruction> EnlargeShadowMaps(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var addSteps = AccessTools.Method(typeof(ShadowResPatches), nameof(AddSteps));
        var patched = 0;

        for (var i = 0; i < code.Count - 1; i++)
        {
            var isMax = code[i].operand is MethodInfo m && m.Name == "Max" && m.DeclaringType == typeof(Math);
            if (!isMax) continue;
            // the size expression is Max(...) * 1024; the multiply follows the constant
            if (!code[i + 1].LoadsConstant(1024) || i + 2 >= code.Count || code[i + 2].opcode != OpCodes.Mul)
                continue;

            code.Insert(i + 1, new CodeInstruction(OpCodes.Call, addSteps));
            patched++;
            i += 2;
        }

        if (patched != 2)
            throw new InvalidOperationException(
                $"expected exactly two shadow map size expressions in SetupDefaultFrameBuffers, patched {patched}");

        return code;
    }

    /// <summary>Called with the engine's quality steps, returns the steps to allocate for.</summary>
    public static int AddSteps(int steps)
    {
        var result = steps + ExtraSteps;
        ShadowMapSize = result * 1024;
        return result;
    }

    /// <summary>
    /// Forces the enlarged shadow framebuffers into existence on a NORMAL game launch.
    ///
    /// The transpiler above patches SetupDefaultFrameBuffers - but that method runs when the
    /// game window is created, BEFORE any mod loads. On a plain start the shadow maps were
    /// therefore always vanilla-sized, and the patch only ever took effect if the user later
    /// changed a graphics setting or toggled fullscreen (both call RebuildFrameBuffers). The
    /// user's HUD caught it: "schattenmap 6144px" with ShadowMapExtraQuality = 1 configured -
    /// the extra resolution this mod has been claiming since 1.19 had never once been applied
    /// on a normal launch.
    ///
    /// So: rebuild once, the way the engine itself does after a settings change. Retried,
    /// because ShaderRegistry.SupressShaderAndBufferReloads makes RebuildFrameBuffers a silent
    /// no-op during startup; ShadowMapSize > 0 is the proof the transpiler actually ran.
    /// Returns true when done (success or permanent failure), false to be called again.
    /// </summary>
    public static bool TryForceRebuild(ClientPlatformWindows platform, Action<string> log)
    {
        if (ExtraSteps == 0 || ShadowMapSize > 0) return true;   // nothing to do / already real
        if (ShaderRegistry.SupressShaderAndBufferReloads) return false; // engine busy - retry

        // Never rebuild into a window that cannot host framebuffers. This shipped a crash
        // (31.08.2026, Windows tester): alt-tab out of fullscreen minimises the window,
        // SetupDefaultFrameBuffers then hits its degenerate-size bailout and returns a list
        // of NULL entries, RebuildFrameBuffers adopts it and disposes the good buffers, and
        // the next frame dies in ClearFrameBuffer(LiquidDepth) inside vanilla code. The
        // engine itself never rebuilds while minimised (Window_Resize checks the state);
        // this forced rebuild has to hold itself to the same rule.
        NativeWindow win = platform.window;
        if (win == null) return false;
        if (!CanHostFramebuffers(win.WindowState == WindowState.Minimized,
                win.ClientSize.X, win.ClientSize.Y, ClientSettings.SSAA))
            return false; // alt-tabbed / degenerate window - retry when it is visible again

        try
        {
            platform.RebuildFrameBuffers();
        }
        catch (Exception e)
        {
            log?.Invoke($"could not rebuild framebuffers for the larger shadow maps ({e.GetType().Name}) - shadow map stays vanilla-sized");
            return true;
        }

        if (ShadowMapSize > 0)
        {
            log?.Invoke($"shadow map framebuffers rebuilt at {ShadowMapSize}px (vanilla setup ran before the mod loaded)");
            return true;
        }
        return false; // suppressed after all, or setup took another path - retry
    }

    /// <summary>
    /// The exact predicate behind SetupDefaultFrameBuffers' degenerate-size bailout, plus
    /// the engine's own never-while-minimised rule: the method computes
    /// (int)(clientSize * ssaa) per axis and returns the all-null list when either is 0 -
    /// so a 1x1 window at SSAA 0.5 is just as fatal as a 0x0 one.
    /// </summary>
    internal static bool CanHostFramebuffers(bool minimized, int width, int height, float ssaa)
        => !minimized && (int)(width * ssaa) > 0 && (int)(height * ssaa) > 0;
}
