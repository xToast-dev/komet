using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Vintagestory.Server;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Names the integrated server's allocation - the "rest" of the client's alloc-quellen line.
///
/// Every gen0 collection pauses the render thread, whoever allocated. The 02.09. join-flood
/// reports measured 279 MB/s with 193 of it unattributed ("rest = ungemessen, v.a.
/// integrierter server"), 35 gen0 collections per second and 52 of 54 hitches sitting on a
/// GC pause: the biggest remaining hitch lever lives on the server side of the process, and
/// it has no name. This measures it per thread and per suspect, with the same tool the client
/// side uses - GC.GetAllocatedBytesForCurrentThread around the work - so the next report says
/// "worldgen 120 MB/s" or "send-chunks 60 MB/s" instead of "rest".
///
/// Thread-level entries (disjoint, each its own thread): the main server tick
/// (ServerMain.Process), every ServerThread loop by its name (chunkdbthread, CompressChunks,
/// Relight, ServerBlockTicks), the additional worldgen threads
/// (GenerateChunkColumns_OnSeparateThread), the physics workers (PhysicsManager.DoWork) and
/// the physics helper (its queued actions, wrapped). Sub-entries are suspects INSIDE those:
/// chunk packet building, entity simulation, the physics tick, main-thread tasks, the
/// worldgen passes, chunk loading from the database, and moving a finished column into the
/// world. Bytes are attributed by the thread that ran the code, so a sub-entry is always
/// contained in exactly one thread-level total.
///
/// Rates are sampled once a second from the server tick; the counters are Interlocked
/// since every thread books its own.
/// </summary>
public static class ServerAllocPatches
{
    public static bool Enabled = true;

    public sealed class Entry
    {
        public readonly string Name;
        /// <summary>A whole thread (disjoint from every other thread-level entry) or a
        /// suspect inside one.</summary>
        public readonly bool IsThread;
        internal long bytes, calls, seenBytes;
        public double MbPerSecond;
        public long Bytes => Interlocked.Read(ref bytes);
        public long Calls => Interlocked.Read(ref calls);
        internal Entry(string name, bool isThread) { Name = name; IsThread = isThread; }
    }

    private static readonly List<Entry> All = new(24);
    private static readonly Dictionary<MethodBase, Entry> ByMethod = new();
    private static readonly ConcurrentDictionary<string, Entry> ByThread = new();
    private static Entry helperEntry;
    private static long lastSampleTs;
    private const double Alpha = 0.4; // like FrameStats.SampleGc: catches a flood while it lasts

    private static readonly AccessTools.FieldRef<ServerThread, string> ThreadNameRef =
        AccessTools.FieldRefAccess<ServerThread, string>("threadName");

    public static IReadOnlyList<Entry> Entries => All;

    /// <summary>Sum of the thread-level rates: what the client's "rest" can subtract.</summary>
    public static double ThreadMbPerSecond
    {
        get
        {
            double sum = 0;
            foreach (var e in All) if (e.IsThread) sum += e.MbPerSecond;
            return sum;
        }
    }

    public static void Apply(Harmony harmony)
    {
        var self = typeof(ServerAllocPatches);
        var prefix = new HarmonyMethod(self, nameof(AllocPrefix));
        var postfix = new HarmonyMethod(self, nameof(AllocPostfix));

        void Book(Type type, string method, string name, bool isThread, Type[] args = null)
        {
            var m = (args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args))
                    ?? throw new InvalidOperationException($"{type.Name}.{method} not found");
            var entry = Add(name, isThread);
            ByMethod[m] = entry;
            harmony.Patch(m, prefix: prefix, postfix: postfix);
        }

        Type Named(string typeName) => AccessTools.TypeByName(typeName)
                                       ?? throw new InvalidOperationException(typeName + " not found");

        var supply = Named("Vintagestory.Server.ServerSystemSupplyChunks");

        // thread-level
        Book(typeof(ServerMain), nameof(ServerMain.Process), "tick", true);
        Book(supply, "GenerateChunkColumns_OnSeparateThread", "worldgen", true);
        Book(typeof(PhysicsManager), nameof(PhysicsManager.DoWork), "physik-worker", true, [typeof(int)]);
        harmony.Patch(AccessTools.Method(typeof(ServerThread), nameof(ServerThread.Update))
                      ?? throw new InvalidOperationException("ServerThread.Update not found"),
            prefix: new HarmonyMethod(self, nameof(ThreadPrefix)),
            postfix: new HarmonyMethod(self, nameof(ThreadPostfix)));
        var helper = AccessTools.Inner(typeof(PhysicsManager), "PhysicsOffthreadTasks")
                     ?? throw new InvalidOperationException("PhysicsManager.PhysicsOffthreadTasks not found");
        helperEntry = Add("physik-helper", true);
        harmony.Patch(AccessTools.Method(helper, "QueueAsyncTask")
                      ?? throw new InvalidOperationException("PhysicsOffthreadTasks.QueueAsyncTask not found"),
            prefix: new HarmonyMethod(self, nameof(HelperPrefix)));

        // suspects inside those threads
        Book(Named("Vintagestory.Server.ServerSystemSendChunks"), "OnServerTick", "send-chunks", false);
        Book(Named("Vintagestory.Server.ServerSystemEntitySimulation"), "OnServerTick", "entities", false);
        Book(typeof(PhysicsManager), nameof(PhysicsManager.ServerTick), "physik-tick", false, [typeof(float)]);
        Book(typeof(ServerMain), nameof(ServerMain.ProcessMainThreadTasks), "mt-tasks", false);
        Book(supply, "runGenerators", "worldgen-passes", false);
        Book(supply, "TryLoadChunkColumn", "db-laden", false);
        Book(supply, "mainThreadLoadChunkColumn", "chunk-einbau", false);
    }

    private static Entry Add(string name, bool isThread)
    {
        var e = new Entry(name, isThread);
        lock (All) All.Add(e);
        return e;
    }

    public static void AllocPrefix(out long __state) => __state = Enabled ? GC.GetAllocatedBytesForCurrentThread() : -1;

    public static void AllocPostfix(long __state, MethodBase __originalMethod)
    {
        if (__state < 0 || !ByMethod.TryGetValue(__originalMethod, out var entry)) return;
        Book(entry, GC.GetAllocatedBytesForCurrentThread() - __state);
        if (entry.Name == "tick") MaybeSample();
    }

    public static void ThreadPrefix(ServerThread __instance, out (Entry entry, long bytes) __state)
    {
        if (!Enabled) { __state = (null, 0); return; }
        var name = ThreadNameRef(__instance) ?? "?";
        var entry = ByThread.GetOrAdd(name, static n => Add(ThreadLabel(n), true));
        __state = (entry, GC.GetAllocatedBytesForCurrentThread());
    }

    public static void ThreadPostfix((Entry entry, long bytes) __state)
    {
        if (__state.entry == null) return;
        Book(__state.entry, GC.GetAllocatedBytesForCurrentThread() - __state.bytes);
    }

    /// <summary>The helper runs queued actions on its own thread; the action is wrapped
    /// so the bytes land on that thread's entry. Off = the action goes through as is.</summary>
    public static void HelperPrefix(ref Action a)
    {
        if (!Enabled || a == null) return;
        var inner = a;
        a = () =>
        {
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            inner();
            Book(helperEntry, GC.GetAllocatedBytesForCurrentThread() - b0);
        };
    }

    internal static string ThreadLabel(string threadName) => threadName switch
    {
        "chunkdbthread" => "chunkdb",
        "CompressChunks" => "compress",
        "Relight" => "relight",
        "ServerBlockTicks" => "blockticks",
        _ => threadName.ToLowerInvariant(),
    };

    private static void Book(Entry e, long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref e.bytes, bytes);
        Interlocked.Increment(ref e.calls);
    }

    /// <summary>Folds the counters into MB/s about once a second (from the server tick).</summary>
    private static void MaybeSample()
    {
        var now = Stopwatch.GetTimestamp();
        if (lastSampleTs == 0) { lastSampleTs = now; return; }
        var dt = (now - lastSampleTs) / (double)Stopwatch.Frequency;
        if (dt < 1.0) return;
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

    /// <summary>One report line: threads by rate, then the suspects inside them.</summary>
    public static void Write(StringBuilder sb, System.Globalization.CultureInfo ci)
    {
        List<Entry> threads = new(), subs = new();
        lock (All)
        {
            foreach (var e in All)
                if (e.MbPerSecond >= 0.5) (e.IsThread ? threads : subs).Add(e);
        }
        threads.Sort((x, y) => y.MbPerSecond.CompareTo(x.MbPerSecond));
        subs.Sort((x, y) => y.MbPerSecond.CompareTo(x.MbPerSecond));
        sb.Append("  alloc server: ");
        if (threads.Count == 0) sb.Append("unter 0,5 MB/s je thread");
        for (var i = 0; i < threads.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.AppendFormat(ci, "{0} {1:F0}", threads[i].Name, threads[i].MbPerSecond);
        }
        if (threads.Count > 0) sb.Append(" MB/s");
        if (subs.Count > 0)
        {
            sb.Append(" | davon ");
            for (var i = 0; i < subs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(ci, "{0} {1:F0}", subs[i].Name, subs[i].MbPerSecond);
            }
        }
        if (!Enabled) sb.Append(" (AUS)");
        sb.Append('\n');
    }

    public static void ResetStats()
    {
        lock (All)
            foreach (var e in All) { Interlocked.Exchange(ref e.bytes, 0); Interlocked.Exchange(ref e.calls, 0); e.seenBytes = 0; }
    }

    /// <summary>Server shutdown: rates go stale, thread entries belong to threads that are gone.</summary>
    public static void Clear()
    {
        lock (All) All.Clear();
        ByMethod.Clear();
        ByThread.Clear();
        helperEntry = null;
        lastSampleTs = 0;
    }
}
