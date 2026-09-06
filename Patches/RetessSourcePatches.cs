using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HarmonyLib;
using Komet.Measure;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Answers "who keeps marking chunks dirty?" - built for the settled-scene finding of
/// 112 chunks/s re-tesselated at an empty queue and 20/s arrival: ~90/s of standing-still
/// re-tesselation with no visible cause, feeding both the terrain recording spikes and the
/// tesselation thread's allocation churn.
///
/// Every dirty mark funnels through ClientWorldMap.SetChunkDirty or MarkChunkDirty (the
/// latter enqueues itself rather than delegating, so both are patched). The prefix counts
/// every attempt, and every Nth one is a capture candidate: a stack trace whose first frame
/// outside the marking plumbing gets booked. The un-captured path is a few increments; the
/// captures cost tens of microseconds each on whatever thread made the mark and are
/// rate-capped (<see cref="MaxCapturesPerSecond"/>), so the ranking is always being
/// collected at a bounded price.
///
/// Attempts, not enqueues: a chunk already awaiting redraw is deduplicated inside the
/// original method, so these figures say who keeps *asking* - which is exactly the
/// attribution question - and can legitimately exceed the tesselation rate.
/// </summary>
public static class RetessSourcePatches
{
    /// <summary>Count mark attempts. Three interlocked increments; the HUD's marks/s row.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// Lift the capture rate cap: every Nth mark gets a stack, whatever that costs - for a
    /// deliberately dense capture session via '.komet toggle retess'.
    ///
    /// A capture resolves method metadata for the frames it walks, which is tens of
    /// microseconds, and during chunk streaming the marks come in at thousands per second -
    /// measured 7244/s, so uncapped sampling would burn ~900 captures a second on the threads
    /// that are doing the loading. That is why the default path is capped instead of off:
    /// gating sampling behind this toggle left two field reports showing a standing-still
    /// mark storm with no ranking - the exact question the sampler exists to answer.
    /// </summary>
    public static bool SampleSources;

    /// <summary>Every Nth mark is a capture candidate.</summary>
    private const int SampleEveryNth = 8;

    /// <summary>
    /// The default path's cost ceiling: at most this many captures per second, whatever the
    /// mark rate (25/s x tens of microseconds = ~1-2 ms/s worst case, spread over the marking
    /// threads). Within one second heavy markers still dominate the ranking; across mixed
    /// phases the shares are time-weighted, so rank a specific question after
    /// '.komet retess reset' in the scene it is about.
    /// </summary>
    internal const int MaxCapturesPerSecond = 25;
    private static readonly CaptureBudget Budget = new(MaxCapturesPerSecond);

    public static long StatMarks, StatEdgeOnly, StatPriority;
    private static long sampled;

    private static readonly ConcurrentDictionary<string, long> Sources = new();
    private static long countingSince;

    public static void Apply(Harmony harmony)
    {
        var map = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientWorldMap")
                  ?? throw new InvalidOperationException("ClientWorldMap not found");

        var set = AccessTools.Method(map, "SetChunkDirty")
                  ?? throw new InvalidOperationException("SetChunkDirty not found");
        var mark = AccessTools.Method(map, "MarkChunkDirty")
                   ?? throw new InvalidOperationException("MarkChunkDirty not found");

        harmony.Patch(set, prefix: new HarmonyMethod(typeof(RetessSourcePatches), nameof(SetPrefix)));
        harmony.Patch(mark, prefix: new HarmonyMethod(typeof(RetessSourcePatches), nameof(MarkPrefix)));
        countingSince = Stopwatch.GetTimestamp();
    }

    public static void SetPrefix(bool priority, bool edgeOnly) => Note(priority, edgeOnly);

    public static void MarkPrefix(bool priority, bool edgeOnly) => Note(priority, edgeOnly);

    private static void Note(bool priority, bool edgeOnly)
    {
        if (!Enabled) return;
        // the edge coalescer re-issues held marks through the same funnel; counting those
        // would double-book every coalesced mark and make "nur-rand" incomparable
        if (EdgeCoalescePatches.IsFlushing) return;
        Interlocked.Increment(ref StatMarks);
        if (edgeOnly) Interlocked.Increment(ref StatEdgeOnly);
        if (priority) Interlocked.Increment(ref StatPriority);

        if (Interlocked.Increment(ref sampled) % SampleEveryNth != 0) return;
        if (!SampleSources && !BucketAllows(Stopwatch.GetTimestamp())) return;

        // Frames are resolved one at a time and the walk stops at the first that answers the
        // question. GetMethod() is the expensive half of a stack capture - it materialises a
        // MethodBase from a handle - and the answer is almost always two or three frames up,
        // so resolving the whole stack was thirty of those to use one.
        var trace = new StackTrace(2, fNeedFileInfo: false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var m = trace.GetFrame(i)?.GetMethod();
            if (m == null) continue;
            var source = Accept(m.DeclaringType?.Name, m.Name);
            if (source == null) continue;
            Sources.AddOrUpdate(source, 1, (_, c) => c + 1);
            return;
        }
    }

    /// <summary>
    /// "type.method", or null when this frame is the marking plumbing itself, patch machinery,
    /// or synthetic (no declaring type - Harmony's dynamic replacement methods). One frame at a
    /// time so the caller can stop at the first hit; pure, so the rule is testable without real
    /// stack captures.
    /// </summary>
    internal static string Accept(string type, string method)
    {
        if (type == null) return null;
        if (method.Contains("SetChunkDirty") || method.Contains("MarkChunkDirty")) return null;
        if (type == nameof(RetessSourcePatches) || type.Contains("Harmony")) return null;
        return type + "." + method;
    }

    /// <summary>At most <see cref="MaxCapturesPerSecond"/> captures per rolling second.</summary>
    internal static bool BucketAllows(long nowTicks) => Budget.Allows(nowTicks);

    /// <summary>The first frame <see cref="Accept"/> takes. Kept for the frame-list tests.</summary>
    internal static string PickSource(IReadOnlyList<(string type, string method)> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var source = Accept(frames[i].type, frames[i].method);
            if (source != null) return source;
        }
        return null;
    }

    // ---- current rate, over a short sliding window -----------------------------------
    // The HUD figure used to be StatMarks divided by the whole session, which made it
    // unreadable for the question it exists to answer: the user stood still at an empty
    // queue and the row said "1006/s" - the loading burst from minutes earlier, averaged
    // in forever. A 5-second window says what is marking NOW.
    private const double WindowSeconds = 5.0;
    private static long windowStart, windowMarks, windowEdge;
    private static double rateMarks, rateEdge;

    private static void RollWindow()
    {
        var now = Stopwatch.GetTimestamp();
        if (windowStart == 0) { windowStart = now; windowMarks = StatMarks; windowEdge = StatEdgeOnly; return; }
        var elapsed = (now - windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < WindowSeconds) return;
        rateMarks = (StatMarks - windowMarks) / elapsed;
        rateEdge = (StatEdgeOnly - windowEdge) / elapsed;
        windowStart = now;
        windowMarks = StatMarks;
        windowEdge = StatEdgeOnly;
    }

    /// <summary>Mark attempts per second over the last ~5 seconds - the HUD figure.</summary>
    public static double MarksPerSecond { get { RollWindow(); return rateMarks; } }

    public static double EdgeMarksPerSecond { get { RollWindow(); return rateEdge; } }

    /// <summary>Marks per second since the last reset, and the sampled source ranking.</summary>
    public static string BuildReport()
    {
        var ci = CultureInfo.CurrentCulture;
        var seconds = countingSince == 0
            ? 0
            : (Stopwatch.GetTimestamp() - countingSince) / (double)Stopwatch.Frequency;
        if (seconds < 1 || StatMarks == 0)
            return "no dirty marks recorded yet";

        var sb = new StringBuilder(512);
        sb.AppendFormat(ci, "dirty marks: {0:F0}/s total ({1:F0}/s edge only, {2:F0}/s priority) over {3:F0}s\n",
            StatMarks / seconds, StatEdgeOnly / seconds, StatPriority / seconds, seconds);

        // Shares, not rates: samples may come from a manual toggle spanning the session or
        // from a short auto-probe burst - dividing either by the whole runtime would lie.
        var top = new List<KeyValuePair<string, long>>(Sources);
        if (top.Count == 0)
        {
            sb.Append("sources: no samples yet (runs at most ")
              .Append(MaxCapturesPerSecond)
              .Append("/s; '.komet toggle retess' lifts the cap)\n");
        }
        else
        {
            long total = 0;
            foreach (var kv in top) total += kv.Value;
            sb.AppendFormat(ci, "quellen ({0} samples a 1/{1}, anteile):\n", total, SampleEveryNth);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var shown = Math.Min(top.Count, 10);
            for (var i = 0; i < shown; i++)
                sb.AppendFormat(ci, "  {0,-44} {1:F0}%\n", top[i].Key, 100.0 * top[i].Value / total);
        }

        return sb.ToString().TrimEnd('\n');
    }

    public static void Reset()
    {
        StatMarks = StatEdgeOnly = StatPriority = 0;
        sampled = 0;
        Budget.Reset();
        Sources.Clear();
        countingSince = Stopwatch.GetTimestamp();
        windowStart = 0;
        windowMarks = windowEdge = 0;
        rateMarks = rateEdge = 0;
    }
}
