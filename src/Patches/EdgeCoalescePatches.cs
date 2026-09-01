using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace Komet.Patches;

/// <summary>
/// Coalesces the edge-only re-tesselation marks that chunk arrival showers on neighbours.
///
/// Measured with the dirty-mark sampler: ~98 % of all marks during streaming come from
/// loadChunkMT / SetChunksAroundDirty - every arriving chunk marks its six neighbours
/// edge-only, so a border chunk is re-meshed up to six times while its neighbourhood
/// trickles in one packet at a time. Each redundant pass costs the tesselation thread a
/// full edge rebuild and the main thread another upload and pool insert - the traffic
/// behind the no-gc opaque/schatten recording stalls while loading.
///
/// The neighbour-load path is precisely identifiable: MarkChunkDirty with edgeOnly=true,
/// fireEvent=false, no priority, no sunRelight, no callback (funneled through
/// MarkChunkDirty_OnNeighbourChunkLoad). Those marks are held for EdgeRetessCoalesceMs and
/// then re-issued once per chunk; marks arriving while one is already pending are absorbed.
/// Everything else - full marks, priority marks, relight, block changes - passes through
/// untouched. The visible cost is that a freshly loaded chunk's border faces settle up to
/// half a second later than vanilla, in a phase where the chunk itself just popped in.
///
/// Disabling mid-session MUST flush the pending set: a swallowed mark that is never
/// re-issued would leave a chunk border un-meshed until something else dirties it.
/// </summary>
public static class EdgeCoalescePatches
{
    public static bool Enabled = true;

    /// <summary>How long edge marks are collected before one re-tesselation is issued.</summary>
    public static double CoalesceMs = 400;

    /// <summary>Due marks re-issued per flush tick at most; the rest follows next tick.</summary>
    public const int MaxFlushPerTick = 256;

    /// <summary>Backlog size from which the flush stops being polite. The 1.35.2 cap held
    /// flat at 192 per 150 ms - BELOW the flood's inflow - and the backlog grew to 32 900
    /// held marks, every one of them a visibly un-meshed chunk border (holes in the ocean
    /// surface, user screenshot). A deferral queue must drain faster than it fills, always.</summary>
    public const int CatchUpThreshold = 2048;

    /// <summary>A quarter of an over-threshold backlog per tick: the backlog decays
    /// exponentially instead of growing, and the worst case clears within a few ticks.</summary>
    internal static int CapFor(int pending)
        => pending > CatchUpThreshold ? Math.Max(MaxFlushPerTick, pending / 4) : MaxFlushPerTick;

    /// <summary>Marks absorbed into an already pending one - the saved re-tesselations.</summary>
    public static long StatAbsorbed;
    public static long StatFlushed;

    private static readonly Coalescer Pending = new();
    [ThreadStatic] private static bool flushing;

    /// <summary>True while this thread is re-issuing held marks - the dirty-mark sampler
    /// uses it to keep our own flushes out of the producer statistics.</summary>
    public static bool IsFlushing => flushing;

    private static object worldMap; // ClientWorldMap, captured from the first intercepted call
    private static FastInvokeHandler markChunkDirty;

    public static Action<string> Log;

    public static void Apply(Harmony harmony, double coalesceMs)
    {
        CoalesceMs = coalesceMs;
        var map = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientWorldMap")
                  ?? throw new InvalidOperationException("ClientWorldMap not found");
        var mark = AccessTools.Method(map, "MarkChunkDirty")
                   ?? throw new InvalidOperationException("MarkChunkDirty not found");
        markChunkDirty = MethodInvoker.GetHandler(mark);

        harmony.Patch(mark, prefix: new HarmonyMethod(typeof(EdgeCoalescePatches), nameof(MarkPrefix)));
    }

    /// <summary>Packs chunk coordinates; map sizes stay far below 2^21 chunks per axis.</summary>
    internal static long Pack(int cx, int cy, int cz) => ((long)cx << 42) | ((long)cy << 21) | (uint)cz;
    internal static (int cx, int cy, int cz) Unpack(long key)
        => ((int)(key >> 42), (int)((key >> 21) & 0x1FFFFF), (int)(key & 0x1FFFFF));

    // ReSharper disable InconsistentNaming - Harmony binds these BY NAME: __instance is the
    // injected receiver and OnRetesselated must match the engine's parameter spelling exactly.
    // A rename (an IDE "fix naming" pass did it once) makes Patch() throw and the feature go dead.
    public static bool MarkPrefix(object __instance, int cx, int cy, int cz, bool priority,
                                  bool sunRelight, Action OnRetesselated, bool fireEvent, bool edgeOnly)
    {
        if (!Enabled || flushing || CoalesceMs <= 0) return true;
        // only the neighbour-load signature is held back; anything else passes through
        if (!edgeOnly || priority || sunRelight || fireEvent || OnRetesselated != null) return true;

        worldMap = __instance;
        lock (Pending)
        {
            if (Pending.Note(Pack(cx, cy, cz), Stopwatch.GetTimestamp(),
                             (long)(CoalesceMs / 1000.0 * Stopwatch.Frequency)))
                return false; // held; the flusher re-issues it
            StatAbsorbed++;
            return false; // already pending - this mark is the saving
        }
    }

    private static readonly List<long> Due = [];

    /// <summary>Re-issues due marks. Runs on the main thread via a game tick listener.</summary>
    public static void Flush() => FlushInternal(onlyDue: true);

    /// <summary>Everything out, due or not - for disabling, safemode and world leave.</summary>
    public static void FlushAll() => FlushInternal(onlyDue: false);

    private static void FlushInternal(bool onlyDue)
    {
        var map = worldMap;
        if (map == null || markChunkDirty == null) return;

        Due.Clear();
        lock (Pending)
        {
            // Capped against the burst cost the stress test measured (-0,12 ms +-0,07 when
            // hundreds of re-issues landed in one tick), but with a catch-up mode: a cap
            // below the inflow turns "delayed" into "missing" - see CatchUpThreshold.
            if (onlyDue) Pending.CollectDue(Stopwatch.GetTimestamp(), Due, CapFor(Pending.Count));
            else Pending.CollectAll(Due);
        }
        if (Due.Count == 0) return;

        try
        {
            flushing = true;
            foreach (var key in Due)
            {
                (var cx, var cy, var cz) = Unpack(key);
                markChunkDirty(map, cx, cy, cz, false, false, null, false, true);
                StatFlushed++;
            }
        }
        catch (Exception e)
        {
            // a flush that cannot deliver must not swallow marks silently: stop holding
            // new ones and say so
            Enabled = false;
            Log?.Invoke("edge coalescing failed and disabled itself: " + e);
        }
        finally
        {
            flushing = false;
        }
    }

    public static int PendingCount { get { lock (Pending) return Pending.Count; } }

    public static void Reset()
    {
        lock (Pending) Pending.Clear();
        StatAbsorbed = StatFlushed = 0;
        worldMap = null;
    }

    /// <summary>
    /// The pure center: first mark of a chunk opens a fixed deadline (not a sliding one -
    /// constant re-marking must never starve the flush), later marks are absorbed.
    /// </summary>
    internal sealed class Coalescer
    {
        private readonly Dictionary<long, long> dueAt = new(256);

        public int Count => dueAt.Count;

        /// <summary>True when this mark opened a new pending entry; false = absorbed.</summary>
        public bool Note(long key, long nowTicks, long delayTicks)
        {
            if (dueAt.ContainsKey(key)) return false;
            dueAt[key] = nowTicks + delayTicks;
            return true;
        }

        public void CollectDue(long nowTicks, List<long> into, int max = int.MaxValue)
        {
            foreach (var kv in dueAt)
            {
                if (kv.Value > nowTicks) continue;
                into.Add(kv.Key);
                if (into.Count >= max) break;
            }
            foreach (var key in into) dueAt.Remove(key);
        }

        public void CollectAll(List<long> into)
        {
            foreach (var kv in dueAt) into.Add(kv.Key);
            dueAt.Clear();
        }

        public void Clear() => dueAt.Clear();
    }
}
