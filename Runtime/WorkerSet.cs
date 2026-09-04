using System;
using System.Diagnostics;
using System.Threading;

namespace Komet.Runtime;

/// <summary>
/// Work handed to a <see cref="WorkerSet"/>. One call per contiguous slice of the index range,
/// so the per-call overhead is paid once per chunk instead of once per item.
/// </summary>
public interface IWorkBody
{
    void Run(int from, int to);
}

/// <summary>
/// A fixed set of dedicated threads for render-critical parallel work, replacing Parallel.For.
///
/// The reason is latency, not throughput. Parallel.For runs on the shared .NET ThreadPool, and
/// so does everything else in the process: the game queues chunk tesselation there, and this mod
/// used to queue both the per-stage cull batch (asking for CPUs-1 threads, three times a frame)
/// and the occlusion walk (CPUs-2 threads) on top. Once the pool's threads are all busy, further
/// work items wait for the thread injection heuristic, which adds threads at roughly one per
/// 500 ms. The calling thread keeps working - so it never deadlocks - but it ends up doing a
/// twelve-way split alone while holding up the frame.
///
/// That is what the hitch log's self-attribution finally showed: sweeps of 26, 30 and 46 ms in
/// frames with no GC pause at all, against an average well under one. The arithmetic was never
/// the problem; the queue in front of it was.
///
/// These threads exist for the life of the process, belong to nobody else, and park on an event
/// between batches. The handshake is allocation-free and the wake latency is an OS event - tens
/// of microseconds, and bounded, which is the entire point.
/// </summary>
public sealed class WorkerSet
{
    private readonly string name;
    private readonly int niceness;
    private readonly object startLock = new();

    private Thread[] threads;
    private ManualResetEventSlim[] gates;

    /// <summary>Spins briefly before parking: a batch that is already in flight when the last
    /// worker finishes is common enough that the spin usually wins, and a batch runs every
    /// couple of milliseconds, so parking has to stay the normal case.</summary>
    private readonly ManualResetEventSlim allDone = new(false, 400);

    private volatile IWorkBody body;
    private int count, chunk;
    private int nextIndex;
    private int itemsDone;
    private int pending;
    private Exception failure;
    private volatile bool shutdown;

    /// <summary>
    /// Stopwatch ticks the *calling* thread spent waiting for the helpers after running out of
    /// work itself. This is the number that separates "the sweep is expensive" from "the sweep
    /// is stuck behind somebody else's queue" - the distinction the ThreadPool version could
    /// not make, and that cost two wrong diagnoses.
    /// </summary>
    public long StatWaitTicks;

    public long StatRuns, StatInline;

    /// <summary>
    /// Batches run inline because a helper from the PREVIOUS batch had not woken up and
    /// checked back in yet. On a loaded machine (occlusion walk, worldgen, GC threads all
    /// wanting the six cores) that helper can be descheduled for many milliseconds; running
    /// the batch on the calling thread alone costs a fraction of that and self-heals the
    /// moment the sleeper gets a core. Reported so contention is a number, not a hunch.
    /// </summary>
    public long StatContendedInline;

    /// <param name="name">Thread name prefix, so a profiler and the HUD can tell the sets apart.</param>
    /// <param name="niceness">
    /// Unix nice increment for these workers, 0 to leave them at the process default. Only ever
    /// raised - lowering nice needs privileges, raising it never does.
    ///
    /// For work that is not on the frame's critical path. The occlusion walk holds five threads
    /// for 11 ms about five times a second; the cull batch needs a burst of every core for well
    /// under a millisecond, on a frame's deadline. On six physical cores those two collide, and
    /// the hitch log caught it: sweeps of 8-16 ms whose 2,0 to 10,9 ms were spent purely waiting
    /// for a worker to be scheduled, in frames with no GC pause at all. Niceness is the right
    /// instrument because it costs nothing when the machine is idle - occlusion still gets every
    /// core - and yields instantly when the render thread wants one.
    /// </param>
    public WorkerSet(string name, int niceness = 0)
    {
        this.name = name;
        this.niceness = niceness;
    }

    // ---- priority ------------------------------------------------------------------

    /// <summary>PRIO_PROCESS. On Linux this operates on the *thread* whose id is given, and 0
    /// means the calling thread - which is why every worker sets its own.</summary>
    private const int PrioProcess = 0;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriorityNative(int which, uint who, int prio);

    /// <summary>
    /// True once a worker has actually been deprioritised by the OS.
    ///
    /// This is reported rather than assumed because the obvious way to do this does not work:
    /// Thread.Priority = BelowNormal is accepted on Linux, reads back as BelowNormal, and leaves
    /// the thread at the process nice value - measured, the worker sat at nice -4 exactly like
    /// the main thread. Shipping that would have been indistinguishable from shipping nothing.
    /// </summary>
    public bool PriorityLowered { get; private set; }

    private void LowerOwnPriority()
    {
        if (niceness <= 0) return;

        // Windows honours this; Linux accepts and ignores it for ordinary threads. Set anyway -
        // it is the portable half, and it is free.
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
        catch (Exception) { /* best effort */ }

        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (SetPriorityNative(PrioProcess, 0, niceness) == 0) PriorityLowered = true;
        }
        catch (DllNotFoundException) { /* no libc under this name - leave it at default */ }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Helper threads, not counting the caller. 0 means everything runs inline.</summary>
    public int ThreadCount => threads?.Length ?? 0;

    /// <summary>
    /// Creates the threads. Idempotent, and safe to call from anywhere; a count of 0 or less
    /// leaves the set empty, which makes every Run inline - the "parallel off" path.
    /// </summary>
    public void Start(int workers)
    {
        lock (startLock)
        {
            if (threads != null || shutdown) return;
            if (workers <= 0) return;

            var t = new Thread[workers];
            var g = new ManualResetEventSlim[workers];
            for (var i = 0; i < workers; i++) g[i] = new ManualResetEventSlim(false, 200);
            gates = g;

            for (var i = 0; i < workers; i++)
            {
                var index = i;
                t[i] = new Thread(() => Loop(index))
                {
                    IsBackground = true,
                    Name = name + "-" + i,
                };
                t[i].Start();
            }
            threads = t;
        }
    }

    public void Stop()
    {
        lock (startLock)
        {
            shutdown = true;
            var g = gates;
            if (g != null) for (var i = 0; i < g.Length; i++) g[i].Set();
            threads = null;
        }
    }

    /// <summary>
    /// Runs <paramref name="itemCount"/> items across the workers and the calling thread, and
    /// returns once every one of them is done. Slices are handed out dynamically, so a chunk
    /// that turns out to be expensive does not leave the other threads idle - which a strided
    /// partition does, and pools differ in size by two orders of magnitude.
    ///
    /// Completion is counted in WORK, not in workers: the caller returns as soon as every item
    /// has run, whether or not every helper has woken up yet. The first version waited for all
    /// helpers to check in, and the hitch log caught what that costs on a loaded machine:
    /// 9,7-11 ms sweep waits with no GC pause, which were nothing but the render thread
    /// waiting for the LAST helper to be scheduled so it could discover there was nothing
    /// left to do. A helper that never claimed a slice holds no state anybody has to wait for.
    ///
    /// The check-in count still exists, but it gates the NEXT batch's setup instead of this
    /// batch's return: batch fields may only be rewritten once no helper can still be inside
    /// the previous drain. If a straggler has not checked in by then, the batch runs inline on
    /// the calling thread - a bounded, self-healing cost instead of an unbounded wait.
    /// </summary>
    public void Run(IWorkBody work, int itemCount, int chunkSize)
    {
        if (itemCount <= 0) return;
        if (chunkSize < 1) chunkSize = 1;

        var t = threads;
        if (t == null || t.Length == 0 || itemCount <= chunkSize)
        {
            StatInline++;
            RunInline(work, itemCount);
            return;
        }

        // A helper from the previous batch has not woken up and checked back in. It cannot be
        // holding work (the previous Run only returned once all items were done), but it WILL
        // read the batch fields when it finally wakes - so they must not be rewritten under
        // it. Waiting here would just move the stall; the batch runs inline instead.
        if (Volatile.Read(ref pending) != 0)
        {
            StatContendedInline++;
            RunInline(work, itemCount);
            return;
        }

        StatRuns++;

        count = itemCount;
        chunk = chunkSize;
        Volatile.Write(ref itemsDone, 0);
        Volatile.Write(ref nextIndex, 0);
        failure = null;
        pending = t.Length;
        allDone.Reset();
        // published last: the gate Set below is the release, so a worker that observes the
        // wake observes all of the above
        body = work;

        var g = gates;
        for (var i = 0; i < t.Length; i++) g[i].Set();

        // The caller is a worker too - it would otherwise sit idle through its own batch, and
        // on a small batch it can finish the whole thing before the first helper even wakes.
        try
        {
            Drain(work);
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref failure, e, null);
            AbandonRemaining();
        }

        // Waits for the WORK, not the workers: every claimed slice is finished and counted, or
        // this never fires. Leaving while a slice is unfinished would let the caller reuse the
        // batch buffers under a helper that is still reading them - so the wait is not
        // optional, but it also never outlasts the work itself.
        var t0 = Stopwatch.GetTimestamp();
        allDone.Wait();
        StatWaitTicks += Stopwatch.GetTimestamp() - t0;

        var failed = failure;
        if (failed != null) throw new InvalidOperationException("parallel work item failed", failed);
    }

    /// <summary>Every path out of Run surfaces a failed work item the same way - the caller
    /// must not need to know whether the batch happened to go parallel or inline.</summary>
    private static void RunInline(IWorkBody work, int itemCount)
    {
        try
        {
            work.Run(0, itemCount);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("parallel work item failed", e);
        }
    }

    private void Drain(IWorkBody work)
    {
        int n = count, c = chunk;
        while (true)
        {
            var from = Interlocked.Add(ref nextIndex, c) - c;
            if (from >= n) return;
            var to = from + c;
            if (to > n) to = n;
            // The item count advances even when the slice throws (the exception still
            // propagates): a claimed slice that never counted would leave the caller waiting
            // on work that no longer exists.
            try
            {
                work.Run(from, to);
            }
            finally
            {
                if (Interlocked.Add(ref itemsDone, to - from) == n) allDone.Set();
            }
        }
    }

    /// <summary>
    /// Claims and counts every remaining slice WITHOUT running it. Only ever called after a
    /// slice failed: the batch's result is void anyway (the caller rethrows), and without
    /// this, a batch in which every participant failed would strand unclaimed items and hang
    /// the caller forever.
    /// </summary>
    private void AbandonRemaining()
    {
        int n = count, c = chunk;
        while (true)
        {
            var from = Interlocked.Add(ref nextIndex, c) - c;
            if (from >= n) return;
            var to = from + c;
            if (to > n) to = n;
            if (Interlocked.Add(ref itemsDone, to - from) == n) allDone.Set();
        }
    }

    private void Loop(int index)
    {
        LowerOwnPriority();
        var gate = gates[index];
        while (true)
        {
            gate.Wait();
            gate.Reset();
            if (shutdown) return;

            // A failed work item is reported through 'failure' and rethrown on the caller,
            // which is where Parallel.For would have surfaced it too. The check-in decrement
            // runs on every path: it is what tells the next Run that this helper can no
            // longer be inside the batch fields.
            try
            {
                Drain(body);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
                AbandonRemaining();
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }
    }

    // ---- default sizing ------------------------------------------------------------

    /// <summary>
    /// Physical cores rather than hardware threads, minus a little headroom.
    ///
    /// The old CPUs-1 was wrong twice over. Both of these sweeps are memory-bound linear scans,
    /// so a second thread on the same core's SMT sibling adds queueing, not throughput - and on
    /// a 6 core / 12 thread part, eleven cull threads plus ten occlusion threads plus the render
    /// thread plus the game's tesselation threads is an oversubscribed machine, which is also
    /// the worst possible state for a GC to start in: every one of its heaps needs a core.
    /// </summary>
    public static int AutoThreads(int share) => AutoThreads(share, CpuTopology.PhysicalCores);

    /// <summary>
    /// Helpers for a caller that keeps <paramref name="share"/> physical cores for itself and
    /// the threads it competes with. Zero helpers is a legitimate answer - on a two-core part
    /// the occlusion walk runs inline on its own thread rather than adding a third busy
    /// thread - and <see cref="Start"/> treats it as "everything inline". The old floor of one
    /// helper per set put two extra threads on the tester's dual core for no gain: a helper
    /// that is scheduled behind the render thread only lengthens the wait it was meant to
    /// shorten. Capped at 8, where the memory-bound sweep stops scaling.
    /// </summary>
    internal static int AutoThreads(int share, int physicalCores)
        => Math.Clamp(physicalCores - share, 0, 8);
}
