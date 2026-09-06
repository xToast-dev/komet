using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Komet.Runtime;

/// <summary>
/// Work handed to the scheduler as a batch. One call per contiguous slice of the index range,
/// so the per-call overhead is paid once per slice instead of once per item.
/// </summary>
public interface IWorkBody
{
    void Run(int from, int to);
}

/// <summary>
/// What Komet itself runs off the main thread. These are the workloads this mod OWNS - not the
/// pipeline stages it merely observes.
///
/// Chunk generation is the integrated server's worldgen threads, chunk loading is the network
/// thread plus main-thread tasks, and terrain tesselation, meshing and the GPU upload all live
/// on engine threads this mod cannot schedule: the tesselator is one thread by construction
/// (BlockEntity.OnTesselation is a public extension point implemented by every content mod
/// against a single-thread contract) and the upload needs the GL context. What Komet can and
/// does move off the critical path is the work below - and the monitor names exactly that
/// rather than inventing states for stages it does not run.
/// </summary>
public enum JobKind : byte
{
    /// <summary>The per-frame visibility sweep. Fired three times a frame from the render
    /// thread, which blocks on it - the one genuinely latency-critical workload here.</summary>
    Cull = 0,

    /// <summary>The tesselator's 34x34x34 block window, built one chunk ahead. Feeds the one
    /// thread that is the loading bottleneck, so it outranks everything except the sweep.</summary>
    MeshPrep = 1,

    /// <summary>Neighbour chunks decompressed ahead of the tesselator.</summary>
    ChunkPrep = 2,

    /// <summary>The occlusion raywalk. Runs a few times a second and takes milliseconds, so it
    /// is sliced finely and scheduled behind anything the frame is waiting for.</summary>
    Occlusion = 3,

    /// <summary>The F7 overlay's cairo raster.</summary>
    Hud = 4,

    /// <summary>Animation frame prewarm. Hundreds of milliseconds per shape and nothing waits
    /// on it - the one workload that only runs when there is genuinely nothing else.</summary>
    Warmup = 5,
}

/// <summary>Scan order for the queues. Lower runs first; <see cref="JobScheduler"/> breaks the
/// strict order periodically so nothing at the bottom can starve.</summary>
public enum JobPriority : byte
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Background = 3,
    Idle = 4,
}

/// <summary>What a worker is doing right now, for the live monitor.</summary>
public enum WorkerState : byte
{
    Idle = 0,
    Waiting = 1,
    Culling = 2,
    Meshing = 3,
    Loading = 4,
    Occluding = 5,
    Rastering = 6,
    Warming = 7,
    Parked = 8,
}

/// <summary>
/// One dynamic worker pool for every CPU-heavy job Komet owns.
///
/// It replaces four independent thread sets that between them put eleven threads on this
/// machine's six physical cores: five cull helpers, four occlusion helpers, one window
/// prebuilder and one unpack prefetcher, plus whatever the shared ThreadPool handed the
/// animation prewarm and the HUD raster. Each set sized itself against the core count in
/// ignorance of the others, none of them could lend a thread to another, and the two that
/// mattered most - the sweep on the frame's deadline and the occlusion walk that holds cores
/// for milliseconds - collided often enough that a whole niceness mechanism was built to keep
/// them apart. A single pool that schedules by priority solves that directly: a worker
/// finishes its current slice (tens of microseconds - both batch workloads were already sliced
/// that finely) and takes the sweep ticket next, wherever it happens to be sitting.
///
/// Two shapes of work share the pool:
///
///   * <see cref="RunBatch"/> - fork/join over an index range, the caller blocks. This is the
///     sweep and the occlusion walk. Slices are handed out dynamically through one interlocked
///     counter, so an expensive slice does not leave the others idle, and the CALLER drains
///     too, which is what makes a small batch finish before a helper has even woken.
///   * <see cref="Submit"/> - fire and forget with a dedup key. This is the window prebuild,
///     the neighbour unpack, the HUD raster and the animation prewarm.
///
/// Completion of a batch is counted in WORK, not in workers: the caller returns as soon as
/// every item has run, whether or not every ticket holder has woken up. That distinction was
/// paid for once already - the hitch log showed 9,7-11 ms sweep waits with no GC pause that
/// were nothing but the render thread waiting for a helper to be scheduled so it could
/// discover there was nothing left to do.
/// </summary>
public static class JobScheduler
{
    /// <summary>Hard ceiling. Nothing here scales past a handful of threads: both batch
    /// workloads are memory-bound linear scans and the rest is latency work.</summary>
    public const int MaxWorkers = 16;

    private const int QueueCount = 5;

    // ---- configuration -------------------------------------------------------------

    /// <summary>Worker threads to create. 0 = derive from the machine.</summary>
    public static int ConfiguredWorkers;

    /// <summary>
    /// Unix nice increment applied to the LAST workers of the pool, which then decline
    /// Critical and High jobs. Not a workload assignment: those workers still serve every
    /// background workload, and the rest of the pool still serves every workload including the
    /// background ones. It exists because nice is a one-way door for an unprivileged thread -
    /// setpriority can raise it and never lower it again - so a worker that has gone nice can
    /// no longer be trusted with the frame's deadline. 0 (the default) makes the pool fully
    /// symmetric.
    /// </summary>
    public static int Niceness;

    /// <summary>Every Nth take starts at the BOTTOM of the queues instead of the top, which is
    /// what bounds the wait of a job nothing else would ever let through. One counter
    /// increment on the hot path; see <see cref="StartQueueFor"/>.</summary>
    internal const int AntiStarvationEvery = 32;

    // ---- statistics ----------------------------------------------------------------

    public static long StatCompleted, StatCancelled, StatDuplicates, StatBatches, StatInline;

    /// <summary>Batches that ran on the calling thread because a ticket holder from the
    /// previous batch had not checked back in yet - bounded, self-healing contention.</summary>
    public static long StatContendedInline;

    /// <summary>Stopwatch ticks a batch's CALLER spent waiting after running out of work
    /// itself. The number that separates "the sweep is expensive" from "the sweep is stuck
    /// behind somebody else".</summary>
    public static long StatWaitTicks;

    private static readonly long[] KindTicks = new long[6];
    private static readonly long[] KindJobs = new long[6];
    private static readonly long[] KindPeakTicks = new long[6];
    private static readonly int[] Queued = new int[6];

    /// <summary>Jobs finished in the last folded second, per kind, and the totals the monitor
    /// prints. Folded by <see cref="Sample"/> at the frame boundary, like every other rate in
    /// this mod.</summary>
    public static double JobsPerSecond { get; private set; }

    private static long seenCompleted, seenAtTimestamp;
    private static long busyTicksTotal, seenBusyTicks;

    /// <summary>Share of the pool's wall time actually spent running jobs, 0..1.</summary>
    public static double Utilisation { get; private set; }

    public static int PendingJobs
    {
        get
        {
            var n = 0;
            for (var i = 0; i < QueueCount; i++) n += Queues[i].Count;
            return n;
        }
    }

    public static int QueuedOf(JobKind kind) => Volatile.Read(ref Queued[(int)kind]);
    public static long JobsOf(JobKind kind) => Interlocked.Read(ref KindJobs[(int)kind]);

    public static double AvgMsOf(JobKind kind)
    {
        var n = Interlocked.Read(ref KindJobs[(int)kind]);
        return n <= 0 ? 0 : Interlocked.Read(ref KindTicks[(int)kind]) * 1000.0 / Stopwatch.Frequency / n;
    }

    public static double PeakMsOf(JobKind kind)
        => Interlocked.Read(ref KindPeakTicks[(int)kind]) * 1000.0 / Stopwatch.Frequency;

    // ---- the job ------------------------------------------------------------------

    private sealed class Job
    {
        public JobKind Kind;
        public Action Work;
        public Action<long> KeyedWork;
        public Batch Batch;
        public long Key;
        public int Generation;
        public long EnqueuedAt;
    }

    /// <summary>A fork/join range. One per calling thread, reused - see <see cref="RunBatch"/>
    /// for why reuse is safe and what happens when it is not.</summary>
    private sealed class Batch
    {
        public IWorkBody Body;
        public int Count, Chunk;
        public int NextIndex, ItemsDone;
        public int Outstanding;
        public Exception Failure;
        public readonly ManualResetEventSlim Done = new(false, 400);
    }

    private static readonly ConcurrentQueue<Job>[] Queues = CreateQueues();
    private static readonly ConcurrentQueue<Job> FreeJobs = new();
    private static readonly ConcurrentDictionary<long, byte>[] InFlight = CreateInFlight();
    private static readonly int[] Generations = new int[6];

    private static ConcurrentQueue<Job>[] CreateQueues()
    {
        var q = new ConcurrentQueue<Job>[QueueCount];
        for (var i = 0; i < QueueCount; i++) q[i] = new ConcurrentQueue<Job>();
        return q;
    }

    private static ConcurrentDictionary<long, byte>[] CreateInFlight()
    {
        var d = new ConcurrentDictionary<long, byte>[6];
        for (var i = 0; i < d.Length; i++) d[i] = new ConcurrentDictionary<long, byte>();
        return d;
    }

    /// <summary>The queue a kind goes into. Fixed rather than per-call, so a workload cannot
    /// quietly promote itself past the frame's own work.</summary>
    internal static JobPriority PriorityOf(JobKind kind) => kind switch
    {
        JobKind.Cull => JobPriority.Critical,
        JobKind.MeshPrep => JobPriority.High,
        JobKind.ChunkPrep => JobPriority.Normal,
        JobKind.Occlusion => JobPriority.Background,
        JobKind.Hud => JobPriority.Background,
        _ => JobPriority.Idle,
    };

    // ---- workers -------------------------------------------------------------------

    private sealed class Worker
    {
        public Thread Thread;
        public readonly ManualResetEventSlim Gate = new(false, 200);
        public volatile int State;      // WorkerState
        public volatile int Kind;       // JobKind of the running job, -1 when idle
        public long StartedAt;
        public long Key = long.MinValue;
        public long BusyTicks;
        public long Jobs;
        public bool Nice;
    }

    private static Worker[] workers = Array.Empty<Worker>();
    private static readonly object startLock = new();
    private static volatile bool shutdown;

    /// <summary>Workers allowed to take jobs right now. Everything above this index parks on a
    /// long wait instead of exiting - shrinking the pool must not cost a thread teardown, and
    /// growing it back must not cost a thread creation inside a frame.</summary>
    private static volatile int activeTarget;

    /// <summary>Bit per parked worker, so a submit wakes exactly one instead of all of
    /// them.</summary>
    private static int idleMask;

    private static int takeCounter;

    public static int WorkerCount => workers.Length;
    public static int ActiveWorkers => Math.Min(activeTarget, workers.Length);

    public static int BusyWorkers
    {
        get
        {
            var w = workers;
            var n = 0;
            for (var i = 0; i < w.Length; i++)
                if (w[i].State is not ((int)WorkerState.Idle or (int)WorkerState.Parked)) n++;
            return n;
        }
    }

    /// <summary>
    /// Brings the pool up. Idempotent. Threads are created once and live for the process; the
    /// adaptive sizing moves <see cref="activeTarget"/>, it never creates or destroys threads.
    /// </summary>
    public static void Start(int configured, int niceness)
    {
        lock (startLock)
        {
            if (workers.Length > 0 || shutdown) return;
            ConfiguredWorkers = configured;
            Niceness = niceness;

            var count = Math.Clamp(configured > 0 ? configured : AutoWorkers(CpuTopology.PhysicalCores),
                                   1, MaxWorkers);
            var nice = Math.Clamp(niceness > 0 ? count / 2 : 0, 0, count - 1);

            var w = new Worker[count];
            for (var i = 0; i < count; i++) w[i] = new Worker { State = (int)WorkerState.Idle, Kind = -1, Nice = i >= count - nice };
            workers = w;
            activeTarget = count;

            for (var i = 0; i < count; i++)
            {
                var index = i;
                w[i].Thread = new Thread(() => Loop(index)) { IsBackground = true, Name = "komet-worker-" + i };
                w[i].Thread.Start();
            }
        }
    }

    public static void Stop()
    {
        lock (startLock)
        {
            shutdown = true;
            var w = workers;
            for (var i = 0; i < w.Length; i++) w[i].Gate.Set();
        }
    }

    /// <summary>
    /// Tears the pool down and lets a later <see cref="Start"/> build a different one. Only the
    /// verify harness uses it - the game starts one pool per process - and it exists because
    /// the properties worth checking (thread count, niceness, the inline path with no workers)
    /// are set at Start and cannot otherwise be exercised in one run.
    /// </summary>
    internal static void StopForTest()
    {
        Stop();
        var w = workers;
        foreach (var t in w)
            try { t.Thread?.Join(2000); } catch (Exception) { /* nothing to salvage in a test teardown */ }
        lock (startLock)
        {
            workers = Array.Empty<Worker>();
            activeTarget = 0;
            idleMask = 0;
            shutdown = false;
        }
        for (var i = 0; i < QueueCount; i++) while (Queues[i].TryDequeue(out _)) { }
        foreach (var d in InFlight) d.Clear();
        while (ToMain.TryDequeue(out _)) { }
        // Queued[] is decremented where a job is EXECUTED, so draining the queues behind the
        // workers' backs is the one path that can leave it standing.
        for (var i = 0; i < Queued.Length; i++) Volatile.Write(ref Queued[i], 0);
        PriorityLowered = false;
        ResetStats();
    }

    /// <summary>
    /// Physical cores rather than hardware threads, minus one for the render thread and one for
    /// the engine's tesselation thread - the two this pool must never take a core from, and the
    /// two whose stalls this mod exists to remove. Both batch workloads are memory-bound linear
    /// scans, so a second worker on the same core's SMT sibling adds queueing, not throughput.
    /// Zero is not a legitimate answer here the way it was for a single-purpose set: the pool
    /// also carries work nobody else will do, so it keeps one worker on the smallest machine.
    /// </summary>
    internal static int AutoWorkers(int physicalCores) => Math.Clamp(physicalCores - 2, 1, 8);

    // ---- adaptive sizing ------------------------------------------------------------

    /// <summary>
    /// How many of the pool's workers may take jobs, from what the last second looked like.
    ///
    /// Pure so the rule can be checked without a machine. The shape: start from what the
    /// hardware allows, give a core back when the main thread is visibly suffering, and take
    /// it again only when there is a queue deep enough to justify it. A pool that grows on
    /// utilisation alone grows during exactly the frames that are already late.
    /// </summary>
    internal static int TargetWorkers(int poolSize, int pending, double utilisation,
                                      double frameMs, double avgFrameMs, int tessBacklog)
    {
        if (poolSize <= 1) return poolSize;
        var target = poolSize;

        // Main-thread pressure. A frame well past the rolling average while the pool is busy
        // is the pool competing with the render thread for cores; hand one back. Never below
        // half the pool - the work still has to happen, and starving it moves the stall into
        // the loading front instead of removing it.
        if (avgFrameMs > 0 && frameMs > avgFrameMs * 1.5 && utilisation > 0.5)
            target = Math.Max((poolSize + 1) / 2, poolSize - 1);

        // Chunk loading pressure outranks that: a deep tesselation backlog means the window
        // prebuild and the unpack prefetch are on the critical path of everything the player
        // is waiting for, and those are the jobs that fill the pool.
        if (tessBacklog > 512 && pending > 0) target = poolSize;

        // Nothing queued and nothing running: no reason to hold the whole pool awake.
        if (pending == 0 && utilisation < 0.05) target = Math.Max(1, poolSize - 1);

        return Math.Clamp(target, 1, poolSize);
    }

    /// <summary>Called at the frame boundary. Folds the rates the monitor prints and moves the
    /// active worker count.</summary>
    public static void Sample(double frameMs, double avgFrameMs, int tessBacklog)
    {
        var now = Stopwatch.GetTimestamp();
        var completed = Interlocked.Read(ref StatCompleted);
        var busy = Interlocked.Read(ref busyTicksTotal);

        if (seenAtTimestamp != 0)
        {
            var seconds = (now - seenAtTimestamp) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                var rate = (completed - seenCompleted) / seconds;
                JobsPerSecond += (rate - JobsPerSecond) * 0.25;
                if (JobsPerSecond < 0.05) JobsPerSecond = 0;

                var n = Math.Max(1, ActiveWorkers);
                var share = (busy - seenBusyTicks) / (double)Stopwatch.Frequency / seconds / n;
                Utilisation += (Math.Clamp(share, 0, 1) - Utilisation) * 0.4;
                if (Utilisation < 0.005) Utilisation = 0;
            }
        }
        seenCompleted = completed;
        seenBusyTicks = busy;
        seenAtTimestamp = now;

        var w = workers;
        if (w.Length == 0) return;
        var want = TargetWorkers(w.Length, PendingJobs, Utilisation, frameMs, avgFrameMs, tessBacklog);
        if (want == activeTarget) return;
        activeTarget = want;
        // A worker that just became allowed to work is parked on its gate; wake the lot so the
        // change takes effect this second rather than at the next submit.
        for (var i = 0; i < w.Length; i++) w[i].Gate.Set();
    }

    // ---- submitting ----------------------------------------------------------------

    /// <summary>
    /// Queues one job. <paramref name="key"/> deduplicates: while a job with the same key and
    /// kind is queued or running, a second submit is refused and counted rather than doubling
    /// the work. long.MinValue means "no key" and never deduplicates.
    ///
    /// Returns false when the job was refused (duplicate, or the pool is not up).
    /// </summary>
    public static bool Submit(JobKind kind, long key, Action work)
        => Enqueue(kind, key, work, null);

    /// <summary>
    /// The same, for a body that works on the key itself. The key is handed to the job rather
    /// than read from shared state when it runs: a submitter that reuses one body across many
    /// keys would otherwise have a stale job pick up a newer key, do that one twice and skip
    /// the one it was queued for.
    /// </summary>
    public static bool Submit(JobKind kind, long key, Action<long> work)
        => Enqueue(kind, key, null, work);

    private static bool Enqueue(JobKind kind, long key, Action work, Action<long> keyed)
    {
        if ((work == null && keyed == null) || shutdown || workers.Length == 0) return false;
        var k = (int)kind;
        if (key != long.MinValue && !InFlight[k].TryAdd(key, 0))
        {
            Interlocked.Increment(ref StatDuplicates);
            return false;
        }

        if (!FreeJobs.TryDequeue(out var job)) job = new Job();
        job.Kind = kind;
        job.Work = work;
        job.KeyedWork = keyed;
        job.Batch = null;
        job.Key = key;
        job.Generation = Volatile.Read(ref Generations[k]);
        job.EnqueuedAt = Stopwatch.GetTimestamp();

        Interlocked.Increment(ref Queued[k]);
        Queues[(int)PriorityOf(kind)].Enqueue(job);
        WakeOne();
        return true;
    }

    /// <summary>
    /// Drops every queued job of a kind that has not started yet, and makes running ones
    /// irrelevant to the next generation. Used when the world goes away: a job holding a chunk
    /// reference from the world being torn down must not run against the next one.
    /// </summary>
    public static void CancelKind(JobKind kind)
    {
        Interlocked.Increment(ref Generations[(int)kind]);
        InFlight[(int)kind].Clear();
    }

    /// <summary>True while a job with this key is queued or running.</summary>
    public static bool IsQueued(JobKind kind, long key) => InFlight[(int)kind].ContainsKey(key);

    // ---- fork/join -----------------------------------------------------------------

    /// <summary>
    /// One reusable batch per CALLING thread. Only two threads ever fire batches (the render
    /// thread for the sweep, the occlusion caller for the walk) and neither is re-entrant, so a
    /// per-thread instance removes both the allocation and every question about which batch a
    /// straggling ticket is looking at.
    /// </summary>
    [ThreadStatic] private static Batch localBatch;

    /// <summary>
    /// Runs <paramref name="count"/> items across the pool AND the calling thread, and returns
    /// once every one of them is done.
    ///
    /// Slices are handed out dynamically rather than partitioned up front: pools differ in size
    /// by two orders of magnitude, and a strided partition leaves threads idle while one of
    /// them finishes the big one.
    /// </summary>
    public static void RunBatch(IWorkBody body, int count, int chunkSize, JobKind kind)
    {
        if (body == null || count <= 0) return;
        if (chunkSize < 1) chunkSize = 1;

        var w = workers;
        if (w.Length == 0 || count <= chunkSize)
        {
            Interlocked.Increment(ref StatInline);
            RunInline(body, count);
            return;
        }

        var b = localBatch ??= new Batch();

        // A ticket holder from this thread's previous batch has not checked back in. It cannot
        // be holding work - the previous call only returned once every item was done - but it
        // WILL touch the batch when it finally wakes, so the fields must not be rewritten under
        // it. Waiting here would only move the stall; the batch runs inline instead, which is
        // bounded and self-heals the moment the sleeper gets a core.
        if (Volatile.Read(ref b.Outstanding) != 0)
        {
            Interlocked.Increment(ref StatContendedInline);
            RunInline(body, count);
            return;
        }

        Interlocked.Increment(ref StatBatches);

        b.Count = count;
        b.Chunk = chunkSize;
        b.Failure = null;
        Volatile.Write(ref b.ItemsDone, 0);
        Volatile.Write(ref b.NextIndex, 0);
        b.Done.Reset();
        b.Body = body;

        // One ticket per worker that could plausibly help, never more than there are slices.
        var slices = (count + chunkSize - 1) / chunkSize;
        var tickets = Math.Min(ActiveWorkers, slices - 1);
        if (tickets > 0)
        {
            Volatile.Write(ref b.Outstanding, tickets);
            var queue = Queues[(int)PriorityOf(kind)];
            var gen = Volatile.Read(ref Generations[(int)kind]);
            for (var i = 0; i < tickets; i++)
            {
                if (!FreeJobs.TryDequeue(out var job)) job = new Job();
                job.Kind = kind;
                job.Work = null;
                job.Batch = b;
                job.Key = long.MinValue;
                job.Generation = gen;
                job.EnqueuedAt = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref Queued[(int)kind]);
                queue.Enqueue(job);
            }
            WakeAll();
        }

        // The caller is a worker too - on a small batch it finishes the whole thing before the
        // first ticket holder has even woken.
        try
        {
            Drain(b);
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref b.Failure, e, null);
            AbandonRemaining(b);
        }

        // Waits for the WORK, not for the workers: every claimed slice is finished and counted,
        // or this never fires.
        var t0 = Stopwatch.GetTimestamp();
        b.Done.Wait();
        Interlocked.Add(ref StatWaitTicks, Stopwatch.GetTimestamp() - t0);

        b.Body = null;
        var failed = b.Failure;
        if (failed != null) throw new InvalidOperationException("komet job batch failed", failed);
    }

    private static void RunInline(IWorkBody body, int count)
    {
        try
        {
            body.Run(0, count);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("komet job batch failed", e);
        }
    }

    private static void Drain(Batch b)
    {
        int n = b.Count, c = b.Chunk;
        var body = b.Body;
        while (true)
        {
            var from = Interlocked.Add(ref b.NextIndex, c) - c;
            if (from >= n) return;
            var to = from + c;
            if (to > n) to = n;
            // The item count advances even when the slice throws: a claimed slice that never
            // counted would leave the caller waiting on work that no longer exists.
            try
            {
                body.Run(from, to);
            }
            finally
            {
                if (Interlocked.Add(ref b.ItemsDone, to - from) == n) b.Done.Set();
            }
        }
    }

    /// <summary>Claims and counts every remaining slice WITHOUT running it. Only after a slice
    /// failed - the batch's result is void anyway, and without this a batch in which every
    /// participant failed would strand unclaimed items and hang the caller forever.</summary>
    private static void AbandonRemaining(Batch b)
    {
        int n = b.Count, c = b.Chunk;
        while (true)
        {
            var from = Interlocked.Add(ref b.NextIndex, c) - c;
            if (from >= n) return;
            var to = from + c;
            if (to > n) to = n;
            if (Interlocked.Add(ref b.ItemsDone, to - from) == n) b.Done.Set();
        }
    }

    // ---- the worker loop -----------------------------------------------------------

    /// <summary>
    /// Which queue a take starts scanning at. Strict priority would let the bottom of the pool
    /// wait forever behind a sweep that fires three times a frame, so every Nth take starts at
    /// the bottom instead. Pure and cheap - one counter - and it bounds the wait of an Idle job
    /// at N takes rather than at "whenever the machine goes quiet".
    /// </summary>
    internal static bool StartsAtBottom(int takeNumber) => takeNumber % AntiStarvationEvery == 0;

    private static Job Take(bool nice)
    {
        var n = Interlocked.Increment(ref takeCounter);
        // A nice worker can no longer respond on the frame's deadline (nice is a one-way door
        // for an unprivileged thread), so it does not pretend to: it starts below the two
        // queues the frame waits on.
        var floor = nice ? (int)JobPriority.Normal : 0;

        if (StartsAtBottom(n))
        {
            for (var p = QueueCount - 1; p >= floor; p--)
                if (Queues[p].TryDequeue(out var job)) return job;
            return null;
        }

        for (var p = floor; p < QueueCount; p++)
            if (Queues[p].TryDequeue(out var job)) return job;
        return null;
    }

    private static void WakeOne()
    {
        var w = workers;
        var mask = Volatile.Read(ref idleMask);
        var limit = Math.Min(activeTarget, w.Length);
        for (var i = 0; i < limit; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            // Clearing the bit here is a hint, not a handshake: the worker clears it too. A
            // lost race wakes one more worker than needed, which finds an empty queue and
            // parks again.
            Interlocked.And(ref idleMask, ~(1 << i));
            w[i].Gate.Set();
            return;
        }
        // Nobody parked: every active worker is running and will re-check the queues when it
        // finishes, so there is nothing to wake.
    }

    private static void WakeAll()
    {
        var w = workers;
        var limit = Math.Min(activeTarget, w.Length);
        for (var i = 0; i < limit; i++) w[i].Gate.Set();
    }

    private static void Loop(int index)
    {
        var self = workers[index];
        if (self.Nice) LowerOwnPriority();

        while (!shutdown)
        {
            if (index >= activeTarget)
            {
                self.State = (int)WorkerState.Parked;
                self.Gate.Wait(50);
                self.Gate.Reset();
                continue;
            }

            var job = Take(self.Nice);
            if (job == null)
            {
                self.State = (int)WorkerState.Idle;
                self.Kind = -1;
                Interlocked.Or(ref idleMask, 1 << index);
                // Re-check after publishing the idle bit: a submit that ran between the failed
                // take and the bit becoming visible would otherwise find nobody to wake.
                job = Take(self.Nice);
                if (job == null)
                {
                    self.Gate.Wait();
                    self.Gate.Reset();
                    Interlocked.And(ref idleMask, ~(1 << index));
                    continue;
                }
                Interlocked.And(ref idleMask, ~(1 << index));
            }

            Execute(self, job);
        }
        self.State = (int)WorkerState.Parked;
    }

    private static void Execute(Worker self, Job job)
    {
        var kind = job.Kind;
        var k = (int)kind;
        Interlocked.Decrement(ref Queued[k]);

        var stale = job.Batch == null && job.Generation != Volatile.Read(ref Generations[k]);
        var t0 = Stopwatch.GetTimestamp();

        self.Kind = k;
        self.Key = job.Key;
        self.StartedAt = t0;
        self.State = (int)StateFor(kind);

        try
        {
            if (job.Batch != null)
            {
                // A batch ticket is never cancelled, whatever generation it carries: its caller
                // is blocked on it right now and needs those slices run. CancelKind is about
                // fire-and-forget work whose world went away - a fork/join caller is neither,
                // and dropping its slices would hand it a half-culled frame rather than a late
                // one. (The first version cancelled these too and counted the items out
                // instead; the stress test's cancel storm then produced batches with slices
                // that had silently never run.)
                try { Drain(job.Batch); }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref job.Batch.Failure, e, null);
                    AbandonRemaining(job.Batch);
                }
            }
            else if (stale)
            {
                Interlocked.Increment(ref StatCancelled);
            }
            else
            {
                if (job.Work != null) job.Work();
                else job.KeyedWork(job.Key);
                Interlocked.Increment(ref StatCompleted);
            }
        }
        catch (Exception)
        {
            // A failed standalone job must never take a worker with it: the pool is an
            // accelerator for every one of its workloads, and each of them already treats a
            // missing result as "do it the slow way".
            Interlocked.Increment(ref StatCancelled);
        }
        finally
        {
            var dt = Stopwatch.GetTimestamp() - t0;
            Interlocked.Add(ref KindTicks[k], dt);
            Interlocked.Increment(ref KindJobs[k]);
            Interlocked.Add(ref busyTicksTotal, dt);
            self.BusyTicks += dt;
            self.Jobs++;
            var peak = Interlocked.Read(ref KindPeakTicks[k]);
            if (dt > peak) Interlocked.Exchange(ref KindPeakTicks[k], dt);

            // The ticket leaves the batch AFTER its last item is counted, which is what the
            // next batch from that caller checks before reusing the object.
            if (job.Batch != null) Interlocked.Decrement(ref job.Batch.Outstanding);
            if (job.Key != long.MinValue) InFlight[k].TryRemove(job.Key, out _);

            job.Work = null;
            job.KeyedWork = null;
            job.Batch = null;
            FreeJobs.Enqueue(job);

            self.State = (int)WorkerState.Idle;
            self.Kind = -1;
            self.Key = long.MinValue;
        }
    }

    private static WorkerState StateFor(JobKind kind) => kind switch
    {
        JobKind.Cull => WorkerState.Culling,
        JobKind.MeshPrep => WorkerState.Meshing,
        JobKind.ChunkPrep => WorkerState.Loading,
        JobKind.Occlusion => WorkerState.Occluding,
        JobKind.Hud => WorkerState.Rastering,
        _ => WorkerState.Warming,
    };

    // ---- priority ------------------------------------------------------------------

    private const int PrioProcess = 0;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriorityNative(int which, uint who, int prio);

    /// <summary>True once a worker has actually been deprioritised by the OS. Reported rather
    /// than assumed: Thread.Priority = BelowNormal is accepted on Linux, reads back as
    /// BelowNormal, and leaves the thread at the process nice value.</summary>
    public static bool PriorityLowered { get; private set; }

    private static void LowerOwnPriority()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
        catch (Exception) { /* best effort - Windows honours it, Linux ignores it */ }

        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (SetPriorityNative(PrioProcess, 0, Niceness) == 0) PriorityLowered = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // ---- main-thread handoff --------------------------------------------------------

    private static readonly ConcurrentQueue<Action> ToMain = new();
    public static long StatHandoffs, StatHandoffDeferrals;
    public static int HandoffDepth => ToMain.Count;

    /// <summary>
    /// Hands a continuation back to the main thread. Anything that touches the GL context, the
    /// engine's non-thread-safe API or a renderer's state goes through here rather than being
    /// done on the worker.
    /// </summary>
    public static void PostToMain(Action action)
    {
        if (action != null) ToMain.Enqueue(action);
    }

    /// <summary>Runs handoffs until the budget is spent. Called on the frame boundary; the
    /// remainder stays queued for the next frame rather than lengthening this one.</summary>
    public static int DrainMain(double budgetMs)
    {
        if (ToMain.IsEmpty) return 0;
        var deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
        var n = 0;
        while (ToMain.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception) { Interlocked.Increment(ref StatCancelled); }
            n++;
            if (Stopwatch.GetTimestamp() >= deadline) break;
        }
        Interlocked.Add(ref StatHandoffs, n);
        if (!ToMain.IsEmpty) Interlocked.Increment(ref StatHandoffDeferrals);
        return n;
    }

    // ---- the monitor ----------------------------------------------------------------

    /// <summary>The world map's index3d multipliers, so the monitor can turn a dedup key back
    /// into chunk coordinates. Set when a world comes up; 0 means "no world, print no
    /// position" rather than printing a wrong one.</summary>
    public static int KeyMulX, KeyMulZ;


    public readonly struct WorkerSnapshot
    {
        public int Index { get; init; }
        public WorkerState State { get; init; }
        public JobKind Kind { get; init; }
        public long Key { get; init; }
        public double Ms { get; init; }
        public long Jobs { get; init; }
        public bool Nice { get; init; }
    }

    /// <summary>What every worker is doing, for the live monitor. Reads volatile fields without
    /// a lock: a row that is one job out of date is the correct trade for a monitor that costs
    /// the pool nothing.</summary>
    public static void SnapshotInto(WorkerSnapshot[] into, out int count)
    {
        var w = workers;
        count = Math.Min(into.Length, w.Length);
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            var state = (WorkerState)w[i].State;
            var busy = state is not (WorkerState.Idle or WorkerState.Parked);
            into[i] = new WorkerSnapshot
            {
                Index = i,
                State = state,
                Kind = (JobKind)Math.Max(0, w[i].Kind),
                Key = busy ? w[i].Key : long.MinValue,
                Ms = busy ? (now - w[i].StartedAt) * 1000.0 / Stopwatch.Frequency : 0,
                Jobs = w[i].Jobs,
                Nice = w[i].Nice,
            };
        }
    }

    public static void ResetStats()
    {
        Interlocked.Exchange(ref StatCompleted, 0);
        Interlocked.Exchange(ref StatCancelled, 0);
        Interlocked.Exchange(ref StatDuplicates, 0);
        Interlocked.Exchange(ref StatBatches, 0);
        Interlocked.Exchange(ref StatInline, 0);
        Interlocked.Exchange(ref StatContendedInline, 0);
        Interlocked.Exchange(ref StatWaitTicks, 0);
        Interlocked.Exchange(ref StatHandoffs, 0);
        Interlocked.Exchange(ref StatHandoffDeferrals, 0);
        for (var i = 0; i < KindTicks.Length; i++)
        {
            Interlocked.Exchange(ref KindTicks[i], 0);
            Interlocked.Exchange(ref KindJobs[i], 0);
            Interlocked.Exchange(ref KindPeakTicks[i], 0);
        }
        var w = workers;
        for (var i = 0; i < w.Length; i++) { w[i].Jobs = 0; w[i].BusyTicks = 0; }
        seenCompleted = 0;
        seenBusyTicks = 0;
        seenAtTimestamp = 0;
        JobsPerSecond = 0;
        Utilisation = 0;
    }
}
