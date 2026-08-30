using System;
using System.Diagnostics;

namespace Komet;

/// <summary>
/// Bounds how much chunk mesh data the main thread pushes to the GPU in one frame.
///
/// ChunkTesselatorManager.OnBeforeFrame computes its per-frame vertex budget as
///
///     num  = frustumCuller.ViewDistanceSq / 48 + 350
///     num3 = num * (3 + queueLength / (1 &lt;&lt; chunkVerticesUploadRateLimiter))
///
/// and uploads chunk meshes until that many vertices have gone through. The base term grows
/// with the *square* of the view distance - at 1536 it is 49 494 against 1 715 at 256, a
/// factor of 29 - and the second term multiplies it again by the backlog. That is a feedback
/// loop: a big backlog buys a huge upload budget, the huge upload makes the frame slow, the
/// slow frame grows the backlog. It is why raising the view distance collapses the frame rate
/// while moving, but not while standing still in already loaded terrain.
///
/// This scales that budget by a gain driven by how long the uploads actually took last frame.
/// The gain is capped at 1.0, so the mod never uploads more than vanilla would - it only
/// backs off once a frame's uploads exceed the target, and recovers as soon as they fit.
/// Chunks appear a little later at extreme view distances; the frame time stays bounded.
/// </summary>
public static class UploadBudget
{
    public static bool Enabled = true;

    /// <summary>Milliseconds of chunk uploading per frame to aim for.</summary>
    public static double TargetMs = 6.0;

    private static double gain = 1.0;
    private static readonly Stopwatch Watch = new();

    public static double Gain => gain;
    public static double LastMs { get; private set; }
    public static double PeakMs { get; private set; }
    public static long Frames { get; private set; }

    /// <summary>Called from the transpiler in place of the raw view-distance derived budget.</summary>
    public static int Scale(int vanillaBudget)
    {
        if (!Enabled) return vanillaBudget;
        int scaled = (int)(vanillaBudget * gain);
        // never throttle to a standstill: one average chunk part must always get through
        return scaled < 2048 ? 2048 : scaled;
    }

    public static void FrameStart()
    {
        Watch.Restart(); // measured even with the throttle off, so the counters stay honest
    }

    /// <summary>Largest correction the controller may apply in one frame, down and up.</summary>
    private const double MaxCut = 0.5;
    private const double MaxRaise = 1.25;

    public static void FrameEnd()
    {
        double ms = Watch.Elapsed.TotalMilliseconds;
        LastMs = ms;
        if (ms > PeakMs) PeakMs = ms;
        Frames++;

        if (!Enabled) return;

        // Proportional, not additive: aim straight at the target instead of stepping towards
        // it. Upload time is close to linear in the budget, so target/actual is a usable
        // estimate of the correction, and one bad frame is corrected in one frame rather than
        // in the four that repeated 0.75 steps took. That difference is the whole point - a
        // controller that lags the workload spends its time oscillating around the target,
        // and every oscillation is a frame that ran long.
        //
        // The clamps keep it honest in the two cases where the ratio means nothing: a frame
        // that uploaded almost nothing (empty queue) would otherwise ask for a huge jump, and
        // a single pathological frame would otherwise throttle to the floor.
        //
        // The band below the target is a deadband: without it the controller would raise the
        // budget the moment a frame came in even slightly under, overshoot, cut, and dither
        // around the target forever - which reads as exactly the stutter it is meant to remove.
        double correction;
        if (ms > TargetMs) correction = Math.Max(MaxCut, TargetMs / ms);
        else if (ms < TargetMs * 0.75) correction = ms > 0.05 ? Math.Min(MaxRaise, TargetMs / ms) : MaxRaise;
        else correction = 1.0;
        gain *= correction;

        // Capped at 1.0 so the throttle can only ever reduce vanilla's budget.
        if (gain > 1.0) gain = 1.0;
        else if (gain < 0.02) gain = 0.02;
    }

    public static void Reset()
    {
        gain = 1.0;
        PeakMs = 0;
        LastMs = 0;
        Frames = 0;
    }
}
