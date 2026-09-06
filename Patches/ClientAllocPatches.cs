using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Komet.Measure;
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

    private static readonly AllocLedger Ledger = new();
    private static readonly ConcurrentDictionary<string, AllocLedger.Entry> ByThread = new();
    private static readonly ConcurrentDictionary<string, AllocLedger.Entry> ByCaller = new();
    private const int MaxCallers = 48;
    private const string Overflow = "(andere)";

    private static AccessTools.FieldRef<object, string> threadNameRef;

    public static IReadOnlyList<AllocLedger.Entry> Entries => Ledger.Entries;

    /// <summary>Sum of the worker thread rates - what the client's "rest" subtracts.</summary>
    public static double ThreadMbPerSecond => Ledger.Sum(true);

    /// <summary>Sum of the pool caller rates.</summary>
    public static double PoolMbPerSecond => Ledger.Sum(false);

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

    public static void ThreadPrefix(object __instance, out (AllocLedger.Entry entry, long bytes) __state)
    {
        if (!Enabled) { __state = (null, 0); return; }
        var name = threadNameRef(__instance) ?? "?";
        var entry = ByThread.GetOrAdd(name, static n => Ledger.Add(ThreadLabel(n), true));
        __state = (entry, GC.GetAllocatedBytesForCurrentThread());
    }

    public static void ThreadPostfix((AllocLedger.Entry entry, long bytes) __state)
    {
        if (__state.entry == null) return;
        AllocLedger.Book(__state.entry, GC.GetAllocatedBytesForCurrentThread() - __state.bytes);
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
            finally { AllocLedger.Book(entry, GC.GetAllocatedBytesForCurrentThread() - b0); }
        };
    }

    internal static AllocLedger.Entry PoolEntry(string caller)
    {
        caller ??= "?";
        if (ByCaller.TryGetValue(caller, out var e)) return e;
        if (ByCaller.Count >= MaxCallers) return ByCaller.GetOrAdd(Overflow, static n => Ledger.Add(n, false));
        return ByCaller.GetOrAdd(caller, static n => Ledger.Add(n, false));
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

    /// <summary>Folds the counters into MB/s. Hooked on FrameStats.PeriodicSample (render
    /// thread, every half second).</summary>
    public static void Sample() => Ledger.MaybeSample(0.2);

    internal static void Sample(double dtSeconds) => Ledger.Sample(dtSeconds);

    /// <summary>One report line: worker threads by rate, then the pool callers.</summary>
    public static void Write(StringBuilder sb, CultureInfo ci)
    {
        List<AllocLedger.Entry> threads = new(), pool = new();
        Ledger.Split(threads, pool);
        sb.Append("  alloc client-threads: ");
        if (threads.Count == 0) sb.Append("unter 0,5 MB/s je thread");
        AllocLedger.AppendRates(sb, ci, threads);
        if (threads.Count > 0) sb.Append(" MB/s");
        if (pool.Count > 0)
        {
            sb.AppendFormat(ci, " | threadpool {0:F0} MB/s: ", PoolMbPerSecond);
            AllocLedger.AppendRates(sb, ci, pool, 6);
        }
        if (!Enabled) sb.Append(" (OFF)");
        sb.Append('\n');
    }

    public static void ResetStats() => Ledger.ResetStats();

    /// <summary>World left: the threads are gone, their rates would go stale in the report.</summary>
    public static void Clear()
    {
        Ledger.Clear();
        ByThread.Clear();
        ByCaller.Clear();
    }
}
