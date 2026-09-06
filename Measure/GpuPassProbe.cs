using System;
using OpenTK.Graphics.OpenGL;

namespace Komet.Measure;

/// <summary>
/// GPU time and fragment count per chunk pass, measured the one way that cannot be fooled by
/// the pipeline.
///
/// The per-stage figures (<see cref="GpuFrameTimer.StageGpuMs"/>) are timestamps, and a
/// timestamp is written when the command processor REACHES it - not when the work before it
/// has finished. Draw calls are dispatched in microseconds and complete later; whatever span
/// contains the next barrier (a framebuffer clear, a texture that was just rendered to being
/// sampled) inherits everything still in flight. That is how a report came to show
/// <c>near 17,4 | opaque 0,0</c> for a near pass of 593.438 triangles and a camera pass of
/// 17 million: the opaque draws were dispatched for nothing and finished inside the next
/// frame's shadow clear. Three optimisations were aimed at the near cascade on the strength of
/// that row before its triangle count was printed next to it.
///
/// <c>GL_TIME_ELAPSED</c> is different: it ends when the commands inside it have COMPLETED. A
/// bracket around one pass is the time that pass really took, whoever was in flight before
/// it. And <c>GL_FRAGMENT_SHADER_INVOCATIONS</c> (ARB_pipeline_statistics_query) counts the
/// fragments the pass shaded - the number that decides "fill or geometry" without an argument.
///
/// One elapsed-time query may be active at a time, and <see cref="GpuFrameTimer"/> holds one
/// around the whole frame. So the probe takes every <see cref="Every"/>th frame for itself: on
/// a probe frame the frame query is not issued and the passes are bracketed instead, each in
/// turn. The frame figure loses a third of its samples and nothing else. Results are read four
/// probes later and only when the driver says they are available - a read never waits.
///
/// Brackets: the near and far cascades' solid half and foliage half of
/// <c>ChunkRenderer.RenderShadow</c> (the transpiled boundary ShadowCullPatches already owns),
/// and <c>ChunkRenderer.RenderOpaque</c> for the camera pass.
/// </summary>
public static class GpuPassProbe
{
    public enum Pass { NearSolid = 0, NearFoliage = 1, FarSolid = 2, FarFoliage = 3, CameraOpaque = 4 }
    public const int PassCount = 5;

    /// <summary>Off: no probe frames, the frame query runs every frame as before.</summary>
    public static bool Enabled;

    /// <summary>Every Nth frame is a probe frame. 3 leaves the frame query two of three.</summary>
    public static int Every = 3;

    /// <summary>Whether the current frame is a probe frame - decided in <see cref="FrameBegin"/>.</summary>
    public static bool ProbeFrame { get; private set; }

    /// <summary>Whether the driver can count fragment shader invocations.</summary>
    public static bool FragmentsSupported { get; private set; }
    public static string FragmentsUnsupportedReason { get; private set; }

    /// <summary>Smoothed GPU milliseconds and fragment shader invocations per pass.</summary>
    public static readonly double[] PassMs = new double[PassCount];
    public static readonly double[] PassFragments = new double[PassCount];
    public static readonly long[] PassSamples = new long[PassCount];

    /// <summary>GL_FRAGMENT_SHADER_INVOCATIONS_ARB. The OpenTK build the game ships names
    /// it under another enum; the value is the extension's, 0x82F4.</summary>
    private const QueryTarget FragmentInvocations = (QueryTarget)0x82F4;

    private const int Depth = 4;
    private static readonly int[] timeQueries = new int[PassCount * Depth];
    private static readonly int[] fragQueries = new int[PassCount * Depth];
    private static readonly bool[] issued = new bool[PassCount * Depth];
    private static bool generated, extensionsChecked;
    private static long frame;
    private static int slot;
    private static int active = -1;
    private static int failures;

    /// <summary>The schedule, pure: which frames probe.</summary>
    internal static bool IsProbeFrame(long frameNumber, int every) => every > 0 && frameNumber % every == 0;

    /// <summary>The ring slot a probe writes, and the slot it reads - Depth-1 probes older,
    /// so the queries in it are Depth-1 probe frames old and finished by construction.</summary>
    internal static int WriteSlot(long probeNumber) => (int)(probeNumber % Depth);
    internal static int ReadSlot(long probeNumber) => (int)((probeNumber + 1) % Depth);

    /// <summary>Called first thing in the Before stage, before the frame query is decided.</summary>
    public static void FrameBegin()
    {
        frame++;
        ProbeFrame = false;
        if (!Enabled) return;
        if (!IsProbeFrame(frame, Every)) return;

        try
        {
            if (!extensionsChecked) CheckExtensions();
            if (!generated)
            {
                GL.GenQueries(timeQueries.Length, timeQueries);
                if (FragmentsSupported) GL.GenQueries(fragQueries.Length, fragQueries);
                generated = true;
            }
            var probe = frame / Every;
            slot = WriteSlot(probe);
            Collect(ReadSlot(probe));
            // the slot about to be reused: anything still unread in it is simply dropped
            for (var p = 0; p < PassCount; p++) issued[p * Depth + slot] = false;
            active = -1;
            ProbeFrame = true;
        }
        catch (Exception)
        {
            if (++failures >= 3) Enabled = false;
        }
    }

    public static void Begin(Pass pass)
    {
        if (!ProbeFrame) return;
        try
        {
            if (active >= 0) EndActive();
            var p = (int)pass;
            GL.BeginQuery(QueryTarget.TimeElapsed, timeQueries[p * Depth + slot]);
            if (FragmentsSupported) GL.BeginQuery(FragmentInvocations, fragQueries[p * Depth + slot]);
            active = p;
        }
        catch (Exception)
        {
            active = -1;
            if (++failures >= 3) Enabled = false;
        }
    }

    public static void End(Pass pass)
    {
        if (!ProbeFrame || active != (int)pass) return;
        EndActive();
    }

    private static void EndActive()
    {
        try
        {
            GL.EndQuery(QueryTarget.TimeElapsed);
            if (FragmentsSupported) GL.EndQuery(FragmentInvocations);
            issued[active * Depth + slot] = true;
        }
        catch (Exception)
        {
            if (++failures >= 3) Enabled = false;
        }
        active = -1;
    }

    /// <summary>Reads one ring slot's finished queries into the smoothed figures. Availability
    /// is asked first, so a slow driver costs a sample, never a stall.</summary>
    private static void Collect(int readSlot)
    {
        for (var p = 0; p < PassCount; p++)
        {
            var i = p * Depth + readSlot;
            if (!issued[i]) continue;
            issued[i] = false;

            GL.GetQueryObject(timeQueries[i], GetQueryObjectParam.QueryResultAvailable, out int ready);
            if (ready == 0) continue;
            GL.GetQueryObject(timeQueries[i], GetQueryObjectParam.QueryResult, out long ns);
            var ms = ns / 1_000_000.0;
            if (ms < 0 || ms > 1000) continue;

            double frags = -1;
            if (FragmentsSupported)
            {
                GL.GetQueryObject(fragQueries[i], GetQueryObjectParam.QueryResultAvailable, out int fready);
                if (fready != 0)
                {
                    GL.GetQueryObject(fragQueries[i], GetQueryObjectParam.QueryResult, out long count);
                    frags = count;
                }
            }

            const double alpha = 0.25;
            PassMs[p] = PassSamples[p] == 0 ? ms : PassMs[p] + (ms - PassMs[p]) * alpha;
            if (frags >= 0)
                PassFragments[p] = PassSamples[p] == 0 ? frags : PassFragments[p] + (frags - PassFragments[p]) * alpha;
            PassSamples[p]++;
        }
    }

    private static void CheckExtensions()
    {
        extensionsChecked = true;
        try
        {
            var n = GL.GetInteger(GetPName.NumExtensions);
            for (var i = 0; i < n; i++)
            {
                if (GL.GetString(StringNameIndexed.Extensions, i) == "GL_ARB_pipeline_statistics_query")
                {
                    FragmentsSupported = true;
                    return;
                }
            }
            FragmentsUnsupportedReason = "the driver has no GL_ARB_pipeline_statistics_query";
        }
        catch (Exception e)
        {
            FragmentsUnsupportedReason = "extension query failed: " + e.GetType().Name;
        }
        FragmentsSupported = false;
    }

    public static void Reset()
    {
        Array.Clear(PassMs);
        Array.Clear(PassFragments);
        Array.Clear(PassSamples);
    }
}
