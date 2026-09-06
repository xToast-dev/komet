using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// The solid terrain passes inside the shadow passes: back-face culling, and a depth-only
/// shader without the alpha test.
///
/// ChunkRenderer.RenderShadow switches face culling OFF for the whole method and draws four
/// chunk render passes into the shadow map: Opaque (0) and TopSoil (5) first, then, after a
/// second GlDisableCullFace, BlendNoCull (2) and OpaqueNoCull (1). The second half needs no
/// culling by definition - leaves, grass, crops, anything single-sided lives there and has to
/// cast a shadow from either side. The first half does not: in the camera pass the very same
/// pools are drawn with culling ON (RenderOpaque enables it before pass 0 and only disables it
/// ahead of pass 2), so their winding is consistent and every volume they describe is closed.
/// A closed volume's back faces lie behind its front faces along any ray - the light's ray
/// included - so the depth map is the same with or without them. What culling removes is the
/// work: half of every solid block face in the shadow box rasterised, depth-tested and, in
/// whatever order the pools happen to come, written and overwritten. On a pass that the GPU
/// report shows to be fill-bound (the near cascade draws a few dozen chunks into 51 million
/// texels and costs as much as the far cascade drawing thousands), that is the cheapest
/// fill there is to give back: the primitive is dropped before a single fragment exists.
///
/// The one place the two depth maps can differ is open geometry in a solid pass - a face
/// whose back is the only thing along the light ray. Such a block is already invisible from
/// behind in the camera view, which is what "solid pass" means; the engine's own edge at the
/// end of the loaded world is the practical case, and it lies beyond the fade. GL_BACK is set
/// explicitly rather than assumed: the engine sets it after OIT and at start-up and nothing in
/// the shipped game sets FRONT, but a mod's renderer may, and the cost is one call.
///
/// The second lever on the same passes, since 05.09. (evening): the shader. chunkshadowmap.fsh
/// samples the terrain texture and discards below alpha 0.02 - for every fragment of every
/// pass, the solid cubes included, whose texels are never transparent. A fragment shader with
/// `discard` in it costs more than its instructions: the hardware cannot write depth before the
/// shader has run, so early depth rejection of the fragments BEHIND the surface is weakened
/// (the test still runs, the hierarchical Z update waits for the shader), and every surviving
/// fragment pays a texture fetch. For the solid passes Komet binds a program with the SAME
/// vertex shader - the engine's own code, prefix defines and includes copied, so SSBO mode,
/// wind warping and whatever a shader mod put there are reproduced exactly - and an EMPTY
/// fragment shader: no fetch, no discard, depth written early, occluded fragments never shaded.
/// The foliage passes keep the engine's program; they need the alpha test.
///
/// Installed as a transpiler on RenderShadow: the first GlDisableCullFace becomes
/// <see cref="BeginSolidPasses"/>, the second <see cref="EndSolidPasses"/>, and the first two
/// of the four Tex2d2D texture binds - the ones inside the solid loops - become
/// <see cref="SetSolidTexture"/>, which is a no-op while the depth-only program is bound (its
/// uniform locations are not the engine program's, and a glUniform on the wrong location is a
/// GL error per pool). A method shape other than 2 + 4 throws, so an engine build that moved
/// the calls refuses the patch rather than culling or shading the wrong pass.
/// </summary>
public static class ShadowCullPatches
{
    /// <summary>Live switch for the back-face culling. Off is exactly vanilla.</summary>
    public static bool Enabled;

    /// <summary>Live switch for the depth-only program on the solid passes. Off = the
    /// engine's chunkshadowmap program for every pass, exactly vanilla.</summary>
    public static bool DepthOnly;

    /// <summary>Shadow passes that began with culling on, for the HUD's "is it doing anything".</summary>
    public static long StatCulledPasses;

    /// <summary>Shadow passes whose solid half ran on the depth-only program.</summary>
    public static long StatDepthOnlyPasses;

    /// <summary>Why the depth-only program is not in use, or null. "not built yet" until the
    /// first shadow pass; a compile failure sticks until the next shader reload.</summary>
    public static string DepthOnlyState { get; private set; } = "not built yet";

    /// <summary>The client, for the shadow MVP matrix and the atlas padding the program needs.
    /// Set at world start; null keeps the engine's program (culling still applies).</summary>
    public static ClientMain Game;

    /// <summary>True between the solid and the foliage half of a shadow pass - set at the
    /// transpiled boundary, cleared when RenderShadow returns.</summary>
    public static bool InFoliageHalf { get; private set; }

    /// <summary>
    /// Diagnostic: skip the foliage passes (BlendNoCull, OpaqueNoCull) of BOTH shadow maps -
    /// leaves, grass and crops stop casting. It changes the picture on purpose: it is the
    /// one-command answer to "is the shadow pass paying for the foliage?", read off the GPU
    /// row while it is on. '.komet toggle shadowfoliage'; never on by config.
    /// </summary>
    public static bool SkipFoliage;
    public static long StatFoliageSkipped;

    private static ShaderProgram solid;
    private static bool solidFailed;
    private static bool solidActive;

    private static readonly AccessTools.FieldRef<ClientMain, float[]> ShadowMvpRef =
        AccessTools.FieldRefAccess<ClientMain, float[]>("shadowMvpMatrix");

    public static void Apply(Harmony harmony, bool enabled, bool depthOnly)
    {
        Enabled = enabled;
        DepthOnly = depthOnly;
        var render = AccessTools.Method(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)])
                     ?? throw new InvalidOperationException("ChunkRenderer.RenderShadow not found");
        harmony.Patch(render, transpiler: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowCullPatches), nameof(CullSolidPasses))));
        // the end of the foliage half: closes the probe's bracket and the half itself
        harmony.Patch(render, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowCullPatches), nameof(AfterRenderShadow))));

        var poolRender = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render),
                             [typeof(Vintagestory.API.MathTools.Vec3d), typeof(string), typeof(EnumFrustumCullMode)])
                         ?? throw new InvalidOperationException("MeshDataPoolManager.Render not found");
        harmony.Patch(poolRender, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowCullPatches), nameof(SkipFoliageRender))));
    }

    /// <summary>Which cascade RenderShadow is drawing into: the prefix on
    /// PrepareForShadowRendering remembered it, and nothing between there and here changes it.</summary>
    private static bool Far => ShadowPatches.PreparingFarCascade;

    /// <summary>Runs when RenderShadow returns: the foliage half is over.</summary>
    public static void AfterRenderShadow()
    {
        InFoliageHalf = false;
        Measure.GpuPassProbe.End(Far ? Measure.GpuPassProbe.Pass.FarFoliage : Measure.GpuPassProbe.Pass.NearFoliage);
    }

    /// <summary>A pool manager's Render inside the foliage half of a shadow pass, while the
    /// diagnostic skip is on, draws nothing. Everywhere else it is untouched.</summary>
    public static bool SkipFoliageRender()
    {
        if (!SkipFoliage || !InFoliageHalf) return true;
        StatFoliageSkipped++;
        return false;
    }

    /// <summary>What the first GlDisableCullFace of RenderShadow now does.</summary>
    public static void BeginSolidPasses(ClientPlatformAbstract platform)
    {
        InFoliageHalf = false;
        Measure.GpuPassProbe.Begin(Far ? Measure.GpuPassProbe.Pass.FarSolid : Measure.GpuPassProbe.Pass.NearSolid);
        if (Enabled)
        {
            platform.GlCullFaceBack();
            platform.GlEnableCullFace();
            StatCulledPasses++;
        }
        else
        {
            platform.GlDisableCullFace();
        }

        solidActive = false;
        if (!DepthOnly) return;
        try
        {
            var game = Game;
            var engine = ShaderPrograms.Chunkshadowmap;
            if (game == null || engine == null || !EnsureSolidProgram(engine)) return;

            // Use() refuses to replace a program that is in use, so the engine's is stopped
            // first; EndSolidPasses puts it back exactly the same way.
            ShaderProgramBase.CurrentShaderProgram?.Stop();
            solid.Use();
            solid.UniformMatrix("mvpMatrix", ShadowMvpRef(game));
            var atlas = game.BlockAtlasManager;
            if (atlas != null)
            {
                solid.Uniform("subpixelPaddingX", atlas.SubPixelPaddingX);
                solid.Uniform("subpixelPaddingY", atlas.SubPixelPaddingY);
            }
            solidActive = true;
            StatDepthOnlyPasses++;
        }
        catch (Exception e)
        {
            // never let the shadow pass down: back to the engine's program for good
            DepthOnlyState = "disabled after " + e.GetType().Name;
            solidFailed = true;
            try
            {
                ShaderProgramBase.CurrentShaderProgram?.Stop();
                ShaderPrograms.Chunkshadowmap?.Use();
            }
            catch (Exception) { /* nothing more to do */ }
            solidActive = false;
        }
    }

    /// <summary>What the second GlDisableCullFace now does: no-cull for the foliage passes,
    /// as before, and the engine's program back if the solid half ran on ours.</summary>
    public static void EndSolidPasses(ClientPlatformAbstract platform)
    {
        platform.GlDisableCullFace();
        Measure.GpuPassProbe.End(Far ? Measure.GpuPassProbe.Pass.FarSolid : Measure.GpuPassProbe.Pass.NearSolid);
        Measure.GpuPassProbe.Begin(Far ? Measure.GpuPassProbe.Pass.FarFoliage : Measure.GpuPassProbe.Pass.NearFoliage);
        InFoliageHalf = true;
        if (!solidActive) return;
        solidActive = false;
        try
        {
            solid.Stop();
            ShaderPrograms.Chunkshadowmap.Use();
        }
        catch (Exception e)
        {
            DepthOnlyState = "disabled after " + e.GetType().Name;
            solidFailed = true;
        }
    }

    /// <summary>The engine's texture bind inside the solid loops. Ours samples nothing, and
    /// the engine's uniform location would be applied to OUR program - so it is skipped
    /// while ours is bound.</summary>
    public static void SetSolidTexture(ShaderProgramChunkshadowmap program, int textureId)
    {
        if (!solidActive) program.Tex2d2D = textureId;
    }

    /// <summary>Whether the depth-only program is compiled and in service.</summary>
    public static bool DepthOnlyLive => solid != null && !solidFailed;

    /// <summary>
    /// Builds the depth-only program from the engine's, on the render thread (a GL context is
    /// needed to compile). Everything that decides what the vertex shader computes is taken
    /// from the engine's program object as it stands right now: the include-expanded source
    /// (a shader mod's replacement included), the prefix defines (USESSBO, WAVINGSTUFF ...),
    /// the include set (Use() keys its uniform uploads on it) and the attribute bindings. The
    /// file name has to start with "chunk": that is how the engine decides to raise the
    /// version to 430 for the SSBO path - Shader.UsesSSBOs() reads the name, not the code.
    /// </summary>
    private static bool EnsureSolidProgram(ShaderProgramChunkshadowmap engine)
    {
        if (solid != null) return true;
        if (solidFailed) return false;
        if (engine.VertexShader == null || engine.FragmentShader == null || engine.ProgramId == 0)
        {
            DepthOnlyState = "engine program not loaded yet";
            return false;
        }

        var program = new ShaderProgram
        {
            PassName = "chunkshadowmap-komet-solid",
            AssetDomain = "komet",
            LoadFromFile = false,
        };
        program.VertexShader = new Shader(EnumShaderType.VertexShader, engine.VertexShader.Code, "chunkshadowmap-komet-solid.vsh")
        {
            PrefixCode = engine.VertexShader.PrefixCode,
        };
        program.FragmentShader = new Shader(EnumShaderType.FragmentShader, DepthOnlyFragmentSource, "chunkshadowmap-komet-solid.fsh")
        {
            PrefixCode = engine.FragmentShader.PrefixCode,
        };
        foreach (var include in engine.includes) program.includes.Add(include);
        foreach (var kv in engine.attributes) program.attributes[kv.Key] = kv.Value;

        bool ok;
        try { ok = program.Compile(); }
        catch (Exception e)
        {
            DepthOnlyState = "compile threw " + e.GetType().Name;
            solidFailed = true;
            return false;
        }
        if (!ok)
        {
            DepthOnlyState = "compile or link failed (see client-main.log)";
            solidFailed = true;
            try { program.Dispose(); } catch (Exception) { /* partial program */ }
            return false;
        }
        solid = program;
        DepthOnlyState = null;
        return true;
    }

    /// <summary>A fragment shader that does nothing: the depth write is all the pass is for.
    /// The engine's prefix defines are inserted after the version line, like everywhere.</summary>
    internal const string DepthOnlyFragmentSource = "#version 330 core\n\nvoid main(void)\n{\n}\n";

    /// <summary>
    /// After the engine reloaded its shaders (settings change, '.reload shaders', a shader
    /// mod), ours is stale: the defines and possibly the source changed. Dropped here and
    /// rebuilt on the next shadow pass, from whatever the engine has then. Main thread with a
    /// GL context - that is where the engine's reload event fires.
    /// </summary>
    public static void OnShadersReloaded()
    {
        var old = solid;
        solid = null;
        solidFailed = false;
        solidActive = false;
        DepthOnlyState = "not built yet";
        try { old?.Dispose(); } catch (Exception) { /* the context may be gone */ }
    }

    /// <summary>
    /// Replaces the FIRST GlDisableCullFace with <see cref="BeginSolidPasses"/>, the SECOND
    /// with <see cref="EndSolidPasses"/>, and the first two Tex2d2D binds with
    /// <see cref="SetSolidTexture"/>. Receivers and arguments are already on the stack at
    /// each site (ldarg.0; ldfld platform - and ldloc program; ldelem texture id), and the
    /// static replacements take exactly those, so no other instruction changes.
    /// </summary>
    public static IEnumerable<CodeInstruction> CullSolidPasses(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var begin = AccessTools.Method(typeof(ShadowCullPatches), nameof(BeginSolidPasses));
        var end = AccessTools.Method(typeof(ShadowCullPatches), nameof(EndSolidPasses));
        var setTex = AccessTools.Method(typeof(ShadowCullPatches), nameof(SetSolidTexture));
        var disables = new List<int>();
        var texBinds = new List<int>();

        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].operand is not MethodInfo m) continue;
            if (!(code[i].opcode == OpCodes.Callvirt || code[i].opcode == OpCodes.Call)) continue;
            if (m.Name == nameof(ClientPlatformAbstract.GlDisableCullFace)) disables.Add(i);
            else if (m.Name == "set_Tex2d2D" && m.DeclaringType == typeof(ShaderProgramChunkshadowmap)) texBinds.Add(i);
        }

        // RenderShadow disables culling twice (top: solid passes follow; middle: no-cull
        // passes follow) and binds the atlas texture once per pass loop, four loops. Any
        // other count means a different method, and the wrong pass culled or shaded without
        // its alpha test would be a shadow that silently disappears.
        if (disables.Count != 2)
            throw new InvalidOperationException(
                $"expected exactly two GlDisableCullFace calls in ChunkRenderer.RenderShadow, found {disables.Count}");
        if (texBinds.Count != 4)
            throw new InvalidOperationException(
                $"expected exactly four Tex2d2D binds in ChunkRenderer.RenderShadow, found {texBinds.Count}");
        if (!(texBinds[0] > disables[0] && texBinds[1] > disables[0] && texBinds[1] < disables[1] && texBinds[2] > disables[1]))
            throw new InvalidOperationException("ChunkRenderer.RenderShadow: the texture binds are not two per half around the second GlDisableCullFace");

        Replace(code, disables[0], begin);
        Replace(code, disables[1], end);
        Replace(code, texBinds[0], setTex);
        Replace(code, texBinds[1], setTex);
        return code;
    }

    private static void Replace(List<CodeInstruction> code, int at, MethodInfo with)
        => code[at] = new CodeInstruction(OpCodes.Call, with).WithLabels(code[at].labels).WithBlocks(code[at].blocks);
}
