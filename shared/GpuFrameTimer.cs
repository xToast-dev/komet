using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;

namespace Komet.Measure;

/// <summary>
/// Measures how long the GPU takes per frame, so "are we CPU-bound or GPU-bound?" becomes a
/// number on the HUD instead of a debate.
///
/// The question this settles, concretely: underwater the frame went from 11.3 to 25.5 ms and
/// the firepit renderer from 0.6 to 4.9 ms - with the same firepits on screen. If the GPU is
/// the bottleneck there, the extra milliseconds are back-pressure pooling wherever the most GL
/// calls are issued, and no CPU work will fix them; if it is not, something CPU-side really
/// did get five times slower. Until now the HUD's only GPU signal was "= ausserhalb", which
/// only catches waiting at the swap - back-pressure inside the stages was invisible.
///
/// Mechanics: one GL_TIME_ELAPSED query spans each frame (begun in the Before stage, ended in
/// Done). Results are read from a four-deep ring, so a query is at least three frames old
/// before it is touched - and only once a second, because glGetQueryObject returns a value and
/// every returning GL call under mesa_glthread is a driver sync (the lesson of the 1.86 ms sun
/// query). One sync per second on a long-finished query is noise; per frame it was the single
/// most expensive renderer.
///
/// GL_TIME_ELAPSED queries cannot nest, so this must stay the only user of that target. The
/// engine's own occlusion queries use GL_SAMPLES_PASSED - a different target, no conflict.
/// </summary>
public static class GpuFrameTimer
{
    public static bool Enabled;

    /// <summary>Smoothed GPU time per frame, milliseconds. 0 until the first result lands.</summary>
    public static double GpuMs { get; private set; }

    private const int Ring = 4;
    private static readonly int[] queries = new int[Ring];
    private static readonly bool[] pending = new bool[Ring];
    private static long frame;
    private static bool queryActive;
    private static double sampleAccum;
    private static int failures;

    /// <summary>Begins the frame's query. Runs first in the Before stage.</summary>
    public sealed class BeginRenderer : IRenderer
    {
        public double RenderOrder => 0.0;
        public int RenderRange => 0;

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled || queryActive) return;

            try
            {
                if (queries[0] == 0) GL.GenQueries(Ring, queries);

                int slot = (int)(frame % Ring);
                if (pending[slot]) return; // ring full - a result reader has fallen behind

                GL.BeginQuery(QueryTarget.TimeElapsed, queries[slot]);
                queryActive = true;
            }
            catch (Exception)
            {
                if (++failures >= 3) Enabled = false;
            }
        }

        public void Dispose() { }
    }

    /// <summary>Ends the query and occasionally collects an old result. Runs last in Done.</summary>
    public sealed class EndRenderer : IRenderer
    {
        public double RenderOrder => 999.0;
        public int RenderRange => 0;

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled || !queryActive) return;

            try
            {
                GL.EndQuery(QueryTarget.TimeElapsed);
                queryActive = false;
                pending[(int)(frame % Ring)] = true;
                frame++;

                // Collect twice a second. The candidate is three frames old by construction,
                // so the one returning GL call this makes should find it finished. Half-second
                // sampling with a 0.4 blend converges in about two seconds - the first field
                // reading produced byte-identical values above and under water because the
                // scene had changed faster than a once-per-second 0.25 blend could follow.
                sampleAccum += dt;
                if (sampleAccum < 0.5f || frame < Ring) return;
                sampleAccum = 0;

                int oldest = (int)((frame - (Ring - 1)) % Ring);
                if (!pending[oldest]) return;

                long nanoseconds = 0;
                GL.GetQueryObject(queries[oldest], GetQueryObjectParam.QueryResult, out nanoseconds);
                pending[oldest] = false;

                double ms = nanoseconds / 1_000_000.0;
                if (ms > 0 && ms < 1000)
                    GpuMs = GpuMs <= 0 ? ms : GpuMs + (ms - GpuMs) * 0.4;
            }
            catch (Exception)
            {
                queryActive = false;
                if (++failures >= 3) Enabled = false;
            }
        }

        public void Dispose() { }
    }

    public static void Reset()
    {
        GpuMs = 0;
    }
}
