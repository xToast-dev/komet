using System;
using System.Collections.Generic;
using System.Numerics;

namespace Komet.Runtime;

/// <summary>
/// Power-of-two size classes of arrays, held for reuse under a byte budget. Rent copies the
/// requested prefix of a source in the same call, so the caller never sees a bare rented
/// array. Return accepts any array whose length is a class size - an array that grew past its
/// rented length via Array.Resize is not one, and is simply left to the collector.
///
/// One lock per element type: the tesselation thread rents, the render thread returns, a few
/// thousand times a second at most - contention is not a factor at that rate, and a lock is
/// the one primitive whose correctness needs no argument.
/// </summary>
internal sealed class ArrayPoolByClass<T>
{
    private const int MinLog = 4;  // 16 elements: below that the array header is the bigger cost
    private const int MaxLog = 22; // 4M elements: larger requests are served exactly and never pooled

    private readonly Stack<T[]>[] classes = new Stack<T[]>[MaxLog + 1];
    private readonly object gate = new();
    private readonly int elementSize;

    /// <summary>Upper bound on bytes held by THIS pool; beyond it returns are dropped.</summary>
    public int BudgetMb = 64;

    public long HeldBytes;
    public long StatHits, StatMisses, StatReturns, StatDropped;

    public ArrayPoolByClass(int elementSize)
    {
        this.elementSize = elementSize;
    }

    /// <summary>An array of at least <paramref name="count"/> elements holding the first
    /// <paramref name="count"/> elements of <paramref name="source"/>.</summary>
    public T[] Rent(int count, T[] source)
    {
        if (count <= 0) return Array.Empty<T>();
        var a = RentBlank(count);
        Array.Copy(source, a, count);
        return a;
    }

    /// <summary>
    /// An array of at least <paramref name="count"/> elements, holding whatever its previous
    /// tenant left. For a caller that writes every element it will later declare - the far
    /// LOD's outputs, whose Count grows face by face as they are written.
    /// </summary>
    public T[] RentBlank(int count)
    {
        if (count <= 0) return Array.Empty<T>();
        var log = Math.Max(MinLog, BitOperations.Log2((uint)count - 1) + 1); // ceil(log2(count)), min class
        if (log > MaxLog)
        {
            StatMisses++;
            return new T[count];
        }
        lock (gate)
        {
            var st = classes[log];
            if (st != null && st.Count > 0)
            {
                var a = st.Pop();
                HeldBytes -= (long)a.Length * elementSize;
                StatHits++;
                return a;
            }
            StatMisses++;
        }
        return new T[1 << log];
    }

    public void Return(T[] a)
    {
        if (a == null) return;
        var len = a.Length;
        if (len < 1 << MinLog || !BitOperations.IsPow2(len)) return;
        var log = BitOperations.Log2((uint)len);
        if (log > MaxLog) return;
        var bytes = (long)len * elementSize;
        lock (gate)
        {
            if (HeldBytes + bytes > (long)BudgetMb << 20) { StatDropped++; return; }
            (classes[log] ??= new Stack<T[]>()).Push(a);
            HeldBytes += bytes;
            StatReturns++;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            for (var i = 0; i < classes.Length; i++) classes[i]?.Clear();
            HeldBytes = 0;
        }
    }

    public void ResetStats()
    {
        StatHits = StatMisses = StatReturns = StatDropped = 0;
    }
}
