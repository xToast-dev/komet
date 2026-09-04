using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Names the client's worker threads' allocation - what was left of "rest" once the server
/// had its own line.
///
/// The 03.09. report: 216 MB/s allocated, 27 gen0 collections a second, 384 of 402 hitches
/// on a GC pause - and 79 MB/s of it still "rest = ungemessen". The candidates are the eight
/// engine worker threads ClientMain starts (compresschunks, blockticking, relight,
/// tesselateterrain, chunkvis, networkproc, chunkculling, asyncparticles) and everything the
/// engine queues on TyronThreadPool (entity shape meshing, map pieces, ...). Same tool as the
/// server side: GC.GetAllocatedBytesForCurrentThread around the work, booked by name.
///
/// Thread level: one bracket around ClientThread.Update, keyed by the thread's name - every
/// system tick of that thread lands in it, so the entries are disjoint and complete for the
/// worker threads. Pool level: the callback handed to TyronThreadPool.QueueTask is wrapped
/// so the bytes land on the caller's name, whichever pool thread runs it. The two existing
/// brackets (meshing inside TesselateChunk, the network system tick) stay where they are and
/// keep feeding the "laden:" line; the thread-level "tess" and "netz" here contain them.
/// </summary>
public static class ClientAllocPatches
{
    public static bool Enabled = true;

    public sealed class Entry
    {
        public readonly string Name;
        /// <summary>A worker thread (disjoint from every other thread entry) or a pool caller.</summary>
        public readonly bool IsThread;
        internal long bytes, calls, seenBytes;
        public double MbPerSecond;
        public long Bytes => Interlocked.Read(ref bytes);
        public long Calls => Interlocked.Read(ref calls);
        internal Entry(string name, bool isThread) { Name = name; IsThread = isThread; }
    }

    private static readonly List<Entry> All = new(24);
    private static readonly ConcurrentDictionary<string, Entry> ByThread = new();
    private static readonly ConcurrentDictionary<string, Entry> ByCaller = new();
    private const int MaxCallers = 48;
    private const string Overflow = "(andere)";
    private static long lastSampleTs;
    private const double Alpha = 0.4;

    private static AccessTools.FieldRef<object, string> threadNameRef;

    public static IReadOnlyList<Entry> Entries => All;

    /// <summary>Sum of the worker thread rates - what the client's "rest" subtracts.</summary>
    public static double ThreadMbPerSecond
    {
        get
        {
            double sum = 0;
            lock (All) foreach (var e in All) if (e.IsThread) sum += e.MbPerSecond;
            return sum;
        }
    }

    /// <summary>Sum of the pool caller rates.</summary>
    public static double PoolMbPerSecond
    {
        get
        {
            double sum = 0;
            lock (All) foreach (var e in All) if (!e.IsThread) sum += e.MbPerSecond;
            return sum;
        }
    }

    public static void Apply(Harmony harmony)
    {
        var self = typeof(ClientAllocPatches);
        var clientThread = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientThread")
                           ?? throw new InvalidOperationException("ClientThread not found");
        threadNameRef = AccessTools.FieldRefAccess<string>(clientThread, "threadName")
                        ?? throw new InvalidOperationException("ClientThread.threadName not found");
        harmony.Patch(AccessTools.Method(clientThread, "Update")
                      ?? throw new InvalidOperationException("ClientThread.Update not found"),
            prefix: new HarmonyMethod(self, nameof(ThreadPrefix)),
            postfix: new HarmonyMethod(self, nameof(ThreadPostfix)));

        // the obsolete overloads delegate to this one, so one prefix sees every queued action;
        // the Func<Task> overload is not wrapped - after an await the continuation may run on
        // another thread, and a per-thread counter cannot follow it
        harmony.Patch(AccessTools.Method(typeof(TyronThreadPool), nameof(TyronThreadPool.QueueTask),
                          [typeof(Action), typeof(string)])
                      ?? throw new InvalidOperationException("TyronThreadPool.QueueTask(Action, string) not found"),
            prefix: new HarmonyMethod(self, nameof(QueuePrefix)));
    }

    public static void ThreadPrefix(object __instance, out (Entry entry, long bytes) __state)
    {
        if (!Enabled) { __state = (null, 0); return; }
        var name = threadNameRef(__instance) ?? "?";
        var entry = ByThread.GetOrAdd(name, static n => Add(ThreadLabel(n), true));
        __state = (entry, GC.GetAllocatedBytesForCurrentThread());
    }

    public static void ThreadPostfix((Entry entry, long bytes) __state)
    {
        if (__state.entry == null) return;
        Book(__state.entry, GC.GetAllocatedBytesForCurrentThread() - __state.bytes);
    }

    /// <summary>Wraps the queued action so its bytes land on the caller's entry. Off = the
    /// action goes through untouched. One closure per task - a few dozen bytes against the
    /// megabytes the task is there to measure.</summary>
    public static void QueuePrefix(ref Action callback, string caller)
    {
        if (!Enabled || callback == null) return;
        var entry = PoolEntry(caller);
        var inner = callback;
        callback = () =>
        {
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            try { inner(); }
            finally { Book(entry, GC.GetAllocatedBytesForCurrentThread() - b0); }
        };
    }

    internal static Entry PoolEntry(string caller)
    {
        caller ??= "?";
        if (ByCaller.TryGetValue(caller, out var e)) return e;
        if (ByCaller.Count >= MaxCallers) return ByCaller.GetOrAdd(Overflow, static n => Add("pool " + n, false));
        return ByCaller.GetOrAdd(caller, static n => Add("pool " + n, false));
    }

    internal static string ThreadLabel(string threadName) => threadName switch
    {
        "tesselateterrain" => "tess",
        "networkproc" => "netz",
        "compresschunks" => "compress",
        "chunkculling" => "culling",
        "blockticking" => "blockticks",
        "asyncparticles" => "partikel",
        _ => threadName.ToLowerInvariant(),
    };

    private static Entry Add(string name, bool isThread)
    {
        var e = new Entry(name, isThread);
        lock (All) All.Add(e);
        return e;
    }

    private static void Book(Entry e, long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref e.bytes, bytes);
        Interlocked.Increment(ref e.calls);
    }

    /// <summary>Folds the counters into MB/s. Hooked on FrameStats.PeriodicSample (render
    /// thread, every half second); the interval is measured here, not assumed.</summary>
    public static void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        if (lastSampleTs == 0) { lastSampleTs = now; return; }
        var dt = (now - lastSampleTs) / (double)Stopwatch.Frequency;
        if (dt < 0.2) return;
        lastSampleTs = now;
        Sample(dt);
    }

    internal static void Sample(double dtSeconds)
    {
        lock (All)
        {
            foreach (var e in All)
            {
                var b = Interlocked.Read(ref e.bytes);
                var rate = (b - e.seenBytes) / dtSeconds / 1048576.0;
                e.seenBytes = b;
                e.MbPerSecond += (rate - e.MbPerSecond) * Alpha;
            }
        }
    }

    /// <summary>One report line: worker threads by rate, then the pool callers.</summary>
    public static void Write(StringBuilder sb, System.Globalization.CultureInfo ci)
    {
        List<Entry> threads = new(), pool = new();
        lock (All)
        {
            foreach (var e in All)
                if (e.MbPerSecond >= 0.5) (e.IsThread ? threads : pool).Add(e);
        }
        threads.Sort((x, y) => y.MbPerSecond.CompareTo(x.MbPerSecond));
        pool.Sort((x, y) => y.MbPerSecond.CompareTo(x.MbPerSecond));
        sb.Append("  alloc client-threads: ");
        if (threads.Count == 0) sb.Append("unter 0,5 MB/s je thread");
        for (var i = 0; i < threads.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.AppendFormat(ci, "{0} {1:F0}", threads[i].Name, threads[i].MbPerSecond);
        }
        if (threads.Count > 0) sb.Append(" MB/s");
        if (pool.Count > 0)
        {
            sb.AppendFormat(ci, " | threadpool {0:F0} MB/s: ", PoolMbPerSecond);
            var shown = Math.Min(pool.Count, 6);
            for (var i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(ci, "{0} {1:F0}", pool[i].Name.Substring(5), pool[i].MbPerSecond);
            }
            if (pool.Count > shown) sb.Append(", ...");
        }
        if (!Enabled) sb.Append(" (AUS)");
        sb.Append('\n');
    }

    public static void ResetStats()
    {
        lock (All)
            foreach (var e in All) { Interlocked.Exchange(ref e.bytes, 0); Interlocked.Exchange(ref e.calls, 0); e.seenBytes = 0; }
    }

    /// <summary>World left: the threads are gone, their rates would go stale in the report.</summary>
    public static void Clear()
    {
        lock (All) All.Clear();
        ByThread.Clear();
        ByCaller.Clear();
        lastSampleTs = 0;
    }
}
