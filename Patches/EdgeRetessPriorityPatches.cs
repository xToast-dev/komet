using System;
using System.Collections.Generic;
using HarmonyLib;
using Komet.Runtime;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Moves edge-only re-tesselation marks of already-meshed chunks to the front of the
/// tesselation queue, so visible border holes close before invisible new chunks mesh.
///
/// The hole this fixes, measured on fresh terrain: a chunk at the load front is meshed
/// before its neighbour arrives, so the shared face is culled against the unknown - a
/// visible hole in the world (most obvious on the ocean surface). The engine repairs it
/// with an edge-only mark (sign bit set on the queue key), but that mark goes to the BACK
/// of dirtyChunks, behind every not-yet-visible full tesselation - about 2000 entries and
/// ~5 s at a measured 371 chunks/s while streaming. The player stares at the hole the
/// whole time. Coalescing those marks (EdgeRetessCoalesceMs, default off) stretched the
/// gap further and was retired for it; this is the opposite direction.
///
/// Mechanism: a sweep on the tesselation thread (prefix on OnSeperateThreadGameTick, at
/// most every <see cref="SweepIntervalMs"/> ms) rotates dirtyChunks once and moves up to
/// <see cref="MaxPromotedPerSweep"/> edge-only keys into dirtyChunksPriority, which the
/// consumer drains completely before touching the normal queue - and routes the result
/// through the priority upload path, bypassing the per-frame vertex budget. Everything
/// else about the entries is untouched: the same negative key, the same consumer, the
/// same TesselateChunk(skipChunkCenter: true). The sweep changes WHEN a repair runs,
/// never what it produces - each promoted entry is work vanilla had already queued.
///
/// Why an edge-only entry is (almost) always a visible repair: the producers only enqueue
/// one when no full tesselation is pending for that chunk (SetChunkDirty and
/// MarkChunkDirty both guard with !dirtyChunks.Contains(index3d)), and a chunk with
/// neither a mesh nor a pending full entry does not get edge marks - its first
/// tesselation is always a full one. No per-chunk lookup needed.
///
/// The two safety rules, both encoded in constants below:
/// * Player block edits land in the same priority queue (as full entries). The sweep must
///   never bury them: it skips entirely while the priority queue is busier than
///   <see cref="PrioBusyThreshold"/>, and per sweep it adds at most MaxPromotedPerSweep
///   entries - worst case ~130 ms of edge repairs in front of an edit, only while chunks
///   flood in.
/// * The promotion capacity (MaxPromotedPerSweep per SweepIntervalMs = 1280/s) must
///   exceed any realistic edge-mark inflow (measured flood: ~1150/s) - a promotion path
///   slower than the inflow would just split the backlog in two. Same lesson as the
///   coalescer's catch-up cap.
/// </summary>
public static class EdgeRetessPriorityPatches
{
    public static bool Enabled = true;

    /// <summary>Set when the sweep failed and disabled itself. An automated restore path
    /// (safemode exit, stress test) must never clear this; only an explicit
    /// '.komet toggle edgeprio' does.</summary>
    public static bool HardDisabled;

    /// <summary>Milliseconds between two sweeps. The consumer drains the priority queue on
    /// every tesselation-thread loop iteration, so a shorter cadence would only shave
    /// latency the player cannot see; a longer one lets the visible backlog grow.</summary>
    internal const int SweepIntervalMs = 50;

    /// <summary>Edge keys moved per sweep. 64 x 20 sweeps/s = 1280/s promotion capacity,
    /// above the measured flood inflow of ~1150 edge marks/s - see the class comment.</summary>
    internal const int MaxPromotedPerSweep = 64;

    /// <summary>Priority-queue size above which the sweep stands down. Whatever fills the
    /// queue - player edits, our own unconsumed promotions, a mini-dimension burst - it is
    /// work the engine considers urgent, and piling more on top helps nobody.</summary>
    internal const int PrioBusyThreshold = 192;

    /// <summary>Edge repairs moved to the priority queue.</summary>
    public static long StatPromoted;

    /// <summary>Sweeps that actually ran (cadence passed, feature on). The field taught this
    /// the hard way: the first field report showed 0 promotions with the row hidden, and
    /// "correctly idle" was indistinguishable from "prefix never ran" - broken must never
    /// look like no-data, so the sweep count is always on display while the feature is on.
    /// A join flood with a deep backlog IS correctly idle, by the way: neighbours of an
    /// arriving chunk almost all have a full tesselation pending, and MarkChunkDirty's
    /// enquedForRedraw early-out then suppresses the edge mark this sweep would promote.</summary>
    public static long StatSweeps;

    /// <summary>Sweeps skipped because the priority queue was already busier than
    /// <see cref="PrioBusyThreshold"/> - the queue's real work goes first.</summary>
    public static long StatBusySkips;

    public static Action<string> Log;

    private static long nextSweepAtMs;

    public static void Apply(Harmony harmony)
    {
        var tick = AccessTools.Method(typeof(ChunkTesselatorManager), "OnSeperateThreadGameTick")
                   ?? throw new InvalidOperationException("OnSeperateThreadGameTick not found");
        harmony.Patch(tick, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(EdgeRetessPriorityPatches), nameof(TickPrefix))));
    }

    /// <summary>
    /// Runs on the tesselation thread, immediately before the consumer drains the queues -
    /// a promotion is therefore consumed in the same tick, and the sweep can never abort
    /// the normal loop mid-pass the way a mid-tick priority insert would.
    /// </summary>
    public static void TickPrefix(ClientMain ___game)
    {
        if (!Enabled || ___game == null || !___game.ShouldTesselateTerrain) return;
        var now = Environment.TickCount64;
        if (now < nextSweepAtMs) return;
        nextSweepAtMs = now + SweepIntervalMs;
        StatSweeps++;

        try
        {
            var dirty = ClientQueues.Dirty(___game);
            var prio = ClientQueues.DirtyPrio(___game);
            if (dirty == null || prio == null) return;
            // racy read of Count, like vanilla's own OnBeforeFrame stats line - a stale
            // answer delays the sweep by one interval, nothing more
            if (prio.Count > PrioBusyThreshold) { StatBusySkips++; return; }
            Promote(dirty, ClientQueues.DirtyLock(___game), prio, ClientQueues.DirtyPrioLock(___game),
                MaxPromotedPerSweep);
        }
        catch (Exception e)
        {
            // a failed queue surgery must not be retried on a timer: whatever state it met
            // once it will meet again 50 ms later
            Enabled = false;
            HardDisabled = true;
            Log?.Invoke("edge retess priority failed and disabled itself: " + e);
        }
    }

    // Only ever touched by the one thread that sweeps (the tesselation thread in game,
    // the test thread in verify); kept static so a sweep allocates nothing.
    private static readonly List<long> taken = new(MaxPromotedPerSweep);

    /// <summary>
    /// UniqueQueue's own two halves. Rotating through its public API costs four hash
    /// operations per key - Dequeue removes from the set, Enqueue puts it straight back -
    /// for keys that never leave the queue at all, and the queue this sweep walks holds
    /// tens of thousands of them during a chunk flood. Rotating the inner Queue instead
    /// leaves the set alone except for the handful of keys that really do leave.
    ///
    /// Measured (./build.sh bench, "edge sweep rotation", four runs): 4,6-5,1x at a short
    /// queue and 5,8-6,8x at the 45.000 the inflow brake was built for - about 1,1 ms per
    /// sweep down to 0,18 ms, which is 16-21 ms a second of tesselation-thread time and the
    /// same reduction in how long dirtyChunksLock is held against the network thread
    /// inserting arrivals.
    ///
    /// Null when the fields are not where they were (a game update): the sweep then takes
    /// the API path, which is what it always did. <see cref="RotateInner"/> lets verify
    /// drive both paths through the same assertions.
    /// </summary>
    private static readonly AccessTools.FieldRef<UniqueQueue<long>, Queue<long>> InnerQueue =
        InnerField<Queue<long>>("queue");
    private static readonly AccessTools.FieldRef<UniqueQueue<long>, HashSet<long>> InnerSet =
        InnerField<HashSet<long>>("hashSet");

    private static AccessTools.FieldRef<UniqueQueue<long>, TField> InnerField<TField>(string name)
    {
        var f = AccessTools.Field(typeof(UniqueQueue<long>), name);
        return f != null && f.FieldType == typeof(TField)
            ? AccessTools.FieldRefAccess<UniqueQueue<long>, TField>(f)
            : null;
    }

    /// <summary>Take the cheap rotation when it is available. Verify clears it to prove the
    /// fallback produces the same queues; nothing in the game changes it.</summary>
    internal static bool RotateInner = true;

    /// <summary>Whether the cheap rotation is available at all on this build of the engine.</summary>
    internal static bool InnerRotationBound => InnerQueue != null && InnerSet != null;

    /// <summary>
    /// The pure center: moves up to <paramref name="cap"/> edge-only (negative) keys from
    /// <paramref name="dirty"/> into <paramref name="prio"/>, preserving the relative order
    /// of everything - the keepers in dirty and the promoted keys among themselves. One
    /// full rotation instead of per-key UniqueQueue.Remove calls, whose queue rebuild is
    /// O(n) EACH. The locks are taken one at a time, never nested, matching every other
    /// user of the pair. Returns how many keys were moved.
    /// </summary>
    internal static int Promote(UniqueQueue<long> dirty, object dirtyLock,
                                UniqueQueue<long> prio, object prioLock, int cap)
    {
        taken.Clear();
        lock (dirtyLock)
        {
            if (dirty.Count == 0) return 0;
            var any = false;
            foreach (var k in dirty)
                if (k < 0) { any = true; break; }
            if (!any) return 0;

            var inner = RotateInner && InnerRotationBound ? InnerQueue(dirty) : null;
            var set = inner != null ? InnerSet(dirty) : null;
            if (set != null) RotateOnInner(inner, set, cap);
            else RotateOnQueue(dirty, cap);
        }
        if (taken.Count == 0) return 0;

        lock (prioLock)
        {
            // UniqueQueue.Enqueue dedups: a key that is already queued as urgent merges
            // instead of doubling. The consumer's own edge/full dedup (a full entry
            // subsumes the edge one) stays in charge after this point.
            foreach (var k in taken) prio.Enqueue(k);
        }
        StatPromoted += taken.Count;
        return taken.Count;
    }

    /// <summary>The rotation as UniqueQueue itself would do it - the fallback, and what the
    /// fast path is checked against.</summary>
    private static void RotateOnQueue(UniqueQueue<long> dirty, int cap)
    {
        var n = dirty.Count;
        for (var i = 0; i < n; i++)
        {
            var k = dirty.Dequeue();
            if (k < 0 && taken.Count < cap) taken.Add(k);
            else dirty.Enqueue(k);
        }
    }

    /// <summary>The same rotation on the inner pair: a key that goes round stays in the set,
    /// so only the promoted ones are removed from it. The queue is left with exactly the
    /// keepers, in their original order, and the set with exactly those keys - which is the
    /// invariant UniqueQueue's own methods maintain.</summary>
    private static void RotateOnInner(Queue<long> queue, HashSet<long> set, int cap)
    {
        var n = queue.Count;
        for (var i = 0; i < n; i++)
        {
            var k = queue.Dequeue();
            if (k < 0 && taken.Count < cap) { taken.Add(k); set.Remove(k); }
            else queue.Enqueue(k);
        }
    }

    public static void Reset()
    {
        StatPromoted = 0;
        StatSweeps = 0;
        StatBusySkips = 0;
        nextSweepAtMs = 0;
    }
}
