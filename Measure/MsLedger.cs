using System.Collections.Generic;
using System.Diagnostics;

namespace Komet.Measure;

/// <summary>
/// Per-name ms-per-frame accounting, shared by the two frame profilers that keep one - tick
/// listeners (TickProfiler) and main-thread task codes (MainThreadTaskPatches).
///
/// Ticks accumulate on whatever ran during the frame; <see cref="EndFrame"/> folds them into a
/// smoothed per-frame figure and zeroes them again. It runs every frame, whether a name fired
/// or not: the figure is "ms per frame", so a quiet frame has to count as a real zero rather
/// than leave the last busy frame's average standing.
/// </summary>
internal sealed class MsLedger
{
    internal sealed class Entry
    {
        public long Ticks;      // accumulated in the current frame
        public double Ms;       // smoothed per frame
        public long Calls;
    }

    internal static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>~1/64 per frame: slow enough that a single frame cannot move a row, fast
    /// enough that a scene change shows up within a second.</summary>
    private const double Alpha = 1.0 / 64.0;

    /// <summary>Below this a row is rounding noise in the report.</summary>
    private const double MinReportedMs = 0.005;

    private readonly Dictionary<string, Entry> entries = new(64);

    internal int Count => entries.Count;

    internal Entry Bucket(string name)
    {
        if (!entries.TryGetValue(name, out var e)) entries[name] = e = new Entry();
        return e;
    }

    /// <summary>
    /// The bucket for <paramref name="name"/>, or a shared overflow one once the vocabulary is
    /// wider than expected - a guard against a mod that generates a unique name per call.
    /// </summary>
    internal Entry Bucket(string name, int maxEntries, string overflow)
        => entries.TryGetValue(name, out var e) ? e
         : Bucket(entries.Count >= maxEntries ? overflow : name);

    internal void EndFrame()
    {
        foreach (var kv in entries)
        {
            var e = kv.Value;
            e.Ms += (e.Ticks * TicksToMs - e.Ms) * Alpha;
            e.Ticks = 0;
        }
    }

    /// <summary>The heaviest names by smoothed ms per frame, with their call totals.</summary>
    internal List<(string name, double ms, long calls)> Top(int count)
    {
        var all = new List<(string, double, long)>(entries.Count);
        foreach (var kv in entries)
            if (kv.Value.Ms > MinReportedMs) all.Add((kv.Key, kv.Value.Ms, kv.Value.Calls));
        all.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (all.Count > count) all.RemoveRange(count, all.Count - count);
        return all;
    }

    /// <summary>The current frame's most expensive name, raw ticks - valid only between the
    /// frame boundary's hitch detection and <see cref="EndFrame"/>.</summary>
    internal (string name, double ms)? TopOfCurrentFrame()
    {
        string bestName = null;
        long best = 0;
        foreach (var kv in entries)
        {
            if (kv.Value.Ticks > best)
            {
                best = kv.Value.Ticks;
                bestName = kv.Key;
            }
        }
        return bestName == null ? null : (bestName, best * TicksToMs);
    }

    internal double TotalMs
    {
        get
        {
            double sum = 0;
            foreach (var kv in entries) sum += kv.Value.Ms;
            return sum;
        }
    }

    internal void Reset()
    {
        foreach (var kv in entries) { kv.Value.Ticks = 0; kv.Value.Ms = 0; kv.Value.Calls = 0; }
    }
}
