using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Puts the minimap's per-tick texture work under an adaptive budget, and makes each piece
/// cheap in the first place.
///
/// The tick profiler's first field report named the dominant hitch bucket of a join flood:
/// 127 of 339 hitches were "tick", and every one of them carried
/// <c>tick-listener WorldMapManager.OnClientTick</c> at 2,5 to 8,5 ms. The mechanism, from
/// VSEssentials' ChunkMapLayer: map pieces (one 32x32 image per chunk column) are generated on
/// a worker and queued; OnTick then dequeues up to 200 of them per 20 ms tick, and for every
/// affected 3x3 component creates a framebuffer, uploads each piece as a texture and draws it
/// into the component's 96x96 texture. While chunks stream in - every loaded column enqueues
/// itself and four neighbours - that is hundreds of texture uploads and draws in one tick,
/// on the main thread, and nothing needs them that fast: the minimap is a HUD element.
///
/// Two layers:
///
/// 1. A transpiler replaces the constant 200 with <see cref="PiecesPerTick"/>, whose value
///    adapts: a prefix/postfix pair times each OnTick in which pieces were dequeued, and the
///    cap halves when the tick ran over 1,5x the target and doubles (up to vanilla's 200)
///    when it stayed under half of it. Nothing is dropped - the pieces stay in the engine's
///    own queue and go out on the following ticks.
///
/// 2. The second field report showed the cap pinned at its floor of 8 and STILL 1,14 ms per
///    tick - 0,14 ms per piece, for a 4 KB image. That is what vanilla's FinishSetChunks
///    costs per piece: a framebuffer object created and destroyed per component per tick, a
///    shader switch, a 32x32 texture re-uploaded WITH mipmaps as a staging texture, a quad
///    drawn through the texture2texture program, then the FBO torn down. All of it to copy
///    32x32 pixels into a rectangle of a 96x96 texture - which is exactly one
///    glTexSubImage2D. The shader (texture2texture.vsh) does not flip: quad vertex (-1,-1)
///    carries uv (0,0), ys maps to the rectangle's lower edge in framebuffer rows, so piece
///    row r lands on texture row 32*(i/3)+r, column 32*(i%3)+c - the same rows the
///    sub-image upload writes. <see cref="FinishSetChunksPrefix"/> replaces the draw with
///    that upload (then the same mipmap regeneration vanilla does). The one semantic
///    difference is the shader's alpha test (source pixels under 0,005 alpha were skipped,
///    keeping the old pixel); a map piece only has such pixels where the rain height map is
///    out of range, which is the same pixels in every generation of that piece.
/// </summary>
public static class MinimapPatches
{
    public static bool Enabled = true;

    /// <summary>Main-thread milliseconds per tick the piece uploads may take; the cap
    /// adapts around it. 0 = vanilla (200 pieces per tick, unconditionally).</summary>
    public static double TargetMs = 1.0;

    public const int VanillaCap = 200;
    public const int MinCap = 8;

    /// <summary>Pieces the next tick may dequeue; adapted after every measured tick.</summary>
    public static int Cap { get; private set; } = 32;

    /// <summary>Ticks that had pieces to upload, and their smoothed cost.</summary>
    public static long StatTicks;
    public static double AvgTickMs { get; private set; }

    private static bool dequeuedThisTick;

    // ---- direct upload ---------------------------------------------------------------

    /// <summary>Compose pieces into the component texture with glTexSubImage2D instead of
    /// vanilla's framebuffer draw per piece. Off = vanilla's FinishSetChunks, untouched.</summary>
    public static bool DirectUpload = true;

    public const int PiecePx = 32;
    public const int ComponentPx = 96;

    /// <summary>Pieces uploaded directly / components finished that way, since start.</summary>
    public static long StatDirectPieces, StatDirectComponents;

    private static AccessTools.FieldRef<object, int[][]> pixelsToSetRef;
    private static AccessTools.FieldRef<object, LoadedTexture> textureRef;
    private static AccessTools.FieldRef<object, ICoreClientAPI> capiRef;
    private static AccessTools.FieldRef<int[]> emptyPixelsRef;
    private static readonly Action<int, int, int[]> GlUpload = UploadPiece;

    public static void Apply(Harmony harmony)
    {
        var layer = AccessTools.TypeByName("Vintagestory.GameContent.ChunkMapLayer")
                    ?? throw new InvalidOperationException("ChunkMapLayer not found - VSEssentials not loaded?");
        var onTick = AccessTools.Method(layer, "OnTick", [typeof(float)])
                     ?? throw new InvalidOperationException("ChunkMapLayer.OnTick(float) not found");
        harmony.Patch(onTick,
            prefix: new HarmonyMethod(typeof(MinimapPatches), nameof(TickPrefix)),
            postfix: new HarmonyMethod(typeof(MinimapPatches), nameof(TickPostfix)),
            transpiler: new HarmonyMethod(typeof(MinimapPatches), nameof(CapPieces)));

        var component = AccessTools.TypeByName("Vintagestory.GameContent.MultiChunkMapComponent")
                        ?? throw new InvalidOperationException("MultiChunkMapComponent not found");
        var finish = AccessTools.Method(component, "FinishSetChunks")
                     ?? throw new InvalidOperationException("MultiChunkMapComponent.FinishSetChunks not found");
        pixelsToSetRef = AccessTools.FieldRefAccess<int[][]>(component, "pixelsToSet");
        textureRef = AccessTools.FieldRefAccess<LoadedTexture>(component, "Texture");
        capiRef = AccessTools.FieldRefAccess<ICoreClientAPI>(component, "capi");
        emptyPixelsRef = AccessTools.StaticFieldRefAccess<int[]>(AccessTools.Field(component, "emptyPixels")
                         ?? throw new InvalidOperationException("MultiChunkMapComponent.emptyPixels not found"));
        harmony.Patch(finish, prefix: new HarmonyMethod(typeof(MinimapPatches), nameof(FinishSetChunksPrefix)));
    }

    /// <summary>
    /// The 200 in <c>Math.Min(readyMapPieces.Count, 200)</c> is the only constant of that value
    /// in the method; it becomes a call. Anything else - none or several - throws rather than
    /// capping the wrong loop.
    /// </summary>
    public static IEnumerable<CodeInstruction> CapPieces(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var call = AccessTools.Method(typeof(MinimapPatches), nameof(PiecesPerTick));
        var patched = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (!code[i].LoadsConstant(VanillaCap)) continue;
            // in place, so the instruction keeps its labels and exception blocks
            code[i].opcode = OpCodes.Call;
            code[i].operand = call;
            patched++;
        }
        if (patched != 1)
            throw new InvalidOperationException($"expected exactly one piece cap of {VanillaCap} in ChunkMapLayer.OnTick, found {patched}");
        return code;
    }

    /// <summary>Called by the patched OnTick in place of the constant, only when pieces wait.</summary>
    public static int PiecesPerTick()
    {
        dequeuedThisTick = true;
        return Enabled && TargetMs > 0 ? Cap : VanillaCap;
    }

    /// <summary>The controller, pure: halve when the tick blew the target, double when it had
    /// room to spare, otherwise hold. Clamped to [MinCap, VanillaCap].</summary>
    internal static int Adapt(int cap, double lastMs, double targetMs)
    {
        if (lastMs > targetMs * 1.5) return Math.Max(MinCap, cap / 2);
        if (lastMs < targetMs * 0.5) return Math.Min(VanillaCap, cap * 2);
        return cap;
    }

    public static void TickPrefix(out long __state)
    {
        dequeuedThisTick = false;
        __state = Stopwatch.GetTimestamp();
    }

    public static void TickPostfix(long __state)
    {
        if (!dequeuedThisTick) return;
        var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        StatTicks++;
        AvgTickMs += (ms - AvgTickMs) * (1.0 / 16.0);
        if (Enabled && TargetMs > 0) Cap = Adapt(Cap, ms, TargetMs);
    }

    /// <summary>
    /// MultiChunkMapComponent.FinishSetChunks with the framebuffer draw replaced by direct
    /// sub-image uploads. Same texture creation as vanilla when the component has none yet
    /// (96x96 from the shared empty buffer, mipmapped), same mipmap regeneration at the end,
    /// same clearing of the pending pieces. Returns true (vanilla runs) while disabled.
    /// </summary>
    public static bool FinishSetChunksPrefix(object __instance)
    {
        if (!DirectUpload) return true;
        var pieces = pixelsToSetRef(__instance);
        if (pieces == null) return false; // vanilla returns early too
        var capi = capiRef(__instance);
        ref var texture = ref textureRef(__instance);
        if (texture == null || texture.Disposed)
        {
            texture = new LoadedTexture(capi, 0, ComponentPx, ComponentPx);
            capi.Render.LoadOrUpdateTextureFromRgba(emptyPixelsRef() ?? new int[ComponentPx * ComponentPx], false, 0, ref texture);
        }
        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);
        var n = Compose(pieces, GlUpload);
        capi.Render.BindTexture2d(texture.TextureId);
        capi.Render.GlGenerateTex2DMipmaps();
        pixelsToSetRef(__instance) = null;
        StatDirectPieces += n;
        StatDirectComponents++;
        return false;
    }

    private static void UploadPiece(int x, int y, int[] pixels)
        => GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, PiecePx, PiecePx, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

    /// <summary>
    /// The composition rule, pure: slot i of the 3x3 goes to column 32*(i%3), row 32*(i/3)
    /// - the rectangle vanilla's framebuffer draw covered. Null slots are skipped. Returns
    /// the number of pieces handed to the upload.
    /// </summary>
    internal static int Compose(int[][] pieces, Action<int, int, int[]> upload)
    {
        var n = 0;
        for (var i = 0; i < pieces.Length; i++)
        {
            if (pieces[i] == null) continue;
            upload(PiecePx * (i % 3), PiecePx * (i / 3), pieces[i]);
            n++;
        }
        return n;
    }

    public static void ResetStats()
    {
        StatTicks = 0;
        AvgTickMs = 0;
        StatDirectPieces = 0;
        StatDirectComponents = 0;
    }
}
