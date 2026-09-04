using System;
using System.Diagnostics;
using System.Threading;
using Vintagestory.Client;

namespace Komet.Measure;

/// <summary>
/// Throughput accounting for the terrain tesselation thread - the single thread that turns
/// chunk data into meshes, and the reason a freshly entered area takes as long to appear as
/// it does (the HUD's "warteschl." column counts the chunks waiting for it).
///
/// Written from the tesselation thread via Interlocked, read from the render thread by the
/// HUD. The neighbour share is the time spent in BuildExtendedChunkData, which unpacks the
/// 27 surrounding chunks and assembles the 34x34x34 block window - the part of a tesselation
/// that other threads could take off the critical path.
/// </summary>
public static class TesselationStats
{
    private static long totalTicks;
    private static long neighbourTicks;
    private static long relightTicks;
    private static long chunkCount;
    private static long edgeCount;
    private static long totalAllocBytes;
    private static long neighbourAllocBytes;
    private static long relightAllocBytes;
    private static long partsAllocBytes;
    private static long jsonAllocBytes;

    // last values seen by Sample(), so each fold covers exactly the interval since the previous one
    private static long seenTicks, seenNeighbourTicks, seenRelightTicks, seenCount, seenEdges, seenAtTimestamp;
    private static long seenReceived;
    private static long seenAllocB, seenNeighbourAllocB, seenRelightAllocB, seenPartsAllocB, seenJsonAllocB;

    // smoothed outputs, updated by Sample()
    public static double MsPerChunk { get; private set; }
    public static double NeighbourMsPerChunk { get; private set; }

    /// <summary>
    /// Sunlight relighting done on the tesselation thread (TesselateChunk calls
    /// SunRelightChunk before meshing when a chunk still needs it). Its own bucket because
    /// it is the one candidate that could plausibly move to the relight worker - but only
    /// if this number says it is worth the ordering headache.
    /// </summary>
    public static double RelightMsPerChunk { get; private set; }

    /// <summary>
    /// Share of tesselations that were edge-only re-runs - a chunk meshed again because a
    /// neighbour arrived after it. This is the re-tesselation overhead of streaming: work
    /// that exists only because chunks arrive one at a time.
    /// </summary>
    public static double EdgeSharePercent { get; private set; }
    public static double ChunksPerSecond { get; private set; }

    /// <summary>
    /// Chunks arriving from the server per second. Against ChunksPerSecond this answers the
    /// question every slow load starts with: a full tesselation queue means the client is the
    /// bottleneck, an empty queue with a low arrival rate means the server (worldgen or
    /// sending) is - and no amount of client tuning will move the second case.
    /// </summary>
    public static double ReceivedPerSecond { get; private set; }

    /// <summary>
    /// Megabytes the tesselation thread allocates per second. Compare with the process-wide
    /// rate: when the two are close, the churn is chunk meshing - chiselled microblocks
    /// rebuild their full geometry on every tesselation - and no renderer is to blame.
    /// </summary>
    public static double AllocMbPerSecond { get; private set; }

    /// <summary>
    /// How much of <see cref="AllocMbPerSecond"/> happens inside BuildExtendedChunkData
    /// (unpacking the 27 neighbours, assembling the block window) and SunRelightChunk. The
    /// split exists because a field run measured 355 MB/s on the tesselation thread while
    /// the mesh recycler reported 100% hits - the churn is NOT the mesh buffers, and these
    /// two shares say which of the remaining phases to open up next.
    /// </summary>
    public static double NeighbourAllocMbPerSecond { get; private set; }
    public static double RelightAllocMbPerSecond { get; private set; }

    /// <summary>
    /// The meshing pass split further: the per-part clones in populateTesselatedChunkPart
    /// (CloneUsingRecycler's small-mesh fallback and fresh extra arrays) versus the
    /// per-block JSON shape tesselation. What none of the brackets claim is the block loop
    /// proper - cube faces, liquids, decor.
    /// </summary>
    public static double PartsAllocMbPerSecond { get; private set; }
    public static double JsonAllocMbPerSecond { get; private set; }
    public static long TotalChunks => Interlocked.Read(ref chunkCount);

    /// <summary>Bytes booked by the part-clone bracket since start. Verify reads it to prove
    /// a nested overload books once; the report only ever sees the per-second rate.</summary>
    internal static long PartsAllocBytesTotal => Interlocked.Read(ref partsAllocBytes);

    public static void AddPartsAlloc(long allocBytes)
    {
        if (allocBytes > 0) Interlocked.Add(ref partsAllocBytes, allocBytes);
    }

    public static void AddJsonAlloc(long allocBytes)
    {
        if (allocBytes > 0) Interlocked.Add(ref jsonAllocBytes, allocBytes);
    }

    public static void AddChunk(long ticks, bool edgeOnly, long allocBytes = 0)
    {
        Interlocked.Add(ref totalTicks, ticks);
        Interlocked.Increment(ref chunkCount);
        if (allocBytes > 0) Interlocked.Add(ref totalAllocBytes, allocBytes);
        if (edgeOnly) Interlocked.Increment(ref edgeCount);
    }

    public static void AddNeighbourTicks(long ticks, long allocBytes = 0)
    {
        Interlocked.Add(ref neighbourTicks, ticks);
        if (allocBytes > 0) Interlocked.Add(ref neighbourAllocBytes, allocBytes);
    }

    public static void AddRelightTicks(long ticks, long allocBytes = 0)
    {
        Interlocked.Add(ref relightTicks, ticks);
        if (allocBytes > 0) Interlocked.Add(ref relightAllocBytes, allocBytes);
    }

    /// <summary>
    /// Folds everything recorded since the previous call into the smoothed figures. Called
    /// from the frame boundary via FrameStats.PeriodicSample (every half second, HUD or not);
    /// an idle tesselation thread decays the rate to zero but keeps the last per-chunk cost
    /// on display - a cost of "0 ms" would just be false.
    /// </summary>
    public static void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var ticks = Interlocked.Read(ref totalTicks);
        var nTicks = Interlocked.Read(ref neighbourTicks);
        var rTicks = Interlocked.Read(ref relightTicks);
        var count = Interlocked.Read(ref chunkCount);
        var edges = Interlocked.Read(ref edgeCount);
        var allocB = Interlocked.Read(ref totalAllocBytes);

        var dCount = count - seenCount;
        var dSeconds = seenAtTimestamp == 0 ? 0 : (now - seenAtTimestamp) / (double)Stopwatch.Frequency;

        var first = seenCount == 0; // snap to the first sample instead of crawling up from zero
        if (dCount > 0)
        {
            var msPer = (ticks - seenTicks) * 1000.0 / Stopwatch.Frequency / dCount;
            var nMsPer = (nTicks - seenNeighbourTicks) * 1000.0 / Stopwatch.Frequency / dCount;
            // one-quarter blend: jumpy enough to follow a load burst, calm enough to read
            MsPerChunk = MsPerChunk <= 0 ? msPer : MsPerChunk + (msPer - MsPerChunk) * 0.25;
            NeighbourMsPerChunk = NeighbourMsPerChunk <= 0 ? nMsPer : NeighbourMsPerChunk + (nMsPer - NeighbourMsPerChunk) * 0.25;
            var rMsPer = (rTicks - seenRelightTicks) * 1000.0 / Stopwatch.Frequency / dCount;
            RelightMsPerChunk = RelightMsPerChunk <= 0 ? rMsPer : RelightMsPerChunk + (rMsPer - RelightMsPerChunk) * 0.25;
            var edgeShare = 100.0 * (edges - seenEdges) / dCount;
            EdgeSharePercent = first ? edgeShare : EdgeSharePercent + (edgeShare - EdgeSharePercent) * 0.25;
        }

        long received = RuntimeStats.chunksReceived;
        if (dSeconds > 0)
        {
            var rate = dCount / dSeconds;
            ChunksPerSecond += (rate - ChunksPerSecond) * 0.25;
            if (ChunksPerSecond < 0.05) ChunksPerSecond = 0;

            var allocRate = (allocB - seenAllocB) / dSeconds / 1048576.0;
            AllocMbPerSecond += (allocRate - AllocMbPerSecond) * 0.4;
            if (AllocMbPerSecond < 0.05) AllocMbPerSecond = 0;

            var nAllocB = Interlocked.Read(ref neighbourAllocBytes);
            var rAllocB = Interlocked.Read(ref relightAllocBytes);
            var nAllocRate = (nAllocB - seenNeighbourAllocB) / dSeconds / 1048576.0;
            var rAllocRate = (rAllocB - seenRelightAllocB) / dSeconds / 1048576.0;
            NeighbourAllocMbPerSecond += (nAllocRate - NeighbourAllocMbPerSecond) * 0.4;
            RelightAllocMbPerSecond += (rAllocRate - RelightAllocMbPerSecond) * 0.4;
            if (NeighbourAllocMbPerSecond < 0.05) NeighbourAllocMbPerSecond = 0;
            if (RelightAllocMbPerSecond < 0.05) RelightAllocMbPerSecond = 0;
            seenNeighbourAllocB = nAllocB;
            seenRelightAllocB = rAllocB;

            var pAllocB = Interlocked.Read(ref partsAllocBytes);
            var jAllocB = Interlocked.Read(ref jsonAllocBytes);
            var pAllocRate = (pAllocB - seenPartsAllocB) / dSeconds / 1048576.0;
            var jAllocRate = (jAllocB - seenJsonAllocB) / dSeconds / 1048576.0;
            PartsAllocMbPerSecond += (pAllocRate - PartsAllocMbPerSecond) * 0.4;
            JsonAllocMbPerSecond += (jAllocRate - JsonAllocMbPerSecond) * 0.4;
            if (PartsAllocMbPerSecond < 0.05) PartsAllocMbPerSecond = 0;
            if (JsonAllocMbPerSecond < 0.05) JsonAllocMbPerSecond = 0;
            seenPartsAllocB = pAllocB;
            seenJsonAllocB = jAllocB;

            var dReceived = received - seenReceived;
            if (dReceived < 0) dReceived = 0; // the vanilla debug screen resets the counter
            var rxRate = dReceived / dSeconds;
            ReceivedPerSecond += (rxRate - ReceivedPerSecond) * 0.25;
            if (ReceivedPerSecond < 0.05) ReceivedPerSecond = 0;
        }

        seenTicks = ticks;
        seenNeighbourTicks = nTicks;
        seenRelightTicks = rTicks;
        seenCount = count;
        seenEdges = edges;
        seenReceived = received;
        seenAllocB = allocB;
        seenAtTimestamp = now;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref totalTicks, 0);
        Interlocked.Exchange(ref neighbourTicks, 0);
        Interlocked.Exchange(ref relightTicks, 0);
        Interlocked.Exchange(ref chunkCount, 0);
        Interlocked.Exchange(ref edgeCount, 0);
        Interlocked.Exchange(ref totalAllocBytes, 0);
        Interlocked.Exchange(ref neighbourAllocBytes, 0);
        Interlocked.Exchange(ref relightAllocBytes, 0);
        Interlocked.Exchange(ref partsAllocBytes, 0);
        Interlocked.Exchange(ref jsonAllocBytes, 0);
        seenTicks = seenNeighbourTicks = seenRelightTicks = seenCount = seenEdges = seenAtTimestamp = 0;
        seenReceived = 0;
        seenAllocB = seenNeighbourAllocB = seenRelightAllocB = seenPartsAllocB = seenJsonAllocB = 0;
        MsPerChunk = NeighbourMsPerChunk = RelightMsPerChunk = ChunksPerSecond = ReceivedPerSecond = EdgeSharePercent = AllocMbPerSecond = 0;
        NeighbourAllocMbPerSecond = RelightAllocMbPerSecond = PartsAllocMbPerSecond = JsonAllocMbPerSecond = 0;
    }
}
