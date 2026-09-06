using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Komet.Guard;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Attributes the main-thread task drain - the biggest unnamed tenant of the "draussen" bucket.
///
/// Every server packet that is not chunk data becomes a ClientTask ("readpacket33",
/// "readpacket38", ...) and runs in ClientMain.ExecuteMainThreadTasks, which the game calls
/// right after MainRenderLoop and which drains EVERYTHING that queued up since the last frame
/// in one go: entity loads (one task per entity the server started tracking, hundreds at a
/// world join and in bursts of dozens every 200 ms while streaming), block updates, attribute
/// syncs, inventory packets. None of it is a render stage, so a burst there was booked as
/// "draussen" and read as driver back-pressure - indistinguishable from a swap stall.
///
/// This replaces the drain with a 1:1 transcription that times each task. The frame keeps the
/// drain's total and its single heaviest task code (hitch line: "tasks 9,1 (readpacket33
/// 8,2)"), and a per-code smoothed table feeds the report - so the next "draussen 20 ms" says
/// which packet type it was. Behaviour is identical: same queue handover under the same lock,
/// same suspend/requeue rule, same profiler marks, exceptions propagate exactly as before.
/// </summary>
public static class MainThreadTaskPatches
{
    public static bool Enabled = true;

    /// <summary>
    /// Milliseconds of drain per frame before the remainder is handed back to the queue with
    /// vanilla's own requeue (the suspend path: what is left goes to the FRONT, ahead of
    /// anything that arrived meanwhile, so order is kept). The 02.09. report had "draussen
    /// 17,7 | tasks 16,9 (loadchunk 16,8)": the network thread hands the drain a whole batch
    /// of chunk packets at once, and it ran them all in one frame. 0 = drain everything.
    /// </summary>
    public static double BudgetMs = 3.0;

    /// <summary>Liveness: this many tasks always run, whatever the clock says.</summary>
    public const int MinPerFrame = 8;

    /// <summary>
    /// The budget only applies to a world that is up. Everything a join queues - the
    /// LevelFinalize packet above all (task code "readpacket6") - is lifecycle, not load:
    /// renderers are registered before it and only initialised by it, so spreading that
    /// queue over frames lets a registered renderer draw against half-built vanilla state
    /// (1.2.0-pre.2: WeatherSystemClient.OnRenderFrame NRE'd on a WeatherDataAtPlayer that
    /// LevelFinalizeInit had not made yet). Until LevelFinalize has run, the drain is
    /// vanilla's: everything queued runs in the frame it arrived. Set by the mod system's
    /// LevelFinalize handler, cleared on world leave (<see cref="Detach"/>).
    /// </summary>
    public static bool WorldReady;

    /// <summary>The budget stretches with the backlog (x2 at this many waiting tasks), and
    /// past <see cref="MaxBacklog"/> it is ignored - a queue that grows faster than the
    /// budget drains must not grow without bound.</summary>
    public const int BacklogScale = 256;
    public const int MaxBacklog = 4096;

    /// <summary>Frames cut short by the budget, and tasks pushed to a later frame.</summary>
    public static long StatBudgetCuts, StatDeferredTasks;

    private static readonly Measure.MsLedger Ledger = new();

    /// <summary>Codes are a bounded vocabulary (packet ids plus a handful of named tasks); the
    /// cap only guards against a mod generating unique codes per call.</summary>
    private const int MaxEntries = 256;
    private const string Overflow = "(andere)";

    /// <summary>Tasks run since start, and the single longest one seen (code + ms).</summary>
    public static long StatTasks;
    public static double StatWorstMs;
    public static string StatWorstCode;

    private static readonly AccessTools.FieldRef<ClientMain, Queue<ClientTask>> ReversedRef =
        AccessTools.FieldRefAccess<ClientMain, Queue<ClientTask>>("reversedQueue");
    private static readonly AccessTools.FieldRef<ClientMain, Queue<ClientTask>> HoldingRef =
        AccessTools.FieldRefAccess<ClientMain, Queue<ClientTask>>("holdingQueue");
    private static readonly AccessTools.FieldRef<ClientMain, bool> SuspendRef =
        AccessTools.FieldRefAccess<ClientMain, bool>("SuspendMainThreadTasks");

    /// <summary>
    /// The lock the network thread's enqueue and this drain share - resolved by NAME, because
    /// its type is not the same on every client. Vanilla declares it <c>object</c> and takes
    /// it with Monitor; the Optimum fork (v0.3.14) declares it <c>Lock</c> and
    /// enters it with EnterScope. A field reference compiled into IL carries the field's type,
    /// so the vanilla binding threw MissingFieldException on the first frame of the fork
    /// (1.2.0-pre.3 crashed in the connecting screen). Read once per game instance, never per
    /// frame: the object is created with the ClientMain and never replaced.
    /// </summary>
    internal static readonly FieldInfo LockField = AccessTools.Field(typeof(ClientMain), "MainThreadTasksLock");

    /// <summary>
    /// Takes the queue lock the way its type demands. This is not a convenience: Monitor on a
    /// <see cref="Lock"/> instance is a DIFFERENT lock than the one the fork's
    /// EnqueueMainThreadTask holds, so a plain <c>lock (obj)</c> would leave the handover
    /// racing the network thread without any exception to say so.
    /// </summary>
    internal readonly struct QueueLock
    {
        private readonly Lock typed;
        private readonly object monitor;

        /// <summary>From the field's value: a <see cref="Lock"/> is entered as one, anything
        /// else is a Monitor lock (vanilla's plain object).</summary>
        public QueueLock(object instance)
        {
            typed = instance as Lock;
            monitor = typed == null ? instance : null;
        }

        public bool IsTyped => typed != null;

        public void Enter()
        {
            if (typed != null) typed.Enter();
            else Monitor.Enter(monitor);
        }

        public void Exit()
        {
            if (typed != null) typed.Exit();
            else Monitor.Exit(monitor);
        }
    }

    // The two delegates RunTasks takes are cached per game instance rather than allocated
    // per frame - a closure per frame is small, but this runs every frame for the life of
    // the session and the point of the patch is to remove garbage, not add it. The lock
    // rides along: one reflection read per session instead of one per frame.
    private static ClientMain cachedGame;
    private static QueueLock cachedLock;
    private static Func<bool> cachedSuspended;
    private static Action cachedRequeue;

    public static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks), [typeof(float)])
                     ?? throw new InvalidOperationException("ClientMain.ExecuteMainThreadTasks not found");
        // A lock of a third kind is a client this transcription has not seen: the drain stays
        // vanilla's (the caller logs "could not enable") rather than guessing how to take it.
        if (LockField == null)
            throw new InvalidOperationException("ClientMain.MainThreadTasksLock not found");
        if (LockField.FieldType != typeof(object) && LockField.FieldType != typeof(Lock))
            throw new InvalidOperationException("ClientMain.MainThreadTasksLock is a " + LockField.FieldType.FullName
                + " - this drain knows object (vanilla) and Lock (Optimum), nothing else");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(MainThreadTaskPatches), nameof(Prefix)));
    }

    public static bool Prefix(ClientMain __instance)
    {
        if (!Enabled) return true;
        Execute(__instance);
        return false;
    }

    /// <summary>ClientMain.ExecuteMainThreadTasks, transcribed, with a clock around each task.</summary>
    private static void Execute(ClientMain game)
    {
        var profiler = ScreenManager.FrameProfiler;
        profiler.Mark("beginMTT");
        if (game.GameLaunchTasks.Count > 0)
        {
            // vanilla runs exactly one launch task per frame and nothing else in that frame
            game.GameLaunchTasks.Dequeue().Action();
            return;
        }
        if (SuspendRef(game)) return;

        if (!ReferenceEquals(cachedGame, game))
        {
            cachedGame = game;
            cachedLock = new QueueLock(LockField.GetValue(game));
            var l = cachedLock;
            cachedSuspended = () => SuspendRef(game);
            cachedRequeue = () => Requeue(game, l);
        }

        var reversed = ReversedRef(game);
        Handover(game, reversed, cachedLock);
        RunTasks(reversed, cachedSuspended, cachedRequeue, game.extendedDebugInfo ? profiler : null);
        profiler.Mark("doneMTT");
    }

    /// <summary>The handover under the shared lock: everything the network thread queued since
    /// the last frame moves to the drain's private queue, in order.</summary>
    private static void Handover(ClientMain game, Queue<ClientTask> reversed, QueueLock l)
    {
        l.Enter();
        try
        {
            var tasks = game.MainThreadTasks;
            while (tasks.Count > 0) reversed.Enqueue(tasks.Dequeue());
        }
        finally { l.Exit(); }
    }

    /// <summary>
    /// The drain loop, separated from the engine fields so verify can drive it with fake
    /// tasks: runs the queue in order, times each task, and hands the remainder back when
    /// the game suspends task execution mid-drain (exactly vanilla's rule).
    /// </summary>
    internal static void RunTasks(Queue<ClientTask> reversed, Func<bool> suspended, Action requeue, FrameProfilerUtil debugProfiler)
    {
        var started = Stopwatch.GetTimestamp();
        var budgetMs = WorldReady ? BudgetMs : 0;
        var ran = 0;
        while (reversed.Count > 0)
        {
            var task = reversed.Dequeue();
            var t0 = Stopwatch.GetTimestamp();
            task.Action();
            var t1 = Stopwatch.GetTimestamp();
            Note(task.Code, t1 - t0);
            ran++;
            debugProfiler?.Mark(task.Code);
            if (reversed.Count == 0) break;
            // vanilla's rule: a suspend mid-drain hands the remainder back (and empties this queue)
            if (suspended()) { requeue(); break; }
            // the budget uses the same hand-back, so order and the requeue semantics are vanilla's
            if (OverBudget(budgetMs, (t1 - started) * Measure.MsLedger.TicksToMs, ran, reversed.Count))
            {
                StatBudgetCuts++;
                StatDeferredTasks += reversed.Count;
                requeue();
                break;
            }
        }
    }

    /// <summary>The cut rule, pure: enough tasks ran for liveness, the clock is past the
    /// budget stretched by the backlog, and the backlog is not so large that cutting would
    /// let it run away.</summary>
    internal static bool OverBudget(double budgetMs, double spentMs, int ran, int remaining)
        => budgetMs > 0 && ran >= MinPerFrame && remaining <= MaxBacklog
           && spentMs > budgetMs * (1.0 + remaining / (double)BacklogScale);

    /// <summary>ClientMain.requeueTasks, transcribed: what is left goes back to the front of
    /// the shared queue, ahead of anything that arrived meanwhile.</summary>
    private static void Requeue(ClientMain game, QueueLock l)
    {
        var reversed = ReversedRef(game);
        var holding = HoldingRef(game);
        l.Enter();
        try
        {
            var tasks = game.MainThreadTasks;
            while (tasks.Count > 0) holding.Enqueue(tasks.Dequeue());
            while (reversed.Count > 0) tasks.Enqueue(reversed.Dequeue());
            while (holding.Count > 0) tasks.Enqueue(holding.Dequeue());
        }
        finally { l.Exit(); }
    }

    /// <summary>Books one finished task into the frame and into its code's smoothed entry.</summary>
    internal static void Note(string code, long ticks)
    {
        StatTasks++;
        var ms = ticks * Measure.MsLedger.TicksToMs;
        // "readpacket58" becomes "readpacket58=ExchangeBlock" - the id table is the engine's own
        code = TaskCodes.Describe(code ?? "?");
        Measure.FrameStats.AddMainThreadTask(code, ms);
        if (ms > StatWorstMs)
        {
            StatWorstMs = ms;
            StatWorstCode = code;
        }

        var e = Ledger.Bucket(code, MaxEntries, Overflow);
        e.Ticks += ticks;
        e.Calls++;
    }

    public static void EndFrame() => Ledger.EndFrame();

    /// <summary>The heaviest task codes by smoothed ms per frame, with their call totals.</summary>
    public static List<(string code, double ms, long calls)> Top(int count) => Ledger.Top(count);

    public static int Count => Ledger.Count;

    /// <summary>One report line: the drain's per-frame cost and its top codes.</summary>
    public static void Write(StringBuilder sb, int count, CultureInfo ci)
    {
        var top = Top(count);
        sb.AppendFormat(ci, "main thread tasks: {0:F2} ms/frame, {1:N0} since start",
            Measure.FrameStats.AvgMainTaskMs, StatTasks);
        if (StatWorstMs >= 1.0)
            sb.AppendFormat(ci, ", longest {0:F1} ms ({1})", StatWorstMs, StatWorstCode);
        if (BudgetMs > 0)
            sb.AppendFormat(ci, ", budget {0:0.#} ms: {1:N0} frames capped, {2:N0} tasks deferred", BudgetMs, StatBudgetCuts, StatDeferredTasks);
        else
            sb.Append(", no budget");
        if (top.Count > 0)
        {
            sb.Append(" | most expensive: ");
            for (var i = 0; i < top.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(ci, "{0} {1:F2} ms ({2:N0}x)", top[i].code, top[i].ms, top[i].calls);
            }
        }
        sb.Append('\n');
    }

    public static void Reset()
    {
        Ledger.Reset();
        StatTasks = 0;
        StatWorstMs = 0;
        StatWorstCode = null;
        StatBudgetCuts = 0;
        StatDeferredTasks = 0;
    }

    /// <summary>World leave: the cached game instance must not outlive its session, and the
    /// next join drains unbudgeted again until its own LevelFinalize.</summary>
    public static void Detach()
    {
        WorldReady = false;
        cachedGame = null;
        cachedLock = default;
        cachedSuspended = null;
        cachedRequeue = null;
    }
}
