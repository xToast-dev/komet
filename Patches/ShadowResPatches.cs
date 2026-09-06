using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
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
///
/// The steps apply only when the slider sits at its ceiling (quality 4). That is the case the
/// patch exists for - the menu cannot go higher. Below it the player CAN go higher and chose
/// not to, and that choice usually has a reason: a tester on an Intel HD 620 ran at a lower
/// quality and still got a 5120 px map forced on top (and, with shadows off entirely, a
/// pointless rebuild of every framebuffer at world join). Fill rate on an integrated GPU is the
/// one resource this patch spends, so it now spends it only where the player has already
/// spent everything the menu offers.
///
/// Second job since 05.09.: the NEAR cascade gets a map of its own size. The engine allocates
/// both cascades from one expression, so the near map is as large as the far one - 7168 px
/// with one extra step - while it covers only the ~60 x 34 blocks of vanilla's near wedge
/// (39 blocks at quality 4, FoV 70, 16:9). That is well over a hundred texels per block on an
/// axis, against the far cascade's fifteen, and the GPU report priced it: `near 5,8 ms` of an
/// 8,9 ms GPU frame, for a cascade that draws a few dozen chunks. A depth pass costs texels
/// times depth complexity, not chunks, and 7168 squared is 51 million texels to clear, test
/// and write for every frame. <see cref="NearMapSize"/> re-specifies the near depth texture
/// after the engine built it - one TexImage2D on the texture the framebuffer already holds -
/// and the near pass costs (size/7168)^2 of what it did: 4096 is a third, 3072 a fifth.
///
/// What stays the same: the PCF sample spacing. fogandlight.fsh takes shadowMapWidthInv from
/// the FAR framebuffer for both cascades, so a smaller near map is sampled with taps that are
/// less than a near texel apart and its 3x3 kernel spans a little over two near texels instead
/// of three. The penumbra gets slightly crisper - at 4096 px it is about the width vanilla has
/// at quality 2, which uses a 4096 map for a 33-block near range. The texel snapping is told
/// which cascade it quantises for, since the two grids now differ.
/// </summary>
public static class ShadowResPatches
{
    /// <summary>Quality steps added on top of the setting, for the framebuffer size only.
    /// Each step is 1024 texels per axis. 0 = exactly vanilla.</summary>
    public static int ExtraSteps;

    /// <summary>The engine's step count at the graphics menu's ceiling: quality 4 gives
    /// Math.Max(4, 4 + 2) = 6, i.e. 6144 px. Below this the slider can still be raised.</summary>
    internal const int SliderCeilingSteps = 6;

    /// <summary>The rule: engine steps in, steps to allocate out. Pure, for the harness.</summary>
    internal static int StepsFor(int engineSteps, int extraSteps)
        => engineSteps >= SliderCeilingSteps ? engineSteps + extraSteps : engineSteps;

    /// <summary>Whether the extra steps have any effect at this quality setting.</summary>
    public static bool AppliesAt(int shadowMapQuality)
        => Math.Max(4, shadowMapQuality + 2) >= SliderCeilingSteps;

    /// <summary>Edge length the shadow maps ended up with, 0 until the patched setup has run.</summary>
    public static int ShadowMapSize { get; private set; }

    /// <summary>
    /// The edge length actually in use, patched or not. One definition, because two places need
    /// it and they must not disagree: the HUD's texels-per-block row, and the texel snapping -
    /// which quantises to a grid that does not exist if it guesses the size wrong.
    /// </summary>
    public static int EffectiveMapSize
        => ShadowMapSize > 0 ? ShadowMapSize : Math.Max(4, ClientSettings.ShadowMapQuality + 2) * 1024;

    // ---- the near cascade's own map ---------------------------------------------------

    /// <summary>Edge length the near shadow map is re-specified to, in pixels. 0 = leave it at
    /// the far map's size (exactly what the engine does). Live: <see cref="TryResizeNear"/>.</summary>
    public static int NearMapSize;

    /// <summary>Smallest and largest near map this will build. 512 is the floor below which a
    /// 39-block cascade drops under a texel per centimetre-scale block detail; 16384 is a
    /// common GL_MAX_TEXTURE_SIZE.</summary>
    internal const int NearMapMin = 512, NearMapMax = 16384;

    /// <summary>The rule, pure: the engine's size in, the size to allocate out. Rounded to a
    /// multiple of 64 so the projection's texel arithmetic stays tidy, never below the floor,
    /// never above the ceiling, and 0 (or anything below) means "as the engine built it".</summary>
    internal static int NearSizeFor(int engineSize, int configured)
    {
        if (configured <= 0) return engineSize;
        var size = Math.Clamp(configured, NearMapMin, NearMapMax);
        return (size + 32) / 64 * 64;
    }

    /// <summary>Edge length the near map really has after the setup postfix ran, 0 while it
    /// has not (then the near map is the engine's, i.e. the far map's size).</summary>
    public static int NearMapSizeApplied { get; private set; }

    /// <summary>True once the setup postfix has seen a framebuffer list - the proof the resize
    /// had its chance, which the forced rebuild at world join needs to know.</summary>
    public static bool NearSetupRan { get; private set; }

    /// <summary>The near map's real edge length, patched or not. Same contract as
    /// <see cref="EffectiveMapSize"/>, for the near cascade.</summary>
    public static int EffectiveNearMapSize
        => NearMapSizeApplied > 0 ? NearMapSizeApplied : EffectiveMapSize;

    public static void Apply(Harmony harmony, int extraSteps, int nearMapSize = 0)
    {
        ExtraSteps = Math.Clamp(extraSteps, 0, 4);
        NearMapSize = nearMapSize;

        var setup = AccessTools.Method(typeof(ClientPlatformWindows), "SetupDefaultFrameBuffers")
                    ?? throw new InvalidOperationException("SetupDefaultFrameBuffers not found");

        // The near-map postfix goes on whatever the config says: it is gated by NearMapSize at
        // run time, and '.komet shadownear' has to be able to switch a size on in a session
        // that started with 0 - a patch that was never applied cannot be enabled by a field
        // write. The transpiler stays conditional: with no extra step it would rewrite the
        // method to compute exactly what it computes anyway.
        harmony.Patch(setup,
            transpiler: ExtraSteps > 0
                ? new HarmonyMethod(AccessTools.Method(typeof(ShadowResPatches), nameof(EnlargeShadowMaps)))
                : null,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ShadowResPatches), nameof(ResizeNearMap))));
    }

    /// <summary>
    /// Runs after the engine built every framebuffer. The near shadow map (slot 12) is a
    /// depth texture the engine allocated at the far map's size; re-specifying the same
    /// texture object at the configured size keeps the framebuffer attachment (it refers to
    /// the object, not to a size) and costs one TexImage2D. Width and Height on the ref are
    /// what ClearFrameBuffer sets the viewport from, so they follow. The far map (slot 11) is
    /// never touched: the shader takes its PCF spacing from it.
    ///
    /// Completeness is checked once, here at setup - that is one returning GL call per
    /// rebuild, not per frame - and a framebuffer the driver will not accept at the new size
    /// is put back to the engine's before anything renders into it.
    /// </summary>
    public static void ResizeNearMap(List<FrameBufferRef> __result)
    {
        NearSetupRan = true;
        NearMapSizeApplied = 0;
        if (NearMapSize <= 0 || __result == null || __result.Count <= (int)EnumFrameBuffer.ShadowmapNear) return;

        var near = __result[(int)EnumFrameBuffer.ShadowmapNear];
        // no near cascade at quality <= 1, no shadows at 0: nothing to resize
        if (near == null || near.DepthTextureId == 0 || near.Width <= 0) return;

        var target = NearSizeFor(near.Width, NearMapSize);
        if (target == near.Width && target == near.Height)
        {
            NearMapSizeApplied = target;
            return;
        }

        int oldW = near.Width, oldH = near.Height;
        try
        {
            SpecifyDepthTexture(near.DepthTextureId, target, target);
            near.Width = target;
            near.Height = target;

            // The engine's setup leaves this very framebuffer bound (it was the last one it
            // attached to), and its LoadFrameBuffer rebinds unconditionally on every switch,
            // so binding it here for the check changes no state anyone relies on.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, near.FboId);
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                SpecifyDepthTexture(near.DepthTextureId, oldW, oldH);
                near.Width = oldW;
                near.Height = oldH;
                LastNearError = $"framebuffer {status} at {target}px, kept {oldW}px";
                return;
            }
            NearMapSizeApplied = target;
            LastNearError = null;
        }
        catch (Exception e)
        {
            // never let a resize take the shadow pass down: back to the engine's size
            try { SpecifyDepthTexture(near.DepthTextureId, oldW, oldH); } catch (Exception) { /* nothing more to do */ }
            near.Width = oldW;
            near.Height = oldH;
            LastNearError = e.GetType().Name;
        }
    }

    /// <summary>Why the last near-map resize was refused, for the log; null when it worked.</summary>
    public static string LastNearError { get; private set; }

    /// <summary>The engine's own allocation for a shadow depth texture, same format and no
    /// data: GL_DEPTH_COMPONENT32 (33191), GL_DEPTH_COMPONENT (6402), GL_FLOAT (5126). The
    /// sampler parameters (compare mode, clamp, border) live on the texture object and are
    /// untouched by a re-specification.</summary>
    private static void SpecifyDepthTexture(int textureId, int width, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)33191, width, height, 0,
            (PixelFormat)6402, (PixelType)5126, IntPtr.Zero);
    }

    /// <summary>
    /// Changes the near map's size in a running session: sets the target and rebuilds the
    /// framebuffers the way the engine does after a settings change, which runs the setup
    /// postfix. Returns the outcome as a sentence for the chat.
    /// </summary>
    public static string TryResizeNear(ClientPlatformWindows platform, int size)
    {
        NearMapSize = size;
        NearSetupRan = false;
        if (platform == null) return "no platform - not in a world?";
        if (ClientSettings.ShadowMapQuality <= 1)
            return "no near cascade at shadow quality " + ClientSettings.ShadowMapQuality + " (needs 2 or higher) - the size is stored and applies once it exists";

        var done = TryForceRebuild(platform, null, out var blockedBy);
        if (!done) return "cannot rebuild the framebuffers right now (" + (blockedBy ?? "unknown") + ") - the size applies at the next rebuild";
        if (!NearSetupRan) return "rebuild ran but the framebuffer setup was not reached - the size applies at the next rebuild";
        if (LastNearError != null) return "near shadow map stays at " + EffectiveNearMapSize + "px: " + LastNearError;
        return size <= 0
            ? "near shadow map back to the engine's " + EffectiveNearMapSize + "px (same as the far map)"
            : "near shadow map now " + NearMapSizeApplied + "px";
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
        var result = StepsFor(steps, ExtraSteps);
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
        => TryForceRebuild(platform, log, out _);

    /// <summary>
    /// Same, and names what blocked a retry - a session that ended with "window never
    /// ready" and no reason is exactly the report this could not answer.
    /// </summary>
    public static bool TryForceRebuild(ClientPlatformWindows platform, Action<string> log, out string blockedBy)
    {
        blockedBy = null;
        var quality = ClientSettings.ShadowMapQuality;

        // Two reasons to rebuild, each with its own "already done" and its own gate. The
        // extra step: below the slider's ceiling the transpiler would allocate the vanilla
        // size anyway, so a forced rebuild - which recreates EVERY framebuffer, not just the
        // shadow maps - would be a world-join hitch for nothing; should the player raise the
        // slider later, the engine's own rebuild runs the transpiler and the rule applies
        // then. The near map: only exists from quality 2, and only needs the rebuild once
        // (NearSetupRan), whatever size it then decided on.
        var stepsPending = ExtraSteps > 0 && ShadowMapSize == 0;
        if (stepsPending && !AppliesAt(quality))
        {
            log?.Invoke(quality == 0
                ? "shadows are off - the extra shadow map step waits until they are on and at quality 4"
                : $"shadow quality {quality} is below the menu's ceiling (4), where the extra step applies - "
                  + $"raise the slider instead; the map stays at {Math.Max(4, quality + 2) * 1024}px");
            stepsPending = false;
        }
        var nearPending = NearMapSize > 0 && !NearSetupRan && quality > 1;
        if (!stepsPending && !nearPending) return true;   // nothing to do / already real
        if (ShaderRegistry.SupressShaderAndBufferReloads)
        {
            blockedBy = "engine suppresses buffer reloads";
            return false; // engine busy - retry
        }

        // Never rebuild into a window that cannot host framebuffers. This shipped a crash
        // (31.08.2026, Windows tester): alt-tab out of fullscreen minimises the window,
        // SetupDefaultFrameBuffers then hits its degenerate-size bailout and returns a list
        // of NULL entries, RebuildFrameBuffers adopts it and disposes the good buffers, and
        // the next frame dies in ClearFrameBuffer(LiquidDepth) inside vanilla code. The
        // engine itself never rebuilds while minimised (Window_Resize checks the state);
        // this forced rebuild has to hold itself to the same rule.
        NativeWindow win = platform.window;
        if (win == null)
        {
            blockedBy = "no window yet";
            return false;
        }
        if (!CanHostFramebuffers(win.WindowState == WindowState.Minimized,
                win.ClientSize.X, win.ClientSize.Y, ClientSettings.SSAA))
        {
            blockedBy = win.WindowState == WindowState.Minimized
                ? "window minimised"
                : $"window {win.ClientSize.X}x{win.ClientSize.Y} at SSAA {ClientSettings.SSAA:0.##} cannot host framebuffers";
            return false; // alt-tabbed / degenerate window - retry when it is visible again
        }

        try
        {
            platform.RebuildFrameBuffers();
        }
        catch (Exception e)
        {
            log?.Invoke($"could not rebuild framebuffers for the larger shadow maps ({e.GetType().Name}) - shadow map stays vanilla-sized");
            return true;
        }

        if (ShadowMapSize > 0 || NearSetupRan)
        {
            var near = NearMapSize > 0
                ? (LastNearError != null
                    ? $", near map stays {EffectiveNearMapSize}px ({LastNearError})"
                    : $", near map {EffectiveNearMapSize}px")
                : "";
            log?.Invoke($"shadow map framebuffers rebuilt at {EffectiveMapSize}px{near} (vanilla setup ran before the mod loaded)");
            return true;
        }
        blockedBy = "rebuild ran but the shadow size expression was never reached";
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
