using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Komet.Measure;

/// <summary>
/// The accounting half of the two allocation attribution patch sets - the client's worker
/// threads and thread-pool callers, the integrated server's threads and the suspects inside
/// them. Both book bytes per named entry from whatever thread ran the code and fold the
/// counters into a smoothed MB/s; only the patch points and the report wording differ, and
/// those stay with the patches.
/// </summary>
public sealed class AllocLedger
{
    public sealed class Entry
    {
        public readonly string Name;

        /// <summary>A whole thread (disjoint from every other thread-level entry) or something
        /// running inside one - a pool caller, a suspect.</summary>
        public readonly bool IsThread;

        internal long bytes, calls, seenBytes;
        public double MbPerSecond;
        public long Bytes => Interlocked.Read(ref bytes);
        public long Calls => Interlocked.Read(ref calls);
        internal Entry(string name, bool isThread) { Name = name; IsThread = isThread; }
    }

    /// <summary>Like FrameStats.SampleGc: catches a flood while it lasts.</summary>
    private const double Alpha = 0.4;

    /// <summary>Below this a row is noise and stays out of the report line.</summary>
    private const double MinReportedRate = 0.5;

    private readonly List<Entry> all = new(24);
    private long lastSampleTs;

    public IReadOnlyList<Entry> Entries => all;

    public Entry Add(string name, bool isThread)
    {
        var e = new Entry(name, isThread);
        lock (all) all.Add(e);
        return e;
    }

    public static void Book(Entry e, long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref e.bytes, bytes);
        Interlocked.Increment(ref e.calls);
    }

    /// <summary>Sum of the thread-level rates - what the client's "rest" subtracts - or of
    /// everything booked inside them.</summary>
    public double Sum(bool isThread)
    {
        double sum = 0;
        lock (all) foreach (var e in all) if (e.IsThread == isThread) sum += e.MbPerSecond;
        return sum;
    }

    /// <summary>Folds the counters into MB/s once <paramref name="minSeconds"/> have passed;
    /// the interval is measured here, not assumed from the caller's cadence.</summary>
    public void MaybeSample(double minSeconds)
    {
        var now = Stopwatch.GetTimestamp();
        if (lastSampleTs == 0) { lastSampleTs = now; return; }
        var dt = (now - lastSampleTs) / (double)Stopwatch.Frequency;
        if (dt < minSeconds) return;
        lastSampleTs = now;
        Sample(dt);
    }

    public void Sample(double dtSeconds)
    {
        lock (all)
        {
            foreach (var e in all)
            {
                var b = Interlocked.Read(ref e.bytes);
                var rate = (b - e.seenBytes) / dtSeconds / 1048576.0;
                e.seenBytes = b;
                e.MbPerSecond += (rate - e.MbPerSecond) * Alpha;
            }
        }
    }

    /// <summary>The entries worth printing, split thread-level / inside, each sorted by rate.</summary>
    public void Split(List<Entry> threads, List<Entry> inside)
    {
        lock (all)
            foreach (var e in all)
                if (e.MbPerSecond >= MinReportedRate) (e.IsThread ? threads : inside).Add(e);
        threads.Sort(ByRate);
        inside.Sort(ByRate);
    }

    private static int ByRate(Entry x, Entry y) => y.MbPerSecond.CompareTo(x.MbPerSecond);

    /// <summary>"tess 12, relight 7" - the shape both report lines use for a group of rows.</summary>
    public static void AppendRates(StringBuilder sb, CultureInfo ci, List<Entry> entries, int max = int.MaxValue)
    {
        var shown = Math.Min(entries.Count, max);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.AppendFormat(ci, "{0} {1:F0}", entries[i].Name, entries[i].MbPerSecond);
        }
        if (entries.Count > shown) sb.Append(", ...");
    }

    public void ResetStats()
    {
        lock (all)
            foreach (var e in all) { Interlocked.Exchange(ref e.bytes, 0); Interlocked.Exchange(ref e.calls, 0); e.seenBytes = 0; }
    }

    /// <summary>The threads are gone: their rates would go stale in the report.</summary>
    public void Clear()
    {
        lock (all) all.Clear();
        lastSampleTs = 0;
    }
}
