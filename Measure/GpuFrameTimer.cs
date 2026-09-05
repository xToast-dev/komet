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
/// did get five times slower. Until now the HUD's only GPU signal was "= außerhalb", which
/// only catches waiting at the swap - back-pressure inside the stages was invisible.
///
/// Mechanics: one GL_TIME_ELAPSED query spans each frame (begun in the Before stage, ended in
/// Done). The query objects form a four-deep ring, so the one read is always three frames old
/// - and it is read only twice a second, because glGetQueryObject returns a value and every
/// returning GL call under mesa_glthread is a driver sync (the lesson of the 1.86 ms sun
/// query). One sync per half second on a long-finished query is noise; per frame it was the
/// single most expensive renderer.
///
/// The ring is overwrite-on-wrap, and that is not a detail: the first version refused to
/// begin a query on a slot whose result had not been read yet. With one read per half second
/// and a slot consumed per frame the ring was full after four frames, and from then on no
/// query was ever begun again - the read cadence only advanced inside the end handler, which
/// returned early because no query was active. The reported figure was the ONE frame that got
/// read before the stall, frozen for the rest of the session: 173 ms from a world-join frame
/// on a tester's laptop, misread for two field reports as a GPU-bound machine. An unread
/// result is simply discarded when its slot comes round again; <see cref="QueryRing"/> holds
/// that rule without any GL so the harness can drive a thousand frames through it.
///
/// Second instrument, since 03.09.: WHERE the GPU's milliseconds go. A GL_TIMESTAMP query is
/// written at the start of every render stage (from the stage measurement prefix, so it sits
/// exactly where the CPU-side stage clock starts) and one more at the very end of the frame;
/// the differences are the GPU time each stage's commands took, shadow cascades, opaque,
/// post-processing and the GUI apart. The span "gpu 11,56 ms in a 12,19 ms frame" said the
/// GPU was near the wall and nothing about which pass to shrink - the shadow map step, the
/// far cascade's LOD, another mod's post-processing all look the same from the total.
/// Timestamps do not nest and do not interfere with the elapsed query; the reads are one
/// returning call per stamped stage, once a second, on a set that is three frames old.
///
/// Both figures are GPU SPAN, including any idle gap in which the GPU waited for the main
/// thread to submit more - see <see cref="GpuBusy"/> for the driver's utilisation figure.
///
/// GL_TIME_ELAPSED queries cannot nest, so this must stay the only user of that target. The
/// engine's own occlusion queries use GL_SAMPLES_PASSED - a different target, no conflict.
/// </summary>
public static class GpuFrameTimer
{
    public static bool Enabled;

    /// <summary>Smoothed GPU time per frame, milliseconds. 0 until the first result lands.</summary>
    public static double GpuMs { get; private set; }

    /// <summary>Results folded into <see cref="GpuMs"/> since start - the proof the ring is
    /// turning (a frozen figure and a live one look the same on the HUD).</summary>
    public static long StatSamples { get; private set; }

    /// <summary>
    /// The slot and read schedule, free of GL. Slots are handed out round-robin, one per
    /// frame; a read picks the slot that was ended <c>Depth-1</c> frames ago, which by then is
    /// finished on the GPU and is not the slot the next frame is about to reuse.
    /// </summary>
    internal sealed class QueryRing
    {
        public const int Depth = 4;

        /// <summary>Seconds between two result reads.</summary>
        public double ReadInterval = 0.5;

        private long frame;          // completed queries so far
        private bool active;
        private double sinceRead;

        public bool Active => active;
        public long Completed => frame;

        /// <summary>The slot to begin this frame's query on, or -1 while one is still open.</summary>
        public int Begin()
        {
            if (active) return -1;
            active = true;
            return (int)(frame % Depth);
        }

        /// <summary>
        /// Closes the frame's query. Returns the slot whose result is due to be read now, or -1.
        /// Only counts time while queries actually run, so a stretch without a GL context
        /// cannot make the first read land on a slot that never held a query.
        /// </summary>
        public int End(double dtSeconds)
        {
            if (!active) return -1;
            active = false;
            frame++;
            sinceRead += dtSeconds;
            if (sinceRead < ReadInterval || frame < Depth) return -1;
            // carry the remainder so the cadence is really two a second and not "two a
            // second, rounded up to whole frames"; after one very long frame (a pause,
            // a load stall) the excess is dropped rather than paid back as a read burst
            sinceRead -= ReadInterval;
            if (sinceRead > ReadInterval) sinceRead = 0;
            return ReadSlot(frame);
        }

        /// <summary>The slot ended Depth-1 frames before the most recent one.</summary>
        internal static int ReadSlot(long completed) => (int)((completed - (Depth - 1)) % Depth);

        public void Reset()
        {
            frame = 0;
            active = false;
            sinceRead = 0;
        }
    }

    // ---- per-stage timestamps ------------------------------------------------------------

    /// <summary>One timestamp per render stage plus the frame's end.</summary>
    public const int StageSlots = FrameStats.StageCount + 1;
    public const int EndSlot = FrameStats.StageCount;

    /// <summary>Smoothed GPU milliseconds per render stage (index = EnumRenderStage).</summary>
    public static readonly double[] StageGpuMs = new double[FrameStats.StageCount];

    /// <summary>Stage sets folded so far.</summary>
    public static long StageSamples { get; private set; }

    /// <summary>
    /// Asked at the end of every frame whether the far shadow cascade was actually drawn in
    /// it. The mod hooks its throttle in here; without a hook every frame counts as drawn.
    /// A throttled far cascade renders in one frame of two to four, and the plain per-stage
    /// average then reads a third of the pass's real cost - "far 1,9" for a pass that takes
    /// 5-6 ms whenever it runs. The report needs both numbers to judge the map size.
    /// </summary>
    public static Func<bool> FarCascadeDrawn;

    /// <summary>Smoothed GPU milliseconds of the far cascade over the sampled frames in which
    /// it was drawn, and how many such samples there were. 0 until one lands.</summary>
    public static double FarDrawnGpuMs { get; private set; }
    public static long FarDrawnSamples { get; private set; }

    /// <summary>
    /// The whole frame as the stamps see it: end stamp minus Before stamp, smoothed the same
    /// way as the stages. The elapsed query and the stamps sample different frames (twice a
    /// second against once), and in a hitching scene the two figures can drift apart - the
    /// 05.09. report had the stage sum at 18 ms against an elapsed span of 9,7. Printed next
    /// to the stage figures, this says whether they are to be read against the elapsed
    /// figure at all, or against their own frame.
    /// </summary>
    public static double StampSpanMs { get; private set; }

    /// <summary>
    /// The same ring idea for a set of timestamps per frame: which stages were stamped in
    /// each slot (a stage the engine skips this frame - the throttled far cascade, OIT off -
    /// simply has no stamp and no GPU time), and when a three-frames-old set is due.
    /// </summary>
    internal sealed class StageRing
    {
        public const int Depth = QueryRing.Depth;
        public double ReadInterval = 1.0;

        private long frame;
        private bool open;
        private double sinceRead;
        public readonly bool[][] Stamped = new bool[Depth][];
        /// <summary>Per slot: whether the far cascade rendered in that frame (see <see cref="FarCascadeDrawn"/>).</summary>
        public readonly bool[] FarDrawn = new bool[Depth];

        public StageRing()
        {
            for (var i = 0; i < Depth; i++) Stamped[i] = new bool[StageSlots];
        }

        public bool Open => open;
        public int Slot => (int)(frame % Depth);

        /// <summary>Stage index of EnumRenderStage.Before, the stamp that opens a frame.</summary>
        public const int BeforeSlot = 0;

        /// <summary>The Before stage opens a frame: its slot is wiped, the Before stamp set,
        /// and the slot returned. The first cut forgot the stamp - the harness caught a read
        /// set without its first timestamp, which would have dropped the Before interval.</summary>
        public int BeginFrame()
        {
            open = true;
            var s = Slot;
            Array.Clear(Stamped[s]);
            Stamped[s][BeforeSlot] = true;
            return s;
        }

        /// <summary>Marks a stage stamped in the open frame; -1 when no frame is open.</summary>
        public int Stamp(int stage)
        {
            if (!open || stage < 0 || stage >= EndSlot) return -1;
            Stamped[Slot][stage] = true;
            return Slot;
        }

        /// <summary>Closes the frame with its end stamp. Returns the slot due for reading, or -1.</summary>
        public int EndFrame(double dtSeconds, bool farDrawn = true)
        {
            if (!open) return -1;
            Stamped[Slot][EndSlot] = true;
            FarDrawn[Slot] = farDrawn;
            open = false;
            frame++;
            sinceRead += dtSeconds;
            if (sinceRead < ReadInterval || frame < Depth) return -1;
            sinceRead -= ReadInterval;
            if (sinceRead > ReadInterval) sinceRead = 0;
            return QueryRing.ReadSlot(frame);
        }

        public void Reset()
        {
            frame = 0;
            open = false;
            sinceRead = 0;
            for (var i = 0; i < Depth; i++) Array.Clear(Stamped[i]);
            Array.Clear(FarDrawn);
        }
    }

    /// <summary>
    /// Nanoseconds per stage from one frame's stamps, pure. A stamped stage runs until the
    /// next stamped one (or the end stamp); an unstamped stage is 0. The engine's own work
    /// between two triggers - framebuffer switches, clears - lands in the stage before it.
    /// </summary>
    internal static void Intervals(long[] timestamps, bool[] stamped, long[] into)
    {
        Array.Clear(into);
        // Render order is timestamp order, NOT enum order: the enum lists the shadow stages
        // after Opaque, the frame renders them before it. The first cut walked the enum and
        // produced a negative Opaque interval in the harness.
        Span<int> order = stackalloc int[StageSlots];
        var n = 0;
        for (var i = 0; i < EndSlot; i++) if (stamped[i]) order[n++] = i;
        for (var i = 1; i < n; i++)
        {
            var k = order[i];
            var j = i - 1;
            while (j >= 0 && timestamps[order[j]] > timestamps[k]) { order[j + 1] = order[j]; j--; }
            order[j + 1] = k;
        }
        for (var i = 0; i + 1 < n; i++) into[order[i]] = timestamps[order[i + 1]] - timestamps[order[i]];
        if (n > 0 && stamped[EndSlot]) into[order[n - 1]] = timestamps[EndSlot] - timestamps[order[n - 1]];
    }

    private static readonly QueryRing ring = new();
    private static readonly StageRing stages = new();
    private static readonly int[] queries = new int[QueryRing.Depth];
    private static readonly int[] stampQueries = new int[StageRing.Depth * StageSlots];
    private static readonly long[] stampTimes = new long[StageSlots];
    private static readonly long[] stageNs = new long[FrameStats.StageCount];
    private static int failures;
    private static int stampFailures;

    /// <summary>
    /// Called at the start of every render stage, from the stage measurement prefix on the
    /// render thread. Never throws; three failures switch the stage stamps off and leave the
    /// frame span running.
    /// </summary>
    public static void StageBegin(EnumRenderStage stage)
    {
        if (!Enabled || stampFailures >= 3) return;
        try
        {
            if (stampQueries[0] == 0) GL.GenQueries(stampQueries.Length, stampQueries);
            var slot = stage == EnumRenderStage.Before ? stages.BeginFrame() : stages.Stamp((int)stage);
            if (slot < 0) return;
            GL.QueryCounter(stampQueries[slot * StageSlots + (int)stage], QueryCounterTarget.Timestamp);
        }
        catch (Exception)
        {
            stampFailures++;
            stages.Reset();
        }
    }

    /// <summary>The frame's last stamp, and the read of an old set when one is due.</summary>
    private static void StageEnd(double dt)
    {
        if (stampFailures >= 3 || !stages.Open) return;
        try
        {
            var slot = stages.Slot;
            GL.QueryCounter(stampQueries[slot * StageSlots + EndSlot], QueryCounterTarget.Timestamp);
            var farDrawn = true;
            try { farDrawn = FarCascadeDrawn?.Invoke() ?? true; } catch (Exception) { /* a hook must not cost the read */ }
            var read = stages.EndFrame(dt, farDrawn);
            if (read < 0) return;

            var stamped = stages.Stamped[read];
            for (var i = 0; i < StageSlots; i++)
            {
                if (!stamped[i]) continue;
                GL.GetQueryObject(stampQueries[read * StageSlots + i], GetQueryObjectParam.QueryResult, out long ts);
                stampTimes[i] = ts;
            }
            Intervals(stampTimes, stamped, stageNs);
            for (var i = 0; i < FrameStats.StageCount; i++)
            {
                var ms = stageNs[i] / 1_000_000.0;
                if (ms < 0 || ms >= 1000) ms = 0;
                StageGpuMs[i] += (ms - StageGpuMs[i]) * 0.4;
            }
            StageSamples++;
            if (stamped[StageRing.BeforeSlot] && stamped[EndSlot])
            {
                var span = (stampTimes[EndSlot] - stampTimes[StageRing.BeforeSlot]) / 1_000_000.0;
                if (span >= 0 && span < 1000) StampSpanMs += (span - StampSpanMs) * 0.4;
            }
            if (stages.FarDrawn[read])
            {
                var far = (stageNs[(int)EnumRenderStage.ShadowFar] + stageNs[(int)EnumRenderStage.ShadowFarDone]) / 1_000_000.0;
                if (far >= 0 && far < 1000)
                {
                    FarDrawnGpuMs += (far - FarDrawnGpuMs) * 0.4;
                    FarDrawnSamples++;
                }
            }
        }
        catch (Exception)
        {
            stampFailures++;
            stages.Reset();
        }
    }

    /// <summary>Sum of the smoothed stage figures, for the report's grouped columns.</summary>
    public static double StageSum(params EnumRenderStage[] which)
    {
        double s = 0;
        foreach (var st in which) s += StageGpuMs[(int)st];
        return s;
    }

    /// <summary>Begins the frame's query. Runs first in the Before stage.</summary>
    public sealed class BeginRenderer : IRenderer
    {
        public double RenderOrder => 0.0;
        public int RenderRange => 0;

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled) return;

            try
            {
                if (queries[0] == 0) GL.GenQueries(QueryRing.Depth, queries);

                var slot = ring.Begin();
                if (slot < 0) return; // still open - the Done stage never came last frame

                GL.BeginQuery(QueryTarget.TimeElapsed, queries[slot]);
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
            if (!Enabled) return;
            StageEnd(dt);
            if (!ring.Active) return;

            try
            {
                GL.EndQuery(QueryTarget.TimeElapsed);

                // Twice a second, a query that is three frames old - finished by
                // construction, so the one returning GL call this makes does not wait.
                // Half-second sampling with a 0.4 blend converges in about two seconds.
                var slot = ring.End(dt);
                if (slot < 0) return;

                long nanoseconds = 0;
                GL.GetQueryObject(queries[slot], GetQueryObjectParam.QueryResult, out nanoseconds);

                var ms = nanoseconds / 1_000_000.0;
                if (ms > 0 && ms < 1000)
                {
                    GpuMs = GpuMs <= 0 ? ms : GpuMs + (ms - GpuMs) * 0.4;
                    StatSamples++;
                }
            }
            catch (Exception)
            {
                ring.Reset();
                if (++failures >= 3) Enabled = false;
            }
        }

        public void Dispose() { }
    }

    public static void Reset()
    {
        GpuMs = 0;
        StatSamples = 0;
        StageSamples = 0;
        Array.Clear(StageGpuMs);
        FarDrawnGpuMs = 0;
        FarDrawnSamples = 0;
        StampSpanMs = 0;
    }
}
