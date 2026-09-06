using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Datastructures;

/// <summary>
/// What the edge-retess sweep costs to walk the tesselation queue - the price of finding a
/// handful of border repairs among everything else waiting to be meshed.
///
/// The sweep rotates dirtyChunks once every 50 ms, on the tesselation thread and under
/// dirtyChunksLock, which is the same lock the network thread inserts arriving chunks under.
/// A rotation through UniqueQueue's own API costs four hash operations per key (Dequeue
/// removes from the set, Enqueue adds it back) for keys that never leave the queue at all -
/// and during the flood this benchmark is about, the queue holds tens of thousands of them.
///
/// The alternative rotates the inner Queue and touches the HashSet only for the keys that are
/// actually promoted, which is at most the cap. Same queue, same order, same result; this
/// prices the difference so the reflection it needs is a measured decision.
/// </summary>
internal static class QueueSweepBench
{
    /// <summary>Backlogs measured in the field: a settled world, a stream, and the 45k flood
    /// the inflow brake was built for.</summary>
    private static readonly int[] Depths = { 200, 2000, 12000, 45000 };

    private const int Cap = 64;

    private static readonly FieldInfo InnerQueue =
        typeof(UniqueQueue<long>).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo InnerSet =
        typeof(UniqueQueue<long>).GetField("hashSet", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>The shape the sweep has today: everything through UniqueQueue's own API.</summary>
    private static int PromoteViaApi(UniqueQueue<long> dirty, List<long> taken, int cap)
    {
        taken.Clear();
        var n = dirty.Count;
        for (var i = 0; i < n; i++)
        {
            var k = dirty.Dequeue();
            if (k < 0 && taken.Count < cap) taken.Add(k);
            else dirty.Enqueue(k);
        }
        return taken.Count;
    }

    /// <summary>The same rotation on the inner queue, with the set touched only for what
    /// leaves.</summary>
    private static int PromoteInner(UniqueQueue<long> dirty, List<long> taken, int cap)
    {
        taken.Clear();
        var q = (Queue<long>)InnerQueue.GetValue(dirty);
        var set = (HashSet<long>)InnerSet.GetValue(dirty);
        var n = q.Count;
        for (var i = 0; i < n; i++)
        {
            var k = q.Dequeue();
            if (k < 0 && taken.Count < cap) { taken.Add(k); set.Remove(k); }
            else q.Enqueue(k);
        }
        return taken.Count;
    }

    /// <summary>A backlog with the flood's mix: about one key in twenty is a border repair.</summary>
    private static UniqueQueue<long> Fill(int depth, Random rnd)
    {
        var q = new UniqueQueue<long>();
        for (var i = 0; i < depth; i++)
        {
            long k = i + 1;
            if (rnd.Next(20) == 0) k |= long.MinValue;
            q.Enqueue(k);
        }
        return q;
    }

    private static double Time(Func<UniqueQueue<long>, List<long>, int, int> promote, int depth, int rounds)
    {
        var rnd = new Random(7);
        var taken = new List<long>(Cap);
        var q = Fill(depth, rnd);

        for (var i = 0; i < 3; i++) promote(q, taken, Cap);

        var sw = Stopwatch.StartNew();
        var moved = 0;
        for (var i = 0; i < rounds; i++)
        {
            moved += promote(q, taken, Cap);
            // put the promoted keys back, so every round walks the same depth
            foreach (var k in taken) q.Enqueue(k);
        }
        sw.Stop();
        if (moved < 0) throw new Exception("unreachable");
        return sw.Elapsed.TotalMilliseconds / rounds;
    }

    public static void Run()
    {
        if (InnerQueue == null || InnerSet == null)
        {
            Console.WriteLine("\nedge sweep rotation: skipped, UniqueQueue's fields are not where they were");
            return;
        }

        Console.WriteLine("\nedge sweep rotation (one sweep, 20 per second on the tesselation thread)");
        Console.WriteLine("     backlog   via UniqueQueue   inner queue   speedup    saved per second");
        foreach (var depth in Depths)
        {
            var rounds = Math.Max(20, 2_000_000 / depth);
            var api = Time(PromoteViaApi, depth, rounds);
            var inner = Time(PromoteInner, depth, rounds);
            Console.WriteLine($"  {depth,10}   {api,13:F3}ms {inner,11:F3}ms   {api / Math.Max(1e-9, inner),6:F2}x"
                              + $"   {(api - inner) * 20,14:F2} ms/s");
        }
    }
}
