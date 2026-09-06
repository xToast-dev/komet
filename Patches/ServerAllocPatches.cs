using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Komet.Measure;
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

    private static readonly AllocLedger Ledger = new();
    private static readonly Dictionary<MethodBase, AllocLedger.Entry> ByMethod = new();
    private static readonly ConcurrentDictionary<string, AllocLedger.Entry> ByThread = new();
    private static AllocLedger.Entry helperEntry;

    private static readonly AccessTools.FieldRef<ServerThread, string> ThreadNameRef =
        AccessTools.FieldRefAccess<ServerThread, string>("threadName");

    public static IReadOnlyList<AllocLedger.Entry> Entries => Ledger.Entries;

    /// <summary>Sum of the thread-level rates: what the client's "rest" can subtract.</summary>
    public static double ThreadMbPerSecond => Ledger.Sum(true);

    public static void Apply(Harmony harmony)
    {
        var self = typeof(ServerAllocPatches);
        var prefix = new HarmonyMethod(self, nameof(AllocPrefix));
        var postfix = new HarmonyMethod(self, nameof(AllocPostfix));

        void Book(Type type, string method, string name, bool isThread, Type[] args = null)
        {
            var m = (args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args))
                    ?? throw new InvalidOperationException($"{type.Name}.{method} not found");
            ByMethod[m] = Ledger.Add(name, isThread);
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
        helperEntry = Ledger.Add("physik-helper", true);
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

    public static void AllocPrefix(out long __state) => __state = Enabled ? GC.GetAllocatedBytesForCurrentThread() : -1;

    public static void AllocPostfix(long __state, MethodBase __originalMethod)
    {
        if (__state < 0 || !ByMethod.TryGetValue(__originalMethod, out var entry)) return;
        AllocLedger.Book(entry, GC.GetAllocatedBytesForCurrentThread() - __state);
        // the server tick is the clock: about once a second the counters become rates
        if (entry.Name == "tick") Ledger.MaybeSample(1.0);
    }

    public static void ThreadPrefix(ServerThread __instance, out (AllocLedger.Entry entry, long bytes) __state)
    {
        if (!Enabled) { __state = (null, 0); return; }
        var name = ThreadNameRef(__instance) ?? "?";
        var entry = ByThread.GetOrAdd(name, static n => Ledger.Add(ThreadLabel(n), true));
        __state = (entry, GC.GetAllocatedBytesForCurrentThread());
    }

    public static void ThreadPostfix((AllocLedger.Entry entry, long bytes) __state)
    {
        if (__state.entry == null) return;
        AllocLedger.Book(__state.entry, GC.GetAllocatedBytesForCurrentThread() - __state.bytes);
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
            AllocLedger.Book(helperEntry, GC.GetAllocatedBytesForCurrentThread() - b0);
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

    internal static void Sample(double dtSeconds) => Ledger.Sample(dtSeconds);

    /// <summary>One report line: threads by rate, then the suspects inside them.</summary>
    public static void Write(StringBuilder sb, CultureInfo ci)
    {
        List<AllocLedger.Entry> threads = new(), subs = new();
        Ledger.Split(threads, subs);
        sb.Append("  alloc server: ");
        if (threads.Count == 0) sb.Append("unter 0,5 MB/s je thread");
        AllocLedger.AppendRates(sb, ci, threads);
        if (threads.Count > 0) sb.Append(" MB/s");
        if (subs.Count > 0)
        {
            sb.Append(" | davon ");
            AllocLedger.AppendRates(sb, ci, subs);
        }
        if (!Enabled) sb.Append(" (OFF)");
        sb.Append('\n');
    }

    public static void ResetStats() => Ledger.ResetStats();

    /// <summary>Server shutdown: rates go stale, thread entries belong to threads that are gone.</summary>
    public static void Clear()
    {
        Ledger.Clear();
        ByMethod.Clear();
        ByThread.Clear();
        helperEntry = null;
    }
}
