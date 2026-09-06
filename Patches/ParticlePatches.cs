using System;
using System.Diagnostics;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// What the particle system costs on the render thread, and how many particles are alive.
///
/// It was a blind spot: the mod measured the render stages, the tick listeners, the renderers,
/// the uploads and the culling, and particles appeared in none of it - so "is it the particles?"
/// could only ever be answered with an opinion. This measures it, and nothing else. There is no
/// optimisation in this file on purpose: the last thing this project needs is another change
/// made against a hypothesis instead of a number.
///
/// Where the cost is: <c>SystemRenderParticles.Render</c> calls <c>OnNewFrame</c> on both the
/// main-thread pool and the off-thread pool of the model it is drawing, inside the Opaque stage
/// (cubes) and the OIT stage (quads). For the MAIN-thread pools that call is the whole physics
/// step - <c>TickFixedStep</c> per particle, block collision included - plus the instance-buffer
/// fill and a <c>UpdateMesh</c> upload. For the off-thread pools the physics already ran on the
/// separate thread and the call only picks up the finished buffer. Both are timed here; the
/// report prints them apart, because they are different problems with the same name.
///
/// <c>ParticlePoolCubes</c> extends <c>ParticlePoolQuads</c> and does not override
/// <c>OnNewFrame</c>, so one patch covers all four pools.
/// </summary>
public static class ParticlePatches
{
    /// <summary>Off is exactly vanilla: no bracket, no timestamps.</summary>
    public static bool Enabled;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // accumulated within the frame, folded at the boundary
    private static long mainTicks;
    private static long offTicks;

    /// <summary>Smoothed cost per frame of the main-thread pools' physics and upload, and of
    /// picking up what the separate thread produced.</summary>
    public static double MainThreadMs { get; private set; }
    public static double OffThreadPickupMs { get; private set; }

    /// <summary>
    /// The upload's share of <see cref="MainThreadMs"/>: the time inside Platform.UpdateMesh
    /// while a main-thread pool's OnNewFrame is running. Split out because the two are
    /// different problems. TickFixedStep advances a particle only every PhysicsTickTime
    /// (a sixteenth of a second), so 660 particles cannot be 16 ms of physics - but
    /// glBufferSubData on an instance buffer the GPU is still reading blocks the render thread
    /// until the GPU has caught up, and that is a GPU-bound frame's wait showing up wherever
    /// the CPU next touches a busy buffer.
    /// </summary>
    public static double UploadMs { get; private set; }

    private static long uploadTicks;
    private static bool inNewFrame;

    /// <summary>
    /// Tell the driver the instance buffers' contents are undefined before overwriting them.
    ///
    /// The measurement that asks for this: 1.543 particles, three instance buffers, 48 KB in
    /// total, and 10,43 ms per frame inside UpdateMesh. Forty percent of a 25,8 ms frame to
    /// move 48 KB is not a transfer, it is a wait. glBufferSubData on a buffer the GPU may
    /// still be reading has to either stall until that draw retires or make the driver
    /// allocate a shadow copy; the particle instance buffers are rewritten every single frame
    /// and drawn every single frame, which is exactly the case that stalls.
    ///
    /// glInvalidateBufferData says "the old contents no longer matter", which lets the driver
    /// hand out fresh storage instead of waiting - the buffer is renamed, not synchronised.
    /// It is safe here for a reason worth stating: the pool writes AliveCount instances and
    /// the draw call reads exactly AliveCount instances, so nothing ever reads the part of the
    /// buffer that invalidation discards.
    ///
    /// Off by default until a report prices it. '.komet toggle particleorphan' does the A/B in
    /// one command, and the particle row prints the upload time either way.
    /// </summary>
    public static bool Orphan;
    public static bool ConfiguredOrphan;

    /// <summary>Whether the driver has ARB_invalidate_subdata (GL 4.3). Checked once, on the
    /// render thread, the first time an upload comes past.</summary>
    public static bool OrphanSupported { get; private set; }
    private static bool orphanChecked;
    public static long StatOrphaned;

    /// <summary>Particles alive in the pools, by where their physics runs.</summary>
    public static int AliveMainThread { get; private set; }
    public static int AliveOffThread { get; private set; }

    /// <summary>Calls seen since the last frame boundary - 0 says the bracket is not running,
    /// which is a different thing from "no particles".</summary>
    public static long StatCalls { get; private set; }

    private static int aliveMain, aliveOff;
    private static long calls;

    private static readonly AccessTools.FieldRef<ParticlePoolQuads, bool> OffthreadRef =
        AccessTools.FieldRefAccess<ParticlePoolQuads, bool>("offthread");

    public static void Apply(Harmony harmony)
    {
        var onNewFrame = AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame),
                             [typeof(float), typeof(Vintagestory.API.MathTools.Vec3d)])
                         ?? throw new InvalidOperationException("ParticlePoolQuads.OnNewFrame not found");
        if (OffthreadRef == null) throw new InvalidOperationException("ParticlePoolQuads.offthread not found");

        harmony.Patch(onNewFrame,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ParticlePatches), nameof(Before))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ParticlePatches), nameof(After))));

        // the upload inside that call, told apart from the physics around it
        var updateMesh = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateMesh),
                             [typeof(Vintagestory.API.Client.MeshRef), typeof(Vintagestory.API.Client.MeshData)])
                         ?? throw new InvalidOperationException("ClientPlatformWindows.UpdateMesh not found");
        harmony.Patch(updateMesh,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ParticlePatches), nameof(BeforeUpload))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ParticlePatches), nameof(AfterUpload))));
    }

    public static void BeforeUpload(Vintagestory.API.Client.MeshRef modelRef,
                                    Vintagestory.API.Client.MeshData data, ref long __state)
    {
        if (!inNewFrame) { __state = 0; return; }
        if (Orphan) TryOrphan(modelRef, data);
        __state = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Invalidates exactly the buffers this call is about to rewrite, and no others. Any
    /// failure switches the feature off rather than repeating itself per frame.
    /// </summary>
    private static void TryOrphan(Vintagestory.API.Client.MeshRef modelRef, Vintagestory.API.Client.MeshData data)
    {
        if (modelRef is not VAO vao || data == null) return;
        try
        {
            if (!orphanChecked)
            {
                orphanChecked = true;
                OrphanSupported = HasInvalidateSubdata();
                if (!OrphanSupported) Orphan = false;
            }
            if (!OrphanSupported) return;

            if (data.xyz != null && vao.xyzVboId != 0) GL.InvalidateBufferData(vao.xyzVboId);
            if (data.Flags != null && data.FlagsCount > 0 && vao.flagsVboId != 0) GL.InvalidateBufferData(vao.flagsVboId);
            if (data.CustomFloats != null && data.CustomFloats.Count > 0 && vao.customDataFloatVboId != 0)
                GL.InvalidateBufferData(vao.customDataFloatVboId);
            if (data.CustomBytes != null && data.CustomBytes.Count > 0 && vao.customDataByteVboId != 0)
                GL.InvalidateBufferData(vao.customDataByteVboId);
            StatOrphaned++;
        }
        catch (Exception)
        {
            Orphan = false;
            OrphanSupported = false;
        }
    }

    private static bool HasInvalidateSubdata()
    {
        try
        {
            var major = GL.GetInteger((GetPName)0x821B); // MAJOR_VERSION
            var minor = GL.GetInteger((GetPName)0x821C); // MINOR_VERSION
            if (major > 4 || (major == 4 && minor >= 3)) return true;
            var count = GL.GetInteger((GetPName)0x821D);  // NUM_EXTENSIONS
            for (var i = 0; i < count; i++)
                if (GL.GetString((StringNameIndexed)0x1F03, i) == "GL_ARB_invalidate_subdata") return true;
        }
        catch (Exception) { /* no context, or the enum is not understood */ }
        return false;
    }

    public static void AfterUpload(long __state)
    {
        if (__state == 0) return;
        uploadTicks += Stopwatch.GetTimestamp() - __state;
    }

    public static void Before(ref long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        inNewFrame = __state != 0;
    }

    public static void After(ParticlePoolQuads __instance, long __state)
    {
        inNewFrame = false;
        if (__state == 0) return;
        var spent = Stopwatch.GetTimestamp() - __state;
        calls++;

        // The flag the pool was constructed with, not the stage: the same pool object is asked
        // on whichever stage draws its model.
        bool off;
        try { off = OffthreadRef(__instance); }
        catch (Exception) { return; }

        if (off)
        {
            offTicks += spent;
            aliveOff += __instance.QuantityAlive;
        }
        else
        {
            mainTicks += spent;
            aliveMain += __instance.QuantityAlive;
        }
    }

    /// <summary>Folds the frame that just ended. Same smoothing as the rest of the HUD.</summary>
    public static void EndFrame()
    {
        const double alpha = 1.0 / 32.0;
        var main = mainTicks * TicksToMs;
        var off = offTicks * TicksToMs;
        var upload = uploadTicks * TicksToMs;
        MainThreadMs += (main - MainThreadMs) * alpha;
        OffThreadPickupMs += (off - OffThreadPickupMs) * alpha;
        UploadMs += (upload - UploadMs) * alpha;

        AliveMainThread = aliveMain;
        AliveOffThread = aliveOff;
        StatCalls = calls;

        mainTicks = offTicks = uploadTicks = 0;
        aliveMain = aliveOff = 0;
        calls = 0;
    }

    public static void Reset()
    {
        MainThreadMs = OffThreadPickupMs = UploadMs = 0;
        AliveMainThread = AliveOffThread = 0;
        StatCalls = 0;
        StatOrphaned = 0;
        orphanChecked = false;
    }
}
