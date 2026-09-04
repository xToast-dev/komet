using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;

namespace Komet.Measure;

/// <summary>
/// A monotonically increasing counter sampled once per frame and smoothed, so the display can
/// show "per frame" instead of "since you logged in".
/// </summary>
public sealed class SmoothedCounter
{
    private readonly Func<long> read;
    private long previous;

    internal SmoothedCounter(Func<long> read) => this.read = read;

    /// <summary>Smoothed value for the last frame.</summary>
    public double PerFrame { get; private set; }

    internal void Advance(bool first, double alpha)
    {
        var now = read();
        var delta = now - previous;
        previous = now;
        if (delta < 0) delta = 0;
        PerFrame = first ? delta : PerFrame + (delta - PerFrame) * alpha;
    }

    internal void Rebase() => previous = read();
}

/// <summary>
/// Per-frame accounting shared by the optimising mod and the measurement-only baseline, so
/// the two produce numbers that can actually be compared.
///
/// The frame boundary is EnumRenderStage.Before, the first thing ClientMain.MainRenderLoop
/// triggers, so the gap between two of those is the real frame period and every render stage
/// of a frame lands in the same bucket.
///
/// Values are exponentially smoothed and republished *every* frame rather than at the end of
/// a fixed window - a window only updates the display when it closes, which makes a HUD look
/// frozen.
/// </summary>
public static class FrameStats
{
    /// <summary>One bucket per EnumRenderStage value.</summary>
    public const int StageCount = 13;

    /// <summary>Smoothing factor: roughly a half second of history at typical frame rates.</summary>
    private const double Alpha = 1.0 / 64.0;

    private const int WarmupFrames = 16;
    private const int PeakWindowFrames = 180;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    private static readonly List<SmoothedCounter> Counters = new();

    // accumulated inside the current frame
    private static long cullTicks;
    private static long cullWaitTicks;
    private static long cullRebuildTicks;
    private static int cullRebuilds;
    private static long gameTickTicks;
    private static long swapTicks;
    private static readonly long[] stageTicks = new long[StageCount];
    private static double uploadMsThisFrame;
    private static double hudMsThisFrame;

    private static long prevFrameTs;
    private static int peakFrames;
    private static double frameMsPeak, cullMsPeak, uploadMsPeak;

    public static bool HasData { get; private set; }
    public static long TotalFrames { get; private set; }

    public static double AvgFrameMs { get; private set; }
    public static double MaxFrameMs { get; private set; }
    public static double AvgCullMs { get; private set; }
    public static double MaxCullMs { get; private set; }
    public static double AvgUploadMs { get; private set; }
    public static double MaxUploadMs { get; private set; }
    public static double GameTickMs { get; private set; }

    /// <summary>
    /// Milliseconds per frame inside SwapBuffers. Under mesa_glthread the per-stage numbers
    /// only measure command *recording* - the driver thread executes asynchronously, and
    /// whatever it has not finished by frame end gets waited for here. A large value with an
    /// idle GPU is therefore driver-thread CPU cost (or compositor back-pressure), not
    /// rendering.
    /// </summary>
    public static double AvgSwapMs { get; private set; }

    /// <summary>
    /// The frame that just ended, unsmoothed: swap and both shadow cascades. The stress test
    /// needs raw per-frame values - it does its own averaging over a slice, and an exponential
    /// average with a 64 frame time constant would carry the previous slice's state across
    /// every toggle. These exist because "safemode is faster" turned out to be a difference
    /// that lived entirely in swap while every stage got *cheaper*: a frame-time-only report
    /// cannot tell a CPU cost from driver back-pressure, and that distinction is the whole
    /// question whenever the GPU is idle.
    /// </summary>
    public static double LastSwapMs { get; private set; }
    public static double LastShadowMs { get; private set; }

    /// <summary>
    /// Raised once per frame boundary, after the warmup gate, with the finished frame's
    /// totals: (frameMs, avgFrameMs, gcPauseMs, uploadMs). For consumers that steer off
    /// whole-frame pressure - the upload budget's second input lives here, because under a
    /// threaded GL driver the cost of an upload surfaces in the frame, not in the upload
    /// clock. Null in the baseline.
    /// </summary>
    public static Action<double, double, double, double> FrameSummary;

    // ---- garbage collector ---------------------------------------------------------
    // Sampled from the frame boundary every SampleIntervalSeconds, HUD or no HUD. The pause
    // total is the number that matters: it is
    // time the runtime stopped every thread at once, which is the only mechanism that can
    // make the render thread, the tesselation thread and the occlusion worker all slow down
    // by the same factor at the same moment.

    /// <summary>Gen0 collections per second.</summary>
    public static double Gen0PerSecond { get; private set; }
    /// <summary>Gen2 collections per second - each one is a candidate for a dropped frame.</summary>
    public static double Gen2PerSecond { get; private set; }
    public static double Gen1PerSecond { get; private set; }

    // ---- what the collector had to keep ----------------------------------------------
    // "216 MB/s allocated, 27 gen0 a second, every hitch on a pause" says how much is
    // allocated, not how much SURVIVES - and that is the number that decides the lever.
    // Garbage is cheap to collect; a gen0 pause is spent on the survivors (marking, copying
    // into gen1) and on the old-generation cards that point at them. Streamed world data
    // survives by definition. GC.GetGCMemoryInfo(Ephemeral) reports the promoted bytes of
    // the last gen0/gen1 collection; read once per frame that saw a collection, so at 27/s
    // and 90 fps nearly every collection is seen once. Gen0 and gen1 promotion both count,
    // so an object promoted twice counts twice - the figure is an upper bound on survival.

    /// <summary>Bytes promoted out of gen0/gen1 per second, smoothed.</summary>
    public static double PromotedMbPerSecond { get; private set; }
    /// <summary>Promoted bytes per ephemeral collection, smoothed - the pause's workload.</summary>
    public static double PromotedMbPerGc { get; private set; }
    /// <summary>Total heap after the last ephemeral collection.</summary>
    public static double GcHeapMb { get; private set; }
    /// <summary>Generation (0/1) and pause of the last ephemeral collection seen; -1 = none yet.</summary>
    public static int LastGcGeneration { get; private set; } = -1;
    public static double LastGcPauseMs { get; private set; }
    /// <summary>Ephemeral collections whose info was read since start.</summary>
    public static long GcInfosSeen { get; private set; }

    private static long lastGcIndex;
    private static long promotedBytesAccum;
    private static int gcSeenAccum;

    private static void NoteEphemeralGc()
    {
        try
        {
            var info = GC.GetGCMemoryInfo(GCKind.Ephemeral);
            if (info.Index == lastGcIndex) return;
            lastGcIndex = info.Index;
            promotedBytesAccum += info.PromotedBytes;
            gcSeenAccum++;
            GcInfosSeen++;
            LastGcGeneration = info.Generation;
            LastGcPauseMs = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0;
            GcHeapMb = info.HeapSizeBytes / 1048576.0;
        }
        catch (Exception)
        {
            // a runtime without the info: the rows stay at zero, nothing else changes
        }
    }
    /// <summary>Milliseconds per second the GC paused all threads.</summary>
    public static double GcPauseMsPerSecond { get; private set; }

    /// <summary>
    /// Megabytes allocated per second, process-wide. This is the number the pause total
    /// follows from: 131 ms/s of pauses under water is the collector keeping up with
    /// whatever this says - and whoever allocates it is the real culprit.
    /// </summary>
    public static double AllocMbPerSecond { get; private set; }

    private static int seenGen0, seenGen1, seenGen2;
    private static double seenPauseMs;
    private static long seenAllocBytes;
    private static long gcSeenAt;

    // ---- allocation attribution ------------------------------------------------------
    // "272 MB/s alloc" names the pressure but not the culprit: the tesselation thread
    // measures its own share (TesselationStats), and the rest was a guess - a field report
    // showed 150 of 161 hitches carrying a gc pause with ~220 MB/s unattributed. These
    // rows close that gap thread by thread; whatever no one measures stays visible as
    // "rest" instead of disappearing into the total.

    /// <summary>Alloc rate of the thread SampleGc runs on - the render/main thread, since
    /// the HUD's once-a-second tick is the only caller. Meaningless if some other thread
    /// ever starts calling SampleGc; keep it on the render path.</summary>
    public static double MainAllocMbPerSecond { get; private set; }

    /// <summary>Alloc rate inside SystemNetworkProcess' own thread tick - server packet
    /// handling, chunk intake included. Fed by MeasurementPatches.</summary>
    public static double NetAllocMbPerSecond { get; private set; }

    /// <summary>Alloc rate of Komet's unpack prefetcher, which decompresses chunk
    /// neighbourhoods so the tesselation thread does not have to. 0 in the baseline mod,
    /// which has no prefetcher.</summary>
    public static double PrefetchAllocMbPerSecond { get; private set; }

    private static long netAllocBytes, prefetchAllocBytes;
    private static long seenMainAllocB, seenNetAllocB, seenPrefetchAllocB;

    public static void AddNetAllocBytes(long bytes)
    { if (bytes > 0) System.Threading.Interlocked.Add(ref netAllocBytes, bytes); }

    public static void AddPrefetchAllocBytes(long bytes)
    { if (bytes > 0) System.Threading.Interlocked.Add(ref prefetchAllocBytes, bytes); }
    private static TimeSpan seenCpuTime;

    /// <summary>
    /// CPU cores the whole process keeps busy, averaged over the last seconds - main thread,
    /// tesselation, cull workers, worldgen, GC threads, everything. This is the number that
    /// answers "my CPU is barely used": a frame is a latency problem, not a throughput
    /// problem, so outside of streaming there simply is no more work to spread - and during
    /// streaming this row shows how many cores the pipeline really soaks.
    /// </summary>
    public static double CpuCoresBusy { get; private set; }

    /// <summary>
    /// Anything else that folds counters into per-second rates (the tesselation throughput,
    /// in TesselationStats). Invoked from the frame boundary right after <see cref="SampleGc"/>,
    /// on the render thread, every <see cref="SampleIntervalSeconds"/>.
    /// </summary>
    public static Action PeriodicSample;

    /// <summary>
    /// How often the per-second rates fold. They used to fold inside the HUD's own refresh
    /// cycle, i.e. only while the overlay was showing - and a '.komet report' taken with the
    /// overlay off, which is how every field report arrives, printed zeros for the GC rate,
    /// the allocation rate, the CPU load and the chunks per second while the same report's
    /// hitch lines carried 40 ms GC pauses. The rates are frame accounting, not HUD decoration.
    /// </summary>
    public const double SampleIntervalSeconds = 0.5;
    private static long sampleSeenAt;

    /// <summary>Folds the GC counters into per-second rates. Called from the frame boundary
    /// every <see cref="SampleIntervalSeconds"/>; a direct call is harmless (0.2 s guard).</summary>
    public static void SampleGc()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        var alloc = GC.GetTotalAllocatedBytes(precise: false);
        var mainAlloc = GC.GetAllocatedBytesForCurrentThread();
        var netAlloc = System.Threading.Interlocked.Read(ref netAllocBytes);
        var prefetchAlloc = System.Threading.Interlocked.Read(ref prefetchAllocBytes);

        var cpuTime = Environment.CpuUsage.TotalTime;

        if (gcSeenAt != 0)
        {
            var dt = (now - gcSeenAt) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (dt > 0.2)
            {
                var a = 0.4; // fast enough to catch an underwater excursion while it lasts
                Gen0PerSecond += ((g0 - seenGen0) / dt - Gen0PerSecond) * a;
                Gen1PerSecond += ((g1 - seenGen1) / dt - Gen1PerSecond) * a;
                Gen2PerSecond += ((g2 - seenGen2) / dt - Gen2PerSecond) * a;
                PromotedMbPerSecond += (promotedBytesAccum / dt / 1048576.0 - PromotedMbPerSecond) * a;
                if (gcSeenAccum > 0)
                    PromotedMbPerGc += (promotedBytesAccum / (double)gcSeenAccum / 1048576.0 - PromotedMbPerGc) * a;
                promotedBytesAccum = 0;
                gcSeenAccum = 0;
                GcPauseMsPerSecond += ((pauseMs - seenPauseMs) / dt - GcPauseMsPerSecond) * a;
                AllocMbPerSecond += ((alloc - seenAllocBytes) / dt / 1048576.0 - AllocMbPerSecond) * a;
                MainAllocMbPerSecond += ((mainAlloc - seenMainAllocB) / dt / 1048576.0 - MainAllocMbPerSecond) * a;
                NetAllocMbPerSecond += ((netAlloc - seenNetAllocB) / dt / 1048576.0 - NetAllocMbPerSecond) * a;
                PrefetchAllocMbPerSecond += ((prefetchAlloc - seenPrefetchAllocB) / dt / 1048576.0 - PrefetchAllocMbPerSecond) * a;
                CpuCoresBusy += ((cpuTime - seenCpuTime).TotalSeconds / dt - CpuCoresBusy) * a;
            }
        }

        seenCpuTime = cpuTime;
        seenGen0 = g0;
        seenGen1 = g1;
        seenGen2 = g2;
        seenPauseMs = pauseMs;
        seenAllocBytes = alloc;
        seenMainAllocB = mainAlloc;
        seenNetAllocB = netAlloc;
        seenPrefetchAllocB = prefetchAlloc;
        gcSeenAt = now;
    }

    /// <summary>Average milliseconds per frame spent in each render stage.</summary>
    public static readonly double[] StageMs = new double[StageCount];

    // ---- worst frame breakdown -----------------------------------------------------
    // "schlechtester 72 ms" alone says a hitch exists but not where it went, and a spike's
    // cause is usually invisible in the smoothed averages precisely because it is rare.
    // Whenever a frame becomes the worst of the current peak window, its complete accounting
    // is snapshotted, and the published copy always describes the same frame MaxFrameMs
    // reports.

    /// <summary>Per-stage milliseconds of the frame MaxFrameMs refers to.</summary>
    public static readonly double[] WorstStageMs = new double[StageCount];
    public static double WorstGameTickMs { get; private set; }
    public static double WorstUploadMs { get; private set; }
    /// <summary>Time inside SwapBuffers during the worst frame - part of WorstOutsideMs.</summary>
    public static double WorstSwapMs { get; private set; }
    /// <summary>GC pause time that landed inside the worst frame. This does not add to the
    /// stage figures - a pause freezes whichever stage it interrupts, so a large value here
    /// says the inflated stage is the victim, not the culprit.</summary>
    public static double WorstGcPauseMs { get; private set; }
    /// <summary>Worst frame time outside every stage and the game tick: swap, frame limiter,
    /// driver back-pressure.</summary>
    public static double WorstOutsideMs { get; private set; }

    private static readonly double[] pendingStageMs = new double[StageCount];
    private static double pendingGameTickMs, pendingUploadMs, pendingGcPauseMs, pendingOutsideMs, pendingSwapMs;
    private static double prevGcPauseMs;
    private static int prevGen0 = -1, prevGen1 = -1, prevGen2 = -1;

    /// <summary>Reused every frame for the hitch log's bucket handover; HitchLog copies it
    /// only when the frame actually is a hitch.</summary>
    private static readonly double[] hitchBuckets = new double[HitchLog.BucketCount];

    /// <summary>The complete accounting of the frame that just set a new window peak.</summary>
    private static void SnapshotWorstFrame(double frameMs, double gcPauseMs)
    {
        double staged = 0;
        for (var i = 0; i < StageCount; i++)
        {
            pendingStageMs[i] = stageTicks[i] * TicksToMs;
            staged += pendingStageMs[i];
        }
        pendingGameTickMs = gameTickTicks * TicksToMs;
        pendingUploadMs = uploadMsThisFrame;
        pendingSwapMs = swapTicks * TicksToMs;
        pendingGcPauseMs = gcPauseMs;
        pendingOutsideMs = Math.Max(0, frameMs - staged - pendingGameTickMs);
    }

    private static void PublishWorstFrame()
    {
        Array.Copy(pendingStageMs, WorstStageMs, StageCount);
        WorstGameTickMs = pendingGameTickMs;
        WorstUploadMs = pendingUploadMs;
        WorstSwapMs = pendingSwapMs;
        WorstGcPauseMs = pendingGcPauseMs;
        WorstOutsideMs = pendingOutsideMs;
    }

    /// <summary>Registers a running total to be reported as a smoothed per-frame figure.</summary>
    public static SmoothedCounter TrackCounter(Func<long> read)
    {
        var counter = new SmoothedCounter(read);
        Counters.Add(counter);
        return counter;
    }

    /// <summary>
    /// Takes a counter back out. The list is static and the mod system re-registers its
    /// counters on every world join, so without this each rejoin left another set behind,
    /// all advanced every frame for the rest of the process.
    /// </summary>
    public static void Untrack(SmoothedCounter counter)
    {
        if (counter != null) Counters.Remove(counter);
    }

    public static void AddCullTicks(long ticks) => cullTicks += ticks;

    /// <summary>
    /// The part of the sweep the render thread spent waiting for its helper threads rather than
    /// culling. A sweep that is mostly wait is a scheduling problem, not an arithmetic one - the
    /// distinction that took two wrong diagnoses to start measuring.
    /// </summary>
    public static void AddCullWaitTicks(long ticks) => cullWaitTicks += ticks;

    /// <summary>Stopwatch ticks the sweep spent rebuilding pool caches this frame, and how many
    /// pools it rebuilt - the hitch log's answer to "was the long sweep sweeping or rebuilding".
    /// Reported by the optimising mod after each cull; never called in the baseline.</summary>
    public static void AddCullRebuild(long ticks, int pools)
    {
        cullRebuildTicks += ticks;
        cullRebuilds += pools;
    }
    public static void AddGameTickTicks(long ticks) => gameTickTicks += ticks;
    public static void AddSwapTicks(long ticks) => swapTicks += ticks;
    public static void AddUploadMs(double ms) => uploadMsThisFrame += ms;

    /// <summary>One mesh pool created inside this frame's upload drain, and what it cost.
    /// The upload figure already contains the time; this names the share.</summary>
    public static void AddPoolAlloc(double ms)
    {
        poolAllocMsThisFrame += ms;
        poolAllocsThisFrame++;
        StatPoolAllocs++;
        StatPoolAllocMs += ms;
        if (ms > MaxPoolAllocMs) MaxPoolAllocMs = ms;
    }
    private static double poolAllocMsThisFrame;
    private static int poolAllocsThisFrame;
    public static long StatPoolAllocs { get; private set; }
    public static double StatPoolAllocMs { get; private set; }
    public static double MaxPoolAllocMs { get; private set; }

    /// <summary>The debug overlay's own text-rebuild cost in this frame, so a hitch it caused
    /// names itself instead of reading as an engine ortho problem.</summary>
    public static void AddHudMs(double ms) => hudMsThisFrame += ms;

    /// <summary>Main-thread entity shape tesselation inside this frame. Booked separately
    /// because the world-join bursts land in the same "before" bucket as the chunk uploads
    /// and the liquid-depth pass - a 65 ms before-hitch needs to say which of them it was.</summary>
    public static void AddEntityTessMs(double ms) => entityTessMsThisFrame += ms;
    private static double entityTessMsThisFrame;

    /// <summary>
    /// One finished main-thread task (ClientMain.ExecuteMainThreadTasks) inside this frame:
    /// its code and cost. The frame keeps the total and the single heaviest task, so a
    /// "draussen" hitch can say "tasks 9,1 (readpacket33 8,2)" instead of nothing. Fed by
    /// the optimising mod's task-drain transcription; never called in the baseline.
    /// </summary>
    public static void AddMainThreadTask(string code, double ms)
    {
        mainTaskMsThisFrame += ms;
        if (ms > mainTaskTopMs) { mainTaskTopMs = ms; mainTaskTop = code; }
    }
    private static double mainTaskMsThisFrame, mainTaskTopMs;
    private static string mainTaskTop;

    /// <summary>Smoothed milliseconds per frame the main-thread task drain took.</summary>
    public static double AvgMainTaskMs { get; private set; }

    /// <summary>Main-thread entity loading (the budgeted half) inside this frame.</summary>
    public static void AddEntityLoadMs(double ms) => entityLoadMsThisFrame += ms;
    private static double entityLoadMsThisFrame;

    public static void AddStageTicks(int stage, long ticks)
    {
        if ((uint)stage < StageCount) stageTicks[stage] += ticks;
    }

    /// <summary>Exponential moving average that snaps to the first sample instead of crawling up from zero.</summary>
    private static double Blend(double current, double sample, bool first)
        => first ? sample : current + (sample - current) * Alpha;

    public static void BeginFrame()
        => Advance(Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration().TotalMilliseconds,
                   GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>
    /// The whole per-frame accounting, with the clock and the GC pause total as parameters so
    /// a test can feed synthetic frames of exact lengths instead of hoping the wall clock
    /// cooperates. The two-argument form leaves the collection counts unknown.
    /// </summary>
    internal static void Advance(long now, double gcPauseTotalMs)
        => Advance(now, gcPauseTotalMs, -1, -1, -1);

    internal static void Advance(long now, double gcPauseTotalMs, int gen0, int gen1, int gen2)
    {
        if (prevFrameTs != 0)
        {
            var first = TotalFrames == 0;
            TotalFrames++;

            var frameMs = (now - prevFrameTs) * TicksToMs;
            var cullMs = cullTicks * TicksToMs;
            var gcPauseMs = prevGcPauseMs > 0 ? Math.Max(0, gcPauseTotalMs - prevGcPauseMs) : 0;

            AvgFrameMs = Blend(AvgFrameMs, frameMs, first);
            AvgCullMs = Blend(AvgCullMs, cullMs, first);
            AvgUploadMs = Blend(AvgUploadMs, uploadMsThisFrame, first);
            AvgSwapMs = Blend(AvgSwapMs, swapTicks * TicksToMs, first);
            AvgMainTaskMs = Blend(AvgMainTaskMs, mainTaskMsThisFrame, first);
            LastSwapMs = swapTicks * TicksToMs;
            LastShadowMs = StageTickSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone,
                                        EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone);
            GameTickMs = Blend(GameTickMs, gameTickTicks * TicksToMs, first);
            for (var i = 0; i < StageCount; i++)
                StageMs[i] = Blend(StageMs[i], stageTicks[i] * TicksToMs, first);

            for (var i = 0; i < Counters.Count; i++) Counters[i].Advance(first, Alpha);

            // a collection boundary crossed inside this frame: read what it promoted
            if (gen0 >= 0 && prevGen0 >= 0 && (gen0 != prevGen0 || gen1 != prevGen1)) NoteEphemeralGc();

            if (sampleSeenAt == 0 || (now - sampleSeenAt) * TicksToMs >= SampleIntervalSeconds * 1000.0)
            {
                sampleSeenAt = now;
                SampleGc();
                PeriodicSample?.Invoke();
            }

            // Rolling peaks, so a single hitch does not stick to the display forever. The
            // breakdown snapshot must happen here, while this frame's buckets are still
            // intact - they are cleared a few lines further down.
            if (frameMs > frameMsPeak)
            {
                frameMsPeak = frameMs;
                SnapshotWorstFrame(frameMs, gcPauseMs);
            }
            if (cullMs > cullMsPeak) cullMsPeak = cullMs;
            if (uploadMsThisFrame > uploadMsPeak) uploadMsPeak = uploadMsThisFrame;

            if (++peakFrames >= PeakWindowFrames)
            {
                MaxFrameMs = frameMsPeak;
                MaxCullMs = cullMsPeak;
                MaxUploadMs = uploadMsPeak;
                PublishWorstFrame();
                peakFrames = 0;
                frameMsPeak = cullMsPeak = uploadMsPeak = 0;
            }
            else
            {
                if (frameMsPeak > MaxFrameMs)
                {
                    MaxFrameMs = frameMsPeak;
                    PublishWorstFrame();
                }
                if (cullMsPeak > MaxCullMs) MaxCullMs = cullMsPeak;
                if (uploadMsPeak > MaxUploadMs) MaxUploadMs = uploadMsPeak;
            }

            // Hitch accounting, while this frame's buckets are still intact. Warmup frames
            // are skipped: the first frames after joining are all "hitches" against an
            // average that does not exist yet. AvgFrameMs already contains this frame at
            // 1/64 weight, which moves the threshold by well under a percent.
            if (TotalFrames > WarmupFrames)
            {
                double stagedMs = 0;
                for (var i = 0; i < StageCount; i++) stagedMs += stageTicks[i] * TicksToMs;
                var tickMs = gameTickTicks * TicksToMs;
                var swapMs = swapTicks * TicksToMs;
                var outsideMs = Math.Max(0, frameMs - stagedMs - tickMs);

                hitchBuckets[HitchLog.Before] = stageTicks[(int)EnumRenderStage.Before] * TicksToMs;
                hitchBuckets[HitchLog.Schatten] = StageTickSum(EnumRenderStage.ShadowFar, EnumRenderStage.ShadowFarDone,
                                                              EnumRenderStage.ShadowNear, EnumRenderStage.ShadowNearDone);
                hitchBuckets[HitchLog.Opaque] = stageTicks[(int)EnumRenderStage.Opaque] * TicksToMs;
                hitchBuckets[HitchLog.Oit] = stageTicks[(int)EnumRenderStage.OIT] * TicksToMs;
                hitchBuckets[HitchLog.Post] = StageTickSum(EnumRenderStage.AfterOIT, EnumRenderStage.AfterPostProcessing,
                                                           EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit);
                hitchBuckets[HitchLog.Ortho] = stageTicks[(int)EnumRenderStage.Ortho] * TicksToMs;
                hitchBuckets[HitchLog.Done] = stageTicks[(int)EnumRenderStage.Done] * TicksToMs;
                hitchBuckets[HitchLog.Tick] = tickMs;
                hitchBuckets[HitchLog.Swap] = swapMs;
                hitchBuckets[HitchLog.Draussen] = Math.Max(0, outsideMs - swapMs);

                // Which GC generation ran during this frame - the difference between "raise
                // the nursery" and "hunt gen2 promotion" when the pauses get hunted later.
                string gcTag = null;
                if (prevGen0 >= 0 && gen0 >= 0)
                    gcTag = HitchLog.GcGenTag(gen0 - prevGen0, gen1 - prevGen1, gen2 - prevGen2);

                HitchLog.OnFrame(frameMs, AvgFrameMs, gcPauseMs, hitchBuckets, gcTag,
                                 cullMs, uploadMsThisFrame, cullWaitTicks * TicksToMs,
                                 hudMsThisFrame, entityTessMsThisFrame,
                                 cullRebuildTicks * TicksToMs, cullRebuilds,
                                 mainTaskMsThisFrame, mainTaskTop, mainTaskTopMs,
                                 entityLoadMsThisFrame, poolAllocMsThisFrame, poolAllocsThisFrame);

                // The finished frame's totals for anyone steering off them (the upload
                // budget's frame-pressure input). Same warmup gate as the hitch log: the
                // first frames after a join all look choked against a newborn average.
                FrameSummary?.Invoke(frameMs, AvgFrameMs, gcPauseMs, uploadMsThisFrame);
            }

            if (TotalFrames >= WarmupFrames) HasData = true;
        }
        else
        {
            for (var i = 0; i < Counters.Count; i++) Counters[i].Rebase();
        }

        cullTicks = 0;
        cullWaitTicks = 0;
        cullRebuildTicks = 0;
        cullRebuilds = 0;
        gameTickTicks = 0;
        swapTicks = 0;
        uploadMsThisFrame = 0;
        poolAllocMsThisFrame = 0;
        poolAllocsThisFrame = 0;
        hudMsThisFrame = 0;
        entityTessMsThisFrame = 0;
        mainTaskMsThisFrame = 0;
        mainTaskTopMs = 0;
        mainTaskTop = null;
        entityLoadMsThisFrame = 0;
        Array.Clear(stageTicks, 0, StageCount);
        prevFrameTs = now;
        prevGcPauseMs = gcPauseTotalMs;
        prevGen0 = gen0;
        prevGen1 = gen1;
        prevGen2 = gen2;
    }

    // ---- stage aggregates ----------------------------------------------------------
    // Shared here so the HUD and the .komet text cannot drift apart on what "schatten" or
    // "ausserhalb" means.

    /// <summary>Both shadow cascades including their Done stages.</summary>
    public static double ShadowMs => StageSum(StageMs);

    /// <summary>SSAO, god rays, colour grading and the blit - AfterOIT through AfterBlit.</summary>
    public static double PostComposeMs => PostSum(StageMs);

    /// <summary>Frame time that belongs to no render stage and no game tick: swap, frame
    /// limiter, driver back-pressure.</summary>
    public static double OutsideStagesMs
    {
        get
        {
            double staged = 0;
            for (var i = 0; i < StageCount; i++) staged += StageMs[i];
            return AvgFrameMs - staged - GameTickMs;
        }
    }

    /// <summary>The same two sums over the worst frame's buckets.</summary>
    public static double WorstShadowMs => StageSum(WorstStageMs);
    public static double WorstPostComposeMs => PostSum(WorstStageMs);

    private static double StageTickSum(EnumRenderStage a, EnumRenderStage b, EnumRenderStage c, EnumRenderStage d)
        => (stageTicks[(int)a] + stageTicks[(int)b] + stageTicks[(int)c] + stageTicks[(int)d]) * TicksToMs;

    private static double StageSum(double[] ms)
        => ms[(int)Vintagestory.API.Client.EnumRenderStage.ShadowFar]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.ShadowFarDone]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.ShadowNear]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.ShadowNearDone];

    private static double PostSum(double[] ms)
        => ms[(int)Vintagestory.API.Client.EnumRenderStage.AfterOIT]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.AfterPostProcessing]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.AfterFinalComposition]
         + ms[(int)Vintagestory.API.Client.EnumRenderStage.AfterBlit];

    public static void Reset()
    {
        StatPoolAllocs = 0;
        StatPoolAllocMs = 0;
        MaxPoolAllocMs = 0;
        HasData = false;
        TotalFrames = 0;
        prevFrameTs = 0;
        sampleSeenAt = 0;
        promotedBytesAccum = 0;
        gcSeenAccum = 0;
        prevGcPauseMs = 0;
        prevGen0 = prevGen1 = prevGen2 = -1;
        cullTicks = cullWaitTicks = cullRebuildTicks = gameTickTicks = swapTicks = 0;
        cullRebuilds = 0;
        uploadMsThisFrame = 0;
        poolAllocMsThisFrame = 0;
        poolAllocsThisFrame = 0;
        hudMsThisFrame = 0;
        entityTessMsThisFrame = 0;
        mainTaskMsThisFrame = mainTaskTopMs = 0;
        mainTaskTop = null;
        entityLoadMsThisFrame = 0;
        AvgMainTaskMs = 0;
        peakFrames = 0;
        frameMsPeak = cullMsPeak = uploadMsPeak = 0;
        Array.Clear(stageTicks, 0, StageCount);
        Array.Clear(StageMs, 0, StageCount);
        Array.Clear(pendingStageMs, 0, StageCount);
        Array.Clear(WorstStageMs, 0, StageCount);
        pendingGameTickMs = pendingUploadMs = pendingGcPauseMs = pendingOutsideMs = pendingSwapMs = 0;
        WorstGameTickMs = WorstUploadMs = WorstGcPauseMs = WorstOutsideMs = WorstSwapMs = 0;
        AvgFrameMs = MaxFrameMs = AvgCullMs = MaxCullMs = AvgUploadMs = MaxUploadMs = GameTickMs = AvgSwapMs = 0;
        LastSwapMs = LastShadowMs = 0;
        for (var i = 0; i < Counters.Count; i++) Counters[i].Rebase();
    }
}
