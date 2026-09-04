using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// Names who on the integrated server sends single-block packets - built for the 03.09.
/// report's "readpacket58 0,01 ms (638.134x)": packet 58 is ExchangeBlock, and 7.000 of them
/// a second arrived while chunks streamed in at 731/s. Every one becomes a main-thread task
/// on the client, a block write, a dirty mark (a third of all dirty marks that session came
/// out of the task drain), and for a freshly meshed chunk a second tesselation.
///
/// Who sends them is not visible from the client: the packet carries a position and a block
/// id, not a reason. On the server every single-block update - exchange or set - funnels
/// through ServerMain.SendSetBlock(blockId, x, y, z, exceptClient, exchangeOnly), so a prefix
/// there counts both kinds and, every Nth call, walks the stack to the first frame outside
/// the accessor plumbing: "BlockShapeFromAttributes.OnServerGameTick" (snow melting off
/// roofs), "BlockEntityFarmland.Update", whatever it is. Same sampling discipline as the
/// dirty-mark sources: a fixed fraction of calls are candidates, and a capture budget per
/// second keeps a storm from buying more than a millisecond or two of stack walks.
///
/// Measurement only; it changes no packet. Whether the sender is worth changing - a bulk
/// packet, a server-side coalescer - is decided from the ranking, not guessed.
/// </summary>
public static class PacketSourcePatches
{
    public static bool Enabled = true;

    public static long StatExchange, StatSet;
    private static long sampled;
    private const int SampleEveryNth = 16;
    internal const int MaxCapturesPerSecond = 25;
    private static long bucketStartTicks;
    private static int bucketTaken;
    private static long countingSince;

    private static readonly ConcurrentDictionary<string, long> Sources = new();

    public static void Apply(Harmony harmony)
    {
        var send = AccessTools.Method(typeof(ServerMain), "SendSetBlock",
                       [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool)])
                   ?? throw new InvalidOperationException("ServerMain.SendSetBlock(broadcast) not found");
        harmony.Patch(send, prefix: new HarmonyMethod(typeof(PacketSourcePatches), nameof(SendPrefix)));
        countingSince = Stopwatch.GetTimestamp();
    }

    public static void SendPrefix(bool exchangeOnly) => Note(exchangeOnly);

    internal static void Note(bool exchangeOnly)
    {
        if (!Enabled) return;
        if (exchangeOnly) Interlocked.Increment(ref StatExchange);
        else Interlocked.Increment(ref StatSet);
        if (Interlocked.Increment(ref sampled) % SampleEveryNth != 0) return;
        if (!BucketAllows(Stopwatch.GetTimestamp())) return;

        // one frame at a time, stop at the first that answers - resolving a MethodBase is the
        // expensive half of a capture, and the answer is usually three frames up. Only this
        // frame is skipped by count; the prefix, Harmony's replacement and the accessor
        // plumbing are skipped by name, so the walk does not depend on how deep the call
        // into here happens to be.
        var trace = new StackTrace(1, fNeedFileInfo: false);
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
    /// "type.method", or null for the send plumbing itself: ServerMain's send methods, the
    /// block accessors and world maps that forward to them, this class, Harmony's synthetic
    /// frames. Pure, so the rule is testable without a real stack.
    /// </summary>
    internal static string Accept(string type, string method)
    {
        if (type == null || method == null) return null;
        if (type == nameof(PacketSourcePatches) || type.Contains("Harmony")) return null;
        if (method.StartsWith("SendSetBlock", StringComparison.Ordinal)
            || method.StartsWith("SendExchangeBlock", StringComparison.Ordinal)
            || method.StartsWith("SendBlockUpdate", StringComparison.Ordinal)) return null;
        if (type.StartsWith("BlockAccessor", StringComparison.Ordinal)
            || type == "ServerWorldMap" || type == "WorldMap" || type == "ServerMain") return null;
        return type + "." + method;
    }

    internal static string PickSource(IReadOnlyList<(string type, string method)> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var s = Accept(frames[i].type, frames[i].method);
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>At most <see cref="MaxCapturesPerSecond"/> captures per rolling second.</summary>
    internal static bool BucketAllows(long nowTicks)
    {
        var start = Volatile.Read(ref bucketStartTicks);
        if (nowTicks - start >= Stopwatch.Frequency)
        {
            if (Interlocked.CompareExchange(ref bucketStartTicks, nowTicks, start) == start)
                Volatile.Write(ref bucketTaken, 0);
        }
        return Interlocked.Increment(ref bucketTaken) <= MaxCapturesPerSecond;
    }

    /// <summary>Total single-block packets per second since the counters were reset.</summary>
    public static double PerSecond
    {
        get
        {
            if (countingSince == 0) return 0;
            var s = (Stopwatch.GetTimestamp() - countingSince) / (double)Stopwatch.Frequency;
            return s > 0.5 ? (StatExchange + StatSet) / s : 0;
        }
    }

    /// <summary>Ranked sources with their share of the captures.</summary>
    public static List<(string source, double share)> Ranking(int max)
    {
        var list = new List<(string, long)>();
        long total = 0;
        foreach (var kv in Sources) { list.Add((kv.Key, kv.Value)); total += kv.Value; }
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        var result = new List<(string, double)>();
        for (var i = 0; i < list.Count && i < max; i++)
            result.Add((list[i].Item1, total > 0 ? 100.0 * list[i].Item2 / total : 0));
        return result;
    }

    public static void Write(StringBuilder sb, CultureInfo ci)
    {
        sb.AppendFormat(ci, "block-pakete (server): exchange {0:N0}, set {1:N0} seit reset = {2:F0}/s",
            StatExchange, StatSet, PerSecond);
        var ranking = Ranking(5);
        if (ranking.Count > 0)
        {
            sb.Append(" | quellen: ");
            for (var i = 0; i < ranking.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(ci, "{0} {1:F0}%", ranking[i].source, ranking[i].share);
            }
        }
        if (!Enabled) sb.Append(" (AUS)");
        sb.Append('\n');
    }

    public static void ResetStats()
    {
        Interlocked.Exchange(ref StatExchange, 0);
        Interlocked.Exchange(ref StatSet, 0);
        Sources.Clear();
        countingSince = Stopwatch.GetTimestamp();
        Volatile.Write(ref bucketStartTicks, 0);
        Volatile.Write(ref bucketTaken, 0);
    }

    public static void Clear()
    {
        ResetStats();
        countingSince = 0;
    }
}
