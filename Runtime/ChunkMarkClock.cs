using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Komet.Runtime;

/// <summary>
/// When each chunk was last marked dirty, keyed by the engine's index3d.
///
/// This is the signal the window prebuilder needs to know whether the world changed under a
/// window it built a few milliseconds ago. Every mutation that can change a window's content -
/// a block edit, a server light update, an arriving neighbour, worldgen - ends in
/// ClientWorldMap.SetChunkDirty or MarkChunkDirty, so those two funnels are a complete
/// account. A reference compare on chunk.Data is not: those mutations happen in place.
///
/// Written from the marking threads (network, main, relight) and read on the tesselation
/// thread, hence the concurrent dictionary. One entry per chunk ever marked - tens of
/// thousands at most, a few MB in the worst case, and the map is cleared on world exit.
/// </summary>
public static class ChunkMarkClock
{
    private static readonly ConcurrentDictionary<long, long> LastMarks = new();

    /// <summary>Whether anything records marks - false leaves the dictionary untouched.</summary>
    public static bool Enabled;

    public static void Note(long index3d)
    {
        if (Enabled) LastMarks[index3d] = Stopwatch.GetTimestamp();
    }

    /// <summary>Timestamp of the last mark, or 0 when this chunk was never marked.</summary>
    public static long LastMark(long index3d)
        => LastMarks.GetValueOrDefault(index3d);

    /// <summary>The engine's chunk key: ((y * mulZ) + z) * mulX + x.</summary>
    public static long Key(int cx, int cy, int cz, int mulX, int mulZ)
        => ((long)cy * mulZ + cz) * mulX + cx;

    public static void Clear() => LastMarks.Clear();

    public static int Count => LastMarks.Count;
}
