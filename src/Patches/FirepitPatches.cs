using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Komet.Patches;

/// <summary>
/// Skips FirepitContentsRenderer for firepits too far away to show their contents.
///
/// The engine never reads IRenderer.RenderRange, so every firepit with something in it runs
/// its full render every frame at any distance: a shader Use/Stop pair, ~15 uniform uploads,
/// a GetTemperature attribute-tree walk and a GetLightRGBs chunk lookup - measured at
/// 4.01 ms of a 23 ms frame in a ruins area while chunks streamed in (the light lookup
/// contends on the same chunk data the network thread is inserting into). The renderer
/// itself declares RenderRange 48 - this patch simply enforces a slightly more generous
/// version of the limit its author intended, for this one renderer whose skip is provably
/// safe.
///
/// Why this is not the removed generic distance gate: skipping this renderer leaves EXACTLY
/// the GL state an empty firepit leaves, because an empty firepit already returns without a
/// single GL call - vanilla produces "some firepits draw, some don't" every frame it renders
/// a scene with empty pits. There is no state a successor could miss. The generic gate died
/// because that argument could not be made for renderers in general.
///
/// The gate never fires when a child renderer (a pot or crucible) is attached: those manage
/// the cooking sound volume inside OnRenderFrame, and audio must not stop at a draw
/// distance.
///
/// For firepits INSIDE the distance limit a second measurement told a second story: the gate
/// skipped 24 000 distant renders and the row still read 4.6 ms, while the same GL work cost
/// 0.5 ms in a fully loaded world. The per-frame cost of a near firepit is not the draw -
/// it is GetLightRGBs (a chunk-data read that contends with the network thread inserting
/// ~1000 chunks/s while streaming), GetTemperature (an attribute-tree walk) and
/// GetIncandescenceColorAsColor4f (allocates a float[4] per call). None of those change
/// meaningfully within a tenth of a second, so the near path re-renders the same mesh with
/// values cached per firepit and refreshed on a timer - the draw itself is a faithful
/// transcription of the renderer's own source (it ships in the game's open VSSurvivalMod).
/// </summary>
public static class FirepitPatches
{
    /// <summary>Runtime switch for `.komet toggle firepit` and safemode.</summary>
    public static bool Enabled = true;

    /// <summary>Blocks beyond which contents are skipped. 0 disables the patch entirely.</summary>
    public static int MaxDistance;

    /// <summary>Skips this frame - if this stays 0 with firepits around, the patch is dead.</summary>
    public static long StatSkipped;

    /// <summary>Camera position, republished every frame boundary by the mod system.</summary>
    public static volatile Vec3d CameraPos;

    /// <summary>Set by the mod system once the client API exists; the fast path needs it.</summary>
    public static ICoreClientAPI Api;

    /// <summary>How long a firepit's light/temperature stay cached. 0 = near firepits render vanilla.</summary>
    public static int LightCacheMs;

    /// <summary>Near firepits drawn through the cached fast path.</summary>
    public static long StatFastPath;

    /// <summary>Near, content-bearing firepits that fell back to the vanilla draw. A large
    /// value with StatFastPath at zero means the fast path is broken, not unused.</summary>
    public static long StatNearVanilla;

    /// <summary>True once the fast path gave up - shown in the HUD, detailed in the log.</summary>
    public static bool FastPathBroken => fastPathBroken;

    public static Action<string> Log;

    private static AccessTools.FieldRef<object, BlockPos> posRef;
    private static AccessTools.FieldRef<object, object> childRef;
    private static AccessTools.FieldRef<object, MultiTextureMeshRef> meshRef;
    private static AccessTools.FieldRef<object, ModelTransform> transformRef;
    private static AccessTools.FieldRef<object, ItemStack> stackRef;
    private static bool fastPathBroken;

    /// <summary>Everything about a firepit that does not change frame to frame.</summary>
    private sealed class Cache
    {
        public readonly Matrixf Mat = new();
        public readonly Vec4f LitColor = new();
        public int Glow;
        public long RefreshAt;
    }

    private static readonly ConditionalWeakTable<object, Cache> Caches = new();

    public static void Apply(Harmony harmony, int maxDistance, int lightCacheMs)
    {
        MaxDistance = maxDistance;
        LightCacheMs = lightCacheMs;
        if (maxDistance <= 0 && lightCacheMs <= 0) return;

        // VSSurvivalMod is a separate mod assembly, resolved at runtime so this mod neither
        // hard-depends on it nor breaks when it is absent.
        Type type = AccessTools.TypeByName("Vintagestory.GameContent.FirepitContentsRenderer")
                    ?? throw new InvalidOperationException("FirepitContentsRenderer not found (VSSurvivalMod not loaded?)");

        posRef = AccessTools.FieldRefAccess<BlockPos>(type, "pos");
        childRef = AccessTools.FieldRefAccess<object>(type, "contentStackRenderer");
        meshRef = AccessTools.FieldRefAccess<MultiTextureMeshRef>(type, "meshref");
        transformRef = AccessTools.FieldRefAccess<ModelTransform>(type, "Transform");
        stackRef = AccessTools.FieldRefAccess<ItemStack>(type, "ContentStack");
        if (posRef == null || childRef == null || meshRef == null || transformRef == null || stackRef == null)
            throw new InvalidOperationException("FirepitContentsRenderer fields not found");

        MethodInfo render = AccessTools.Method(type, "OnRenderFrame")
                            ?? throw new InvalidOperationException("FirepitContentsRenderer.OnRenderFrame not found");

        harmony.Patch(render, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(FirepitPatches), nameof(SkipDistant))));
    }

    /// <summary>The rule on its own, so the boundary cases are pinned by a test.</summary>
    internal static bool ShouldSkip(bool hasChildRenderer, double distSq, int maxDistance)
        => !hasChildRenderer && maxDistance > 0 && distSq > (double)maxDistance * maxDistance;

    /// <summary>
    /// False = handled here: either the firepit is too far away for its contents to matter,
    /// or it was drawn through the cached fast path.
    /// </summary>
    public static bool SkipDistant(object __instance)
    {
        if (!Enabled) return true;

        Vec3d cam = CameraPos;
        if (cam == null) return true;

        BlockPos pos = posRef(__instance);
        if (pos == null) return true;
        if (childRef(__instance) != null) return true;   // pot/crucible: sound logic, hands off

        double dx = pos.X + 0.5 - cam.X;
        double dy = pos.Y + 0.5 - cam.Y;
        double dz = pos.Z + 0.5 - cam.Z;

        if (ShouldSkip(hasChildRenderer: false, dx * dx + dy * dy + dz * dz, MaxDistance))
        {
            StatSkipped++;
            return false;
        }

        if (LightCacheMs <= 0 || fastPathBroken) return true;
        if (TryRenderCached(__instance, pos)) return false;

        // reaching here with contents means the cache path declined or died - count it so a
        // broken fast path can never masquerade as "no firepits nearby"
        if (stackRef(__instance)?.Collectible != null) StatNearVanilla++;
        return true;
    }

    /// <summary>
    /// The mesh branch of FirepitContentsRenderer.OnRenderFrame, transcribed from its source,
    /// with the three per-frame lookups replaced by the timer-refreshed cache. The GL state
    /// left behind (cull face off, blend on, shader stopped) is exactly vanilla's.
    /// </summary>
    private static int fastPathFailures;

    private static bool TryRenderCached(object __instance, BlockPos pos)
    {
        ICoreClientAPI capi = Api;
        MultiTextureMeshRef mesh = meshRef(__instance);
        ItemStack stack = stackRef(__instance);
        ModelTransform tf = transformRef(__instance);
        if (capi == null || stack?.Collectible == null || tf == null) return false;
        if (mesh == null) return true;                   // vanilla would do nothing - so do we

        // the LIVE camera position, exactly as vanilla reads it mid-draw - the frame-boundary
        // snapshot used for the distance gate is a tick stale, which a gate does not care
        // about but a model matrix does
        Vec3d cam = capi.World?.Player?.Entity?.CameraPos;
        if (cam == null) return false;

        try
        {
            Cache cache = Caches.GetOrCreateValue(__instance);
            long now = Environment.TickCount64;
            if (now >= cache.RefreshAt)
            {
                int temp = (int)stack.Collectible.GetTemperature(capi.World, stack);
                Vec4f light = capi.World.BlockAccessor.GetLightRGBs(pos.X, pos.Y, pos.Z);
                float[] glowColor = ColorUtil.GetIncandescenceColorAsColor4f(temp);
                cache.LitColor.Set(light.R + glowColor[0], light.G + glowColor[1], light.B + glowColor[2], light.A);
                cache.Glow = GameMath.Clamp((temp - 500) / 4, 0, 255);
                cache.RefreshAt = now + LightCacheMs;
            }

            IRenderAPI render = capi.Render;
            render.GlDisableCullFace();
            render.GlToggleBlend(true);
            IStandardShaderProgram prog = render.StandardShader;
            prog.Use();
            prog.DontWarpVertices = 0;
            prog.AddRenderFlags = 0;
            prog.RgbaAmbientIn = render.AmbientColor;
            prog.RgbaFogIn = render.FogColor;
            prog.FogMinIn = render.FogMin;
            prog.FogDensityIn = render.FogDensity;
            prog.RgbaTint = ColorUtil.WhiteArgbVec;
            prog.NormalShaded = 1;
            prog.ExtraGodray = 0f;
            prog.SsaoAttn = 0f;
            prog.AlphaTest = 0.05f;
            prog.OverlayOpacity = 0f;
            prog.RgbaLightIn = cache.LitColor;
            prog.ExtraGlow = cache.Glow;
            prog.ModelMatrix = cache.Mat.Identity()
                .Translate(pos.X - cam.X + tf.Translation.X, pos.Y - cam.Y + tf.Translation.Y, pos.Z - cam.Z + tf.Translation.Z)
                .Translate(tf.Origin.X, 0.6f + tf.Origin.Y, tf.Origin.Z)
                .RotateX(tf.Rotation.X * GameMath.DEG2RAD)
                .RotateY(tf.Rotation.Y * GameMath.DEG2RAD)
                .RotateZ(tf.Rotation.Z * GameMath.DEG2RAD)
                .Scale(tf.ScaleXYZ.X, tf.ScaleXYZ.Y, tf.ScaleXYZ.Z)
                .Translate(0f - tf.Origin.X, 0f - tf.Origin.Y, 0f - tf.Origin.Z)
                .Values;
            prog.ViewMatrix = render.CameraMatrixOriginf;
            prog.ProjectionMatrix = render.CurrentProjectionMatrix;
            render.RenderMultiTextureMesh(mesh, "tex");
            prog.Stop();

            StatFastPath++;
            return true;
        }
        catch (Exception e)
        {
            // A silent self-disable is how this shipped broken once already: the HUD read
            // "0 aus cache" and nothing said why. Log every failure, give a transient early
            // hiccup two more chances, then hand near firepits to vanilla for good.
            Log?.Invoke($"firepit fast path failed ({e.GetType().Name}: {e.Message}) at\n{e.StackTrace}");
            if (++fastPathFailures >= 3)
            {
                fastPathBroken = true;
                Log?.Invoke("firepit fast path disabled for this session - near firepits render vanilla");
            }
            return false;
        }
    }
}
