using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Replaces the engine's MeshDataRecycler storage with a size-class pool behind the same API.
///
/// Why: the hitch profile while chunks stream in is dominated by GC pauses, fed by a measured
/// 382 MB/s of tesselation-thread allocation (1.47 report; ~1,3 MB per chunk). The tesselator
/// already routes every chunk mesh through MeshData.CloneUsingRecycler, so that money can only
/// be lost inside the recycler - and its storage loses it two ways, both worst under exactly
/// the streaming pattern (bursts of similar sizes, sizes drifting as terrain changes):
///
///   1. SortedList storage allows ONE mesh per size key. TryAdd probes a handful of fractional
///      keys (+0.25 steps) and then THROWS THE BUFFER AWAY - the fifth same-size mesh of a
///      burst is disposed even when the pool is nearly empty.
///   2. TryGet only accepts an entry within [size, size*1.25+64] - a near miss allocates a
///      fresh ~34 bytes/vertex set even when a slightly larger buffer sits right there.
///
/// The replacement files buffers into geometric size classes (x1.25), each class an unbounded
/// LIFO list, evicted oldest-first by a byte budget and the same 15 s TTL as vanilla. A get
/// rounds up to the class, so after warmup every request of a recurring magnitude hits. The
/// trade is slack: a served buffer may be up to ~1.56x the request (class rounding, one class
/// up), where vanilla overshoots at most 1.25x - bytes held for allocation-rate, bounded by
/// <see cref="BudgetMb"/>.
///
/// Mesh CONTENT is untouched: CloneUsingRecycler copies counts and data into whatever buffer
/// comes back, and every path here preserves the engine contract (VerticesMax >= request,
/// IndicesMax == VerticesMax*6/4, Recyclable set, arrays may hold junk).
///
/// Threading mirrors vanilla's design: Recycle may be called from any thread (concurrent
/// queue); GetOrCreateMesh/DoRecycling run on the tesselation thread. One lock keeps the
/// runtime toggle safe; contention is a few hundred operations per second.
/// </summary>
public static class MeshRecyclerPatches
{
    public static bool Enabled;

    /// <summary>Upper bound on bytes held for reuse. Vanilla's three lists cap out around
    /// 300-470 MB (its own doc says 300-400), so this is not new memory - it is the same
    /// reserve, actually enforced.</summary>
    public static int BudgetMb = 384;

    /// <summary>Same idle lifetime as vanilla's TTL constant.</summary>
    public const int TtlMs = 15000;

    /// <summary>xyz 12 + uv 8 + rgba 4 + flags 4 + indices 6 bytes per vertex - the same
    /// arithmetic as the engine's MinimumSizeForRecycling check (34 * vertices).</summary>
    internal const int BytesPerVertex = 34;

    /// <summary>Injectable for the verify tests; the game runs on TickCount64.</summary>
    internal static Func<long> Clock = () => Environment.TickCount64;

    public static long StatHits;
    public static long StatMisses;
    /// <summary>Bytes freshly allocated because no pooled buffer fit - the number that should
    /// collapse once the pool is warm, and the direct feed into the GC pressure.</summary>
    public static long StatMissBytes;
    public static long StatEvicted;
    public static long HeldBytes;

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<MeshData> Incoming = new();
    private static int[] classSizes;
    private static List<MeshData>[] classes;
    private static long lastSweepMs;
    private static bool drainVanillaOnce;

    private static readonly AccessTools.FieldRef<MeshDataRecycler, SortedList<float, MeshData>> SmallRef =
        AccessTools.FieldRefAccess<MeshDataRecycler, SortedList<float, MeshData>>("smallSizes");
    private static readonly AccessTools.FieldRef<MeshDataRecycler, SortedList<float, MeshData>> MediumRef =
        AccessTools.FieldRefAccess<MeshDataRecycler, SortedList<float, MeshData>>("mediumSizes");
    private static readonly AccessTools.FieldRef<MeshDataRecycler, SortedList<float, MeshData>> LargeRef =
        AccessTools.FieldRefAccess<MeshDataRecycler, SortedList<float, MeshData>>("largeSizes");
    private static readonly AccessTools.FieldRef<MeshDataRecycler, ConcurrentQueue<MeshData>> QueueRef =
        AccessTools.FieldRefAccess<MeshDataRecycler, ConcurrentQueue<MeshData>>("forRecycling");

    public static void Apply(Harmony harmony)
    {
        EnsureClasses();
        var t = typeof(MeshDataRecycler);
        harmony.Patch(AccessTools.Method(t, nameof(MeshDataRecycler.GetOrCreateMesh)),
            prefix: new HarmonyMethod(typeof(MeshRecyclerPatches), nameof(GetPrefix)));
        harmony.Patch(AccessTools.Method(t, nameof(MeshDataRecycler.Recycle)),
            prefix: new HarmonyMethod(typeof(MeshRecyclerPatches), nameof(RecyclePrefix)));
        harmony.Patch(AccessTools.Method(t, nameof(MeshDataRecycler.DoRecycling)),
            prefix: new HarmonyMethod(typeof(MeshRecyclerPatches), nameof(DoRecyclingPrefix)));
    }

    /// <summary>
    /// The runtime gate. Enabling schedules a one-shot takeover of whatever vanilla's lists
    /// hold - executed on the tesselation thread inside DoRecycling, the only thread that
    /// touches those lists, so no lock ever has to reach into engine state. Disabling frees
    /// everything held; vanilla starts fresh from its own empty lists.
    /// </summary>
    public static void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        if (on)
        {
            drainVanillaOnce = true;
            Enabled = true;
        }
        else
        {
            Enabled = false;
            Clear();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            while (Incoming.TryDequeue(out var m)) m.DisposeBasicData();
            if (classes != null)
            {
                foreach (var list in classes)
                {
                    foreach (var m in list) m.DisposeBasicData();
                    list.Clear();
                }
            }
            HeldBytes = 0;
        }
    }

    public static void ResetStats()
    {
        StatHits = StatMisses = StatMissBytes = StatEvicted = 0;
    }

    internal static bool GetPrefix(int minimumVertices, ref MeshData __result)
    {
        if (!Enabled) return true;
        __result = Get(minimumVertices);
        return false;
    }

    internal static bool RecyclePrefix(MeshData meshData)
    {
        if (!Enabled) return true;
        Incoming.Enqueue(meshData);
        return false;
    }

    internal static bool DoRecyclingPrefix(MeshDataRecycler __instance)
    {
        if (!Enabled) return true;
        if (drainVanillaOnce)
        {
            drainVanillaOnce = false;
            DrainVanilla(__instance);
        }
        Drain();
        return false;
    }

    private static MeshData Get(int minimumVertices)
    {
        // the engine contract rounds requests to a multiple of 4 (whole faces)
        minimumVertices = (minimumVertices + 3) / 4 * 4;
        var c = ClassFor(minimumVertices);
        lock (Gate)
        {
            if (c >= 0)
            {
                // this class serves the request by construction; the next one up still does,
                // at more slack - taken only over allocating fresh
                var last = Math.Min(c + 1, classes.Length - 1);
                for (var i = c; i <= last; i++)
                {
                    var list = classes[i];
                    var n = list.Count;
                    if (n == 0) continue;
                    var m = list[n - 1];
                    list.RemoveAt(n - 1);
                    HeldBytes -= (long)m.VerticesMax * BytesPerVertex;
                    StatHits++;
                    if (m.IndicesMax != m.VerticesMax * 6 / 4)
                    {
                        // foreign entry (vanilla-era or mod-made): restore the invariant the
                        // way vanilla's GetOrCreateMesh does
                        m.Indices = new int[m.VerticesMax * 6 / 4];
                        m.IndicesMax = m.Indices.Length;
                    }
                    m.Recyclable = true;
                    return m;
                }
            }
            var capacity = c >= 0 ? classSizes[c] : minimumVertices;
            StatMisses++;
            StatMissBytes += (long)capacity * BytesPerVertex;
            return new MeshData(capacity) { Recyclable = true };
        }
    }

    private static void Drain()
    {
        var now = Clock();
        lock (Gate)
        {
            while (Incoming.TryDequeue(out var m)) File(m, now);
            if (now - lastSweepMs >= 500 || HeldBytes > (long)BudgetMb << 20)
            {
                lastSweepMs = now;
                Sweep(now);
            }
        }
    }

    /// <summary>Files one returned mesh into its class, or frees it when it cannot serve
    /// any class (too small, or basic arrays already gone). Caller holds the gate.</summary>
    private static void File(MeshData m, long now)
    {
        if (m == null) return;
        var c = m.xyz == null || m.Uv == null || m.Rgba == null || m.Flags == null || m.Indices == null
            ? -1
            : FloorClassFor(m.VerticesMax);
        if (c < 0)
        {
            m.DisposeBasicData();
            return;
        }
        m.RecyclingTime = now;
        classes[c].Add(m);
        HeldBytes += (long)m.VerticesMax * BytesPerVertex;
    }

    /// <summary>
    /// Eviction. Each class list is append-at-end/pop-at-end, so the front is always the
    /// oldest entry - TTL trimming is a front scan, and the budget pass repeatedly drops the
    /// globally oldest front. Caller holds the gate.
    /// </summary>
    private static void Sweep(long now)
    {
        for (var c = 0; c < classes.Length; c++)
        {
            var list = classes[c];
            var drop = 0;
            while (drop < list.Count && now - list[drop].RecyclingTime > TtlMs) drop++;
            for (var i = 0; i < drop; i++) Discard(list[i]);
            if (drop > 0) list.RemoveRange(0, drop);
        }
        var budget = (long)BudgetMb << 20;
        while (HeldBytes > budget)
        {
            var oldestClass = -1;
            var oldestTime = long.MaxValue;
            for (var c = 0; c < classes.Length; c++)
            {
                var list = classes[c];
                if (list.Count > 0 && list[0].RecyclingTime < oldestTime)
                {
                    oldestTime = list[0].RecyclingTime;
                    oldestClass = c;
                }
            }
            if (oldestClass < 0) break;
            Discard(classes[oldestClass][0]);
            classes[oldestClass].RemoveAt(0);
        }
    }

    private static void Discard(MeshData m)
    {
        HeldBytes -= (long)m.VerticesMax * BytesPerVertex;
        StatEvicted++;
        m.DisposeBasicData();
    }

    /// <summary>
    /// Takes over what vanilla's storage holds at the moment of enabling, so those buffers
    /// serve requests instead of sitting stranded until world exit (our DoRecycling prefix
    /// stops vanilla's own TTL sweep from ever running). Runs on the tesselation thread -
    /// the only consumer of those SortedLists - so reading and clearing them races nothing.
    /// </summary>
    private static void DrainVanilla(MeshDataRecycler vanilla)
    {
        if (vanilla == null) return;
        var now = Clock();
        lock (Gate)
        {
            var queue = QueueRef(vanilla);
            if (queue != null)
                while (queue.TryDequeue(out var m)) File(m, now);
            TakeOver(SmallRef(vanilla), now);
            TakeOver(MediumRef(vanilla), now);
            TakeOver(LargeRef(vanilla), now);
        }
    }

    private static void TakeOver(SortedList<float, MeshData> list, long now)
    {
        if (list == null || list.Count == 0) return;
        foreach (var kv in list) File(kv.Value, now);
        list.Clear();
    }

    /// <summary>Smallest class serving the request, -1 when it exceeds the largest class
    /// (then the exact size is allocated; on return such a buffer files under the top class,
    /// which any top-class request can use - capacity only ever exceeds the class).</summary>
    internal static int ClassFor(int minimumVertices)
    {
        var sizes = classSizes;
        for (var c = 0; c < sizes.Length; c++)
            if (sizes[c] >= minimumVertices)
                return c;
        return -1;
    }

    /// <summary>Largest class whose size the capacity covers, -1 below the smallest class.</summary>
    internal static int FloorClassFor(int verticesMax)
    {
        var sizes = classSizes;
        var found = -1;
        for (var c = 0; c < sizes.Length && sizes[c] <= verticesMax; c++) found = c;
        return found;
    }

    private static void EnsureClasses()
    {
        if (classes != null) return;
        // 128 (just under the engine's ~121-vertex recycling floor) growing x1.25, rounded to
        // whole faces, up past the 500k-vertex pool part maximum: ~35 classes.
        var sizes = new List<int>();
        var size = 128;
        while (size < 550000)
        {
            sizes.Add(size);
            size = (size * 5 / 4 + 3) / 4 * 4;
        }
        sizes.Add(size);
        classSizes = sizes.ToArray();
        classes = new List<MeshData>[classSizes.Length];
        for (var c = 0; c < classes.Length; c++) classes[c] = new List<MeshData>();
    }
}
