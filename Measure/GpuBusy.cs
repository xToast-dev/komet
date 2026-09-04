using System;
using System.IO;

namespace Komet.Measure;

/// <summary>
/// The GPU's real utilisation, next to the GPU frame time - because the two answer different
/// questions and were read as the same one.
///
/// <see cref="GpuFrameTimer"/> measures GL_TIME_ELAPSED between the frame's first and last
/// command: the span of the GPU frame, INCLUDING every gap in which the GPU sat idle waiting
/// for the main thread to submit more. A CPU-bound frame that dribbles commands over 10 ms
/// therefore reads as "gpu 10 ms" just like a GPU-bound one that computes for 10 ms. The
/// 03.09. report had gpu 10,20 ms in a 10,93 ms frame, and the user read it as a GPU running
/// flat out after the last update - it was the same load as before, only visible for the
/// first time (the old timer had frozen at a load-screen value).
///
/// Utilisation is what the driver counts as busy, and on Linux the amdgpu driver publishes
/// it in sysfs: <c>/sys/class/drm/cardN/device/gpu_busy_percent</c>, one integer, refreshed
/// by the driver. Reading a sysfs file twice a second costs microseconds and no GL call, so
/// no driver sync. Intel and NVIDIA publish nothing comparable there, and Windows has no
/// file to read; the row simply stays absent instead of guessing.
///
/// Rule for the HUD's "GPU-LIMITIERT" tag: with a utilisation figure, that figure decides
/// (90 % and above); without one, the old comparison of GPU span against frame time stands
/// as the weaker signal it is.
/// </summary>
public static class GpuBusy
{
    /// <summary>Last reading, 0..100, or -1 before the first.</summary>
    public static int Percent { get; private set; } = -1;

    /// <summary>Where the figure comes from ("amdgpu"), null when nothing is readable.</summary>
    public static string Source { get; private set; }

    public static bool Available => Percent >= 0 && Source != null;

    /// <summary>Readings taken since start - the proof the figure is live.</summary>
    public static long Samples { get; private set; }

    /// <summary>Utilisation at which the GPU counts as the wall.</summary>
    public const int LimitedAtPercent = 90;

    internal const string DefaultDrmRoot = "/sys/class/drm";
    /// <summary>Where the cards live; the harness points it at a directory of its own.</summary>
    internal static string DrmRoot = DefaultDrmRoot;

    private static string path;
    private static bool probed;
    private static int failures;

    /// <summary>Twice a second, from FrameStats.PeriodicSample. Never throws.</summary>
    public static void Sample()
    {
        if (!probed)
        {
            probed = true;
            path = OperatingSystem.IsLinux() || DrmRoot != DefaultDrmRoot ? Probe(DrmRoot) : null;
            Source = path != null ? "amdgpu" : null;
        }
        if (path == null) return;
        try
        {
            if (TryParse(File.ReadAllText(path), out var p))
            {
                Percent = p;
                Samples++;
                failures = 0;
            }
            else if (++failures >= 3) Disable();
        }
        catch (Exception)
        {
            if (++failures >= 3) Disable();
        }
    }

    private static void Disable()
    {
        path = null;
        Source = null;
        Percent = -1;
    }

    /// <summary>
    /// The first card under <paramref name="drmRoot"/> whose driver publishes a busy figure.
    /// A machine with an integrated and a discrete GPU has two cards; the one running the
    /// game is not knowable from here, and the first readable one is the honest default -
    /// the HUD names the source so a wrong pick is at least a visible one.
    /// </summary>
    internal static string Probe(string drmRoot)
    {
        try
        {
            if (!Directory.Exists(drmRoot)) return null;
            var cards = Directory.GetDirectories(drmRoot, "card*");
            Array.Sort(cards, StringComparer.Ordinal);
            foreach (var card in cards)
            {
                // "card1" yes, "card1-DP-1" (a connector) no
                var name = Path.GetFileName(card);
                if (name.Length < 5 || !char.IsDigit(name[4]) || name.IndexOf('-') >= 0) continue;
                var candidate = Path.Combine(card, "device", "gpu_busy_percent");
                if (!File.Exists(candidate)) continue;
                if (TryParse(File.ReadAllText(candidate), out _)) return candidate;
            }
        }
        catch (Exception) { /* sysfs is best effort */ }
        return null;
    }

    /// <summary>"13\n" -> 13. Rejects anything outside 0..100.</summary>
    internal static bool TryParse(string text, out int percent)
    {
        percent = -1;
        if (text == null) return false;
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return false;
        if (p < 0 || p > 100) return false;
        percent = p;
        return true;
    }

    /// <summary>The HUD's tag rule, pure: utilisation when known, else span against frame.</summary>
    public static bool IsLimited(double gpuMs, double frameMs)
        => Available ? Percent >= LimitedAtPercent : gpuMs > 0 && frameMs > 0 && gpuMs >= frameMs * 0.95;

    public static void Reset()
    {
        Samples = 0;
    }

    /// <summary>For the harness: forget the probe so the next Sample probes again.</summary>
    internal static void ForgetProbe()
    {
        probed = false;
        path = null;
        Source = null;
        Percent = -1;
        failures = 0;
    }
}
