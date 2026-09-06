namespace Komet.Measure;

/// <summary>Which side of the frame is the wall.</summary>
public enum Bound
{
    /// <summary>Nothing measured the GPU: MeasureGpuTime is off, or no samples yet.</summary>
    Unknown,
    Gpu,
    Cpu,
    Balanced,
}

/// <summary>
/// The three-way answer to "CPU or GPU?".
///
/// The overlay has always printed a GPU-LIMITED tag, and its rule - <see cref="GpuBusy.IsLimited"/>
/// - is the upper half of this one: the driver's utilisation figure where the OS publishes one,
/// the GL span against the frame where it does not. What was missing is the OTHER end. "Not
/// GPU-limited" is not the same statement as "CPU-limited": a frame that is neither is a frame
/// waiting on something that is not work at all - vsync, the frame limiter, a compositor - and
/// that case has cost this project one wrong conclusion already (36 fps at 7 % CPU with the GPU
/// just over the refresh budget, which is vsync quantising into half-rate frames).
///
/// So this says three things instead of two, and the window prints whichever it is. Pure, with
/// its inputs passed in, so verify can pin the boundaries; <see cref="Current"/> is the same
/// rule reading the live statics.
/// </summary>
public static class FrameVerdict
{
    /// <summary>Below this share of the frame, the GPU is provably not the thing being waited on.</summary>
    public const double CpuBoundBelow = 0.60;

    public static Bound Of(double frameMs, double gpuMs, int gpuBusyPercent)
    {
        if (frameMs <= 0) return Bound.Unknown;

        // The driver's own utilisation when it exists, otherwise the GL span's share of the
        // frame. The span counts the GPU's idle gaps between submissions, which is exactly why
        // the driver figure wins where both are available.
        double busy;
        if (gpuBusyPercent >= 0) busy = gpuBusyPercent / 100.0;
        else if (gpuMs > 0) busy = gpuMs / frameMs;
        else return Bound.Unknown;

        if (busy >= GpuBusy.LimitedAtPercent / 100.0) return Bound.Gpu;
        return busy <= CpuBoundBelow ? Bound.Cpu : Bound.Balanced;
    }

    /// <summary>The same rule on what is being measured right now.</summary>
    public static Bound Current()
        => Of(FrameStats.AvgFrameMs, GpuFrameTimer.GpuMs, GpuBusy.Available ? GpuBusy.Percent : -1);

    /// <summary>The verdict in the words the window prints. Uppercase on purpose: it is the one
    /// line in the overview that decides which of the other pages is worth reading.</summary>
    public static string Text(Bound b) => b switch
    {
        Bound.Gpu => Loc.T("komet:verdict-gpu", "GPU LIMITED"),
        Bound.Cpu => Loc.T("komet:verdict-cpu", "CPU LIMITED"),
        Bound.Balanced => Loc.T("komet:verdict-balanced", "BALANCED"),
        _ => Loc.T("komet:verdict-unknown", "not measured (MeasureGpuTime)"),
    };

    /// <summary>What to do about it - the sentence that turns the verdict into a next step.</summary>
    public static string Advice(Bound b) => b switch
    {
        Bound.Gpu => Loc.T("komet:verdict-gpu-hint", "the GPU page names the passes; shadows are usually the largest"),
        Bound.Cpu => Loc.T("komet:verdict-cpu-hint", "the CPU page splits the main thread by stage"),
        Bound.Balanced => Loc.T("komet:verdict-balanced-hint", "neither is saturated - check vsync and the fps limit"),
        _ => null,
    };
}
