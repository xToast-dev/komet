using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Patches;

/// <summary>
/// Stops distant shadow edges from crawling as the player walks.
///
/// The engine renders everything camera-relative and centres the shadow projection on the
/// camera. ShadowBox.getCameraRotationMatrix returns the identity, so the box does not even
/// follow where you look - it is a fixed shape anchored to the player - and loadOrthoModeMatrix
/// only ever sets the three scale terms, never a translation. The result is a shadow map whose
/// texel grid slides continuously through the world with the player. Every fraction of a block
/// you move, every shadow edge re-samples on a different texel boundary, and the edges shimmer
/// and crawl. It is the classic shadow mapping artefact and it has a classic answer: quantise
/// the projection to whole texels, so the grid stands still in the world while the player moves
/// through it.
///
/// The offset goes into the projection matrix's translation column, which vanilla leaves at
/// zero, so this adds a term rather than fighting one. Everything downstream - the pushed
/// PMatrix, shadowMvpMatrix, toShadowMapSpaceMatrix* - is derived from that same matrix
/// afterwards and picks the offset up on its own.
///
/// Note the exact sign of the offset does not matter: what removes the crawl is that the
/// position is quantised at all. A sign error snaps to the same grid half a texel across.
/// </summary>
public static class ShadowStabilityPatches
{
    public static bool Enabled;

    /// <summary>Snap offsets applied, so the HUD can show that it is doing something.</summary>
    public static long StatSnaps;

    private static readonly AccessTools.FieldRef<SystemRenderShadowMap, double[]> LightViewRef =
        AccessTools.FieldRefAccess<SystemRenderShadowMap, double[]>("lightViewMatrix");
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> GameRef =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");

    public static void EnsureReady()
    {
        if (LightViewRef == null || GameRef == null)
            throw new InvalidOperationException("SystemRenderShadowMap internals not found");
    }

    public static void Apply(Harmony harmony)
    {
        EnsureReady();

        MethodInfo ortho = AccessTools.Method(typeof(SystemRenderShadowMap), "loadOrthoModeMatrix",
                               [typeof(double[]), typeof(double), typeof(double), typeof(double)])
                           ?? throw new InvalidOperationException("loadOrthoModeMatrix not found");

        harmony.Patch(ortho, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ShadowStabilityPatches), nameof(SnapToTexelGrid))));
        Enabled = true;
    }

    /// <summary>
    /// How far, in light space units, the projection has to be pulled back so the camera lands
    /// on a whole texel. Pure arithmetic, split out from the patch so the property that
    /// matters - that the result is quantised and never more than one texel - can be checked
    /// without a running client.
    /// </summary>
    internal static bool SnapOffset(double[] lightView, double camX, double camY, double camZ,
                                    double width, double height, int mapSize,
                                    out double offsetX, out double offsetY)
    {
        offsetX = offsetY = 0;
        if (lightView == null || lightView.Length < 16 || mapSize <= 0) return false;

        double texelX = width / mapSize;
        double texelY = height / mapSize;
        if (texelX <= 0 || texelY <= 0) return false;

        // The camera's world position in light space. Only the rotation matters - the
        // translation column would cancel out, since the projection is centred on the camera
        // either way. lightViewMatrix still holds the previous pass's value at this point,
        // which is the same sun direction to well within a texel.
        double lx = lightView[0] * camX + lightView[4] * camY + lightView[8] * camZ;
        double ly = lightView[1] * camX + lightView[5] * camY + lightView[9] * camZ;

        offsetX = lx - Math.Floor(lx / texelX) * texelX;
        offsetY = ly - Math.Floor(ly / texelY) * texelY;
        return true;
    }

    /// <summary>
    /// Runs right after the ortho projection is built and before it is combined with the light
    /// view matrix, which is exactly the window in which a translation can still be added.
    /// </summary>
    public static void SnapToTexelGrid(SystemRenderShadowMap __instance, double[] projectionMatrix,
                                       double width, double height)
    {
        if (!Enabled || projectionMatrix == null || width <= 0 || height <= 0) return;

        try
        {
            ClientMain game = GameRef(__instance);
            Vintagestory.API.MathTools.Vec3d cam = game?.EntityPlayer?.CameraPos;
            double[] lightView = LightViewRef(__instance);
            if (cam == null || lightView == null || lightView.Length < 16) return;

            // Shadow map resolution. Snapping to the wrong grid does not snap at all: the
            // offset lands on a boundary the sampler does not have, and the crawl this is
            // supposed to remove comes back as a sub-texel one. ShadowResPatches enlarges the
            // framebuffer past what the setting alone implies, so its size wins when it is
            // active; the engine's own formula is the fallback.
            int mapSize = ShadowResPatches.EffectiveMapSize;
            if (!SnapOffset(lightView, cam.X, cam.Y, cam.Z, width, height, mapSize,
                            out double offsetX, out double offsetY))
                return;

            // Light-space units into normalised device coordinates: the ortho projection maps
            // [-width/2, width/2] onto [-1, 1], so a shift of t is 2t/width.
            projectionMatrix[12] -= 2.0 * offsetX / width;
            projectionMatrix[13] -= 2.0 * offsetY / height;

            StatSnaps++;
        }
        catch (Exception)
        {
            // A stabilisation nicety must never be the reason a frame dies. Switch it off and
            // let vanilla's unsnapped projection through for the rest of the session.
            Enabled = false;
        }
    }
}
