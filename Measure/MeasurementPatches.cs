using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Measure;

/// <summary>
/// Pure measurement: one bucket per render stage, the game tick, and how long the per-frame
/// chunk mesh upload takes. Changes no behaviour, which is what makes it safe to run in the
/// vanilla baseline mod as well as the optimising one.
///
/// ClientMain.MainRenderLoop runs the whole game tick and then every render stage on the main
/// thread, so these buckets add up to the frame. Two Stopwatch reads per stage, about thirteen
/// stages a frame - well under a microsecond in total.
/// </summary>
public static class MeasurementPatches
{
    /// <summary>Set by the optimising mod to drive its upload throttle. Unused in the baseline.</summary>
    public static Action UploadBegin;
    public static Action UploadEnd;

    /// <summary>The method the upload timing hangs off, so a caller can add a transpiler to it.</summary>
    public static MethodInfo UploadMethod { get; private set; }

    /// <summary>
    /// Where a skipped optional bracket is reported. Null = silent (the verify harness).
    /// </summary>
    public static Action<string> Warn;

    /// <summary>Optional brackets that did not apply in the last <see cref="Apply"/>, by name.
    /// Empty on a stock engine; the report prints it so a field log says what is missing.</summary>
    public static readonly System.Collections.Generic.List<string> SkippedBrackets = new();

    /// <summary>
    /// Applies the frame accounting. The four core brackets (render stages, game tick, chunk
    /// upload, per-chunk tesselation) are mandatory and throw - without them there is no
    /// frame accounting at all. Everything after them is attribution detail, and each of
    /// those brackets is applied on its own: a tester's log (02.09., "Optimum" - a modified
    /// engine build with a second populateTesselatedChunkPart overload) showed the whole
    /// group failing on one AmbiguousMatchException, which left the brackets before it
    /// applied and the ones after it (JSON alloc, network alloc, swap timing) silently
    /// missing. Half a measurement that reports itself as "could not enable" is the worst of
    /// both worlds; now the stock engine gets every bracket and a modified one loses exactly
    /// the bracket it changed, by name.
    /// </summary>
    public static void Apply(Harmony harmony)
    {
        SkippedBrackets.Clear();
        var stage = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage),
                        [typeof(EnumRenderStage), typeof(float)])
                    ?? throw new InvalidOperationException("ClientMain.TriggerRenderStage not found");

        harmony.Patch(stage,
            prefix: OuterPrefix(nameof(StagePrefix)),
            postfix: OuterPostfix(nameof(StagePostfix)));
        // From here FrameBoundary fires once per frame. Features that budget themselves per
        // frame ask for this before they arm: a budget whose reset never comes does not
        // degrade to "slower", it degrades to "never" (see EntityTessPatches).
        FrameBoundaryLive = true;

        // EventManager.TriggerGameTick is the client's whole game tick; ClientEventManager does
        // not override it, so the base method is what runs.
        var tick = AccessTools.Method(typeof(Vintagestory.Common.EventManager), "TriggerGameTick",
                       [typeof(long), typeof(IWorldAccessor)])
                   ?? throw new InvalidOperationException("EventManager.TriggerGameTick not found");

        harmony.Patch(tick,
            prefix: OuterPrefix(nameof(TickPrefix)),
            postfix: OuterPostfix(nameof(TickPostfix)));

        var tesselator = AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkTesselatorManager")
                         ?? throw new InvalidOperationException("ChunkTesselatorManager not found");
        UploadMethod = AccessTools.Method(tesselator, "OnBeforeFrame", [typeof(float)])
                       ?? throw new InvalidOperationException("OnBeforeFrame(float) not found");

        harmony.Patch(UploadMethod,
            prefix: OuterPrefix(nameof(UploadPrefix)),
            postfix: OuterPostfix(nameof(UploadPostfix)));

        // Tesselation throughput: how long one chunk takes to mesh, and how much of that is
        // spent unpacking and assembling the 27 neighbouring chunks. Runs on the tesselation
        // thread; TesselationStats does the cross-thread bookkeeping.
        var tesselate = AccessTools.Method(tesselator, "TesselateChunk",
                            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()])
                        ?? throw new InvalidOperationException("TesselateChunk not found");
        harmony.Patch(tesselate,
            prefix: OuterPrefix(nameof(TesselatePrefix)),
            postfix: OuterPostfix(nameof(TesselatePostfix)));

        var buildExt = AccessTools.Method(typeof(ChunkTesselator), "BuildExtendedChunkData")
                       ?? throw new InvalidOperationException("ChunkTesselator.BuildExtendedChunkData not found");
        harmony.Patch(buildExt,
            prefix: OuterPrefix(nameof(NeighbourPrefix)),
            postfix: OuterPostfix(nameof(NeighbourPostfix)));

        // Sun relighting that TesselateChunk runs inline before meshing - kept in its own
        // bucket so its share of the per-chunk cost is readable.
        var relight = AccessTools.Method(typeof(TerrainIlluminator), "SunRelightChunk",
                          [typeof(ClientChunk), typeof(Vintagestory.Common.Database.ChunkPos)])
                      ?? throw new InvalidOperationException("SunRelightChunk not found");
        harmony.Patch(relight,
            prefix: OuterPrefix(nameof(RelightPrefix)),
            postfix: OuterPostfix(nameof(RelightPostfix)));

        // ---- optional brackets: attribution detail, each on its own ----------------

        // Allocation attribution INSIDE the meshing pass. A field run measured 219 MB/s on
        // the tesselation thread with the mesh recycler at 100% hits and the neighbour/relight
        // phases at zero - so the churn lives in the meshing itself, and these two brackets
        // split it: the per-part clones (populateTesselatedChunkPart -> CloneUsingRecycler,
        // whose small-mesh fallback and extra arrays allocate fresh) versus the per-block
        // JSON shape tesselation. Alloc-only brackets: two thread-local reads per call.
        //
        // Every overload of that name is bracketed, not "the" method: a modified engine
        // build added a second overload, and looking the method up by name alone threw
        // AmbiguousMatchException. Which overload the engine calls (and whether one wraps
        // the other) is its business - the prefix/postfix pair is nesting-aware, so a call
        // reached through another overload books once, on the outermost frame.
        Optional("tesselation part clones (populateTesselatedChunkPart)", () =>
            PatchEveryOverload(harmony, typeof(ChunkTesselator), "populateTesselatedChunkPart",
                OuterPrefix(nameof(PartsAllocPrefix)),
                OuterPostfix(nameof(PartsAllocPostfix))));

        // the 5-arg overload delegates here, so this one bracket sees every call
        Optional("json shape tesselation (AddJsonModelDataToMesh)", () =>
        {
            var json = AccessTools.Method(typeof(JsonTesselator), "AddJsonModelDataToMesh",
                       [typeof(MeshData), typeof(int), typeof(TCTCache), typeof(IMeshPoolSupplier),
                           typeof(float[]), typeof(IJsonTesselatorHooks), typeof(int)])
                       ?? throw new InvalidOperationException("AddJsonModelDataToMesh(7 args) not found");
            harmony.Patch(json,
                prefix: OuterPrefix(nameof(AllocPrefix)),
                postfix: OuterPostfix(nameof(JsonAllocPostfix)));
        });

        // The network thread's share of the allocation rate. SystemNetworkProcess handles
        // every server packet - chunk intake included - inside its own thread tick, and its
        // allocations were the largest unmeasured block in a field report (150 of 161
        // hitches with a gc pause, ~220 MB/s that no existing row could name). Pure
        // measurement: two thread-local reads per 1 ms tick, behaviour untouched.
        Optional("network thread allocation (SystemNetworkProcess)", () =>
        {
            var netType = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemNetworkProcess")
                          ?? throw new InvalidOperationException("SystemNetworkProcess not found");
            var netTick = AccessTools.Method(netType, "OnSeperateThreadGameTick")
                          ?? throw new InvalidOperationException(
                              "SystemNetworkProcess.OnSeperateThreadGameTick not found");
            harmony.Patch(netTick,
                prefix: OuterPrefix(nameof(NetAllocPrefix)),
                postfix: OuterPostfix(nameof(NetAllocPostfix)));
        });

        // How long SwapBuffers itself takes, so "ausserhalb" splits into the swap and the
        // rest of the event loop. Under mesa_glthread every stage timing above only measures
        // command *recording*; the driver thread's real work is paid wherever the queue must
        // drain, and the swap is the frame's one guaranteed drain point. A transpiler on the
        // caller rather than a patch on SwapBuffers, because GameWindow.SwapBuffers is a
        // one-line non-virtual method the JIT inlines into window_RenderFrame - a prefix on
        // it would apply cleanly and never run (the dead-profiler lesson).
        Optional("swap timing (window_RenderFrame)", () =>
        {
            var renderFrame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame")
                              ?? throw new InvalidOperationException("window_RenderFrame not found");
            harmony.Patch(renderFrame, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(MeasurementPatches), nameof(WrapSwapBuffers))));
        });

        // A new mesh pool is a GL buffer allocation of tens of megabytes inside the upload
        // drain - the 03.09. hitch list had "before 13,5 | upload 12,7" frames against a
        // 6 ms budget, which a vertex-count budget cannot see coming. Counted and timed, so
        // the hitch line can say "upload 12,7 (davon 1 neue pools 9,8)" and the report how
        // many pools a session created and what the longest cost.
        Optional("mesh pool creation (MeshDataPool.AllocateNewPool)", () =>
        {
            var alloc = AccessTools.Method(typeof(MeshDataPool), "AllocateNewPool")
                        ?? throw new InvalidOperationException("MeshDataPool.AllocateNewPool not found");
            harmony.Patch(alloc,
                prefix: OuterPrefix(nameof(PoolAllocPrefix)),
                postfix: OuterPostfix(nameof(PoolAllocPostfix)));
        });
    }

    /// <summary>
    /// Runs one optional bracket. A failure is logged with the bracket's name and recorded in
    /// <see cref="SkippedBrackets"/>; the remaining brackets still apply. The throw-away
    /// exception text is kept short on purpose - the mandatory brackets already proved the
    /// engine is patchable, so what matters here is WHICH detail is missing.
    /// </summary>
    /// <summary>
    /// The brackets sit OUTSIDE every other mod's prefix and postfix on the same method:
    /// prefixes at Priority.First run before the rest, postfixes at Priority.Last run after
    /// them (Harmony orders both kinds by descending priority). What another mod adds inside
    /// a measured method - SheyderMod relights wide shapes in a postfix on
    /// AddJsonModelDataToMesh - is then part of that method's figure instead of leaking into
    /// "rest". The mod's own patches on measured methods already sit at Low for the same
    /// reason (the window prebuilder, the priority upload drain).
    /// </summary>
    public const int OuterPrefixPriority = Priority.First;
    public const int OuterPostfixPriority = Priority.Last;

    private static HarmonyMethod OuterPrefix(string name)
        => new(AccessTools.Method(typeof(MeasurementPatches), name)) { priority = OuterPrefixPriority };

    private static HarmonyMethod OuterPostfix(string name)
        => new(AccessTools.Method(typeof(MeasurementPatches), name)) { priority = OuterPostfixPriority };

    private static void Optional(string name, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception e)
        {
            SkippedBrackets.Add(name);
            Warn?.Invoke($"measurement bracket '{name}' not applied ({e.GetType().Name}: {FirstLine(e.Message)}) - "
                         + "the frame accounting works, this one attribution row stays empty");
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var nl = s.IndexOf('\n');
        return nl < 0 ? s : s.Substring(0, nl);
    }

    /// <summary>
    /// Every method of that name declared on the type, in declaration order. Harmony's
    /// AccessTools.Method(type, name) resolves through Type.GetMethod, which throws
    /// AmbiguousMatchException the moment a second overload exists - a build of the engine
    /// with one extra overload is enough. Enumerating never throws, and a caller that wants
    /// all of them just gets all of them.
    /// </summary>
    internal static System.Collections.Generic.List<MethodInfo> MethodsNamed(Type type, string name)
    {
        var found = new System.Collections.Generic.List<MethodInfo>();
        foreach (var m in AccessTools.GetDeclaredMethods(type))
            if (m.Name == name) found.Add(m);
        return found;
    }

    /// <summary>
    /// Patches every overload of <paramref name="name"/> with the same prefix/postfix pair and
    /// returns how many. Throws when there is none - a bracket that measures nothing must not
    /// report itself as applied.
    /// </summary>
    internal static int PatchEveryOverload(Harmony harmony, Type type, string name,
                                           HarmonyMethod prefix, HarmonyMethod postfix)
    {
        var overloads = MethodsNamed(type, name);
        if (overloads.Count == 0)
            throw new InvalidOperationException($"{type.Name}.{name} not found");
        foreach (var m in overloads) harmony.Patch(m, prefix: prefix, postfix: postfix);
        return overloads.Count;
    }

    public static void NetAllocPrefix(out long __state)
        => __state = GC.GetAllocatedBytesForCurrentThread();

    public static void NetAllocPostfix(long __state)
        => FrameStats.AddNetAllocBytes(GC.GetAllocatedBytesForCurrentThread() - __state);

    private static long swapStartedAt;

    public static void SwapPrefix() => swapStartedAt = Stopwatch.GetTimestamp();

    public static void SwapPostfix()
        => FrameStats.AddSwapTicks(Stopwatch.GetTimestamp() - swapStartedAt);

    /// <summary>
    /// Brackets the SwapBuffers call inside window_RenderFrame with the two timestamp calls.
    /// Both helpers take no arguments and return nothing, so the evaluation stack around the
    /// call site is untouched. Fails loudly when the call is not found - a measurement that
    /// silently measures nothing is worse than none.
    /// </summary>
    public static System.Collections.Generic.IEnumerable<CodeInstruction> WrapSwapBuffers(
        System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
    {
        var prefix = AccessTools.Method(typeof(MeasurementPatches), nameof(SwapPrefix));
        var postfix = AccessTools.Method(typeof(MeasurementPatches), nameof(SwapPostfix));

        var wrapped = 0;
        foreach (var ins in instructions)
        {
            var isSwap = ins.operand is MethodInfo m && m.Name == "SwapBuffers" && m.GetParameters().Length == 0;
            if (isSwap)
            {
                // the receiver is already on the stack; a static void() call leaves it alone.
                // Any label pointing at the call moves to the inserted prefix, so a jump to
                // "swap" cannot bypass the start timestamp.
                var pre = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, prefix);
                pre.MoveLabelsFrom(ins);
                pre.MoveBlocksFrom(ins);
                yield return pre;
                yield return ins;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, postfix);
                wrapped++;
            }
            else
            {
                yield return ins;
            }
        }

        if (wrapped != 1)
            throw new InvalidOperationException($"expected exactly one SwapBuffers call in window_RenderFrame, found {wrapped}");
    }

    /// <summary>Raised on the frame boundary so a consumer can fold up its per-frame totals.</summary>
    public static Action FrameBoundary;

    /// <summary>
    /// Whether the render-stage bracket is on the method, i.e. whether <see cref="FrameBoundary"/>
    /// actually fires. False means <see cref="Apply"/> never got that far - and every per-frame
    /// budget in this mod has to stay on vanilla, because its window would never reopen.
    /// </summary>
    public static bool FrameBoundaryLive { get; private set; }

    public static void StagePrefix(EnumRenderStage stage, out long __state)
    {
        if (stage == EnumRenderStage.Before) { FrameStats.BeginFrame(); FrameBoundary?.Invoke(); }
        // the GPU-side stage clock starts where the CPU-side one does
        GpuFrameTimer.StageBegin(stage);
        __state = Stopwatch.GetTimestamp();
    }

    public static void StagePostfix(EnumRenderStage stage, long __state)
        => FrameStats.AddStageTicks((int)stage, Stopwatch.GetTimestamp() - __state);

    public static void TickPrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void TickPostfix(long __state, IWorldAccessor world)
    {
        // In singleplayer the SERVER runs in the same process, and CoreServerEventManager.
        // TriggerGameTick calls base.TriggerGameTick - the very method patched here - on the
        // server thread. Booking those made "game tick" a mix of two threads, caught by the
        // hitch log with "tick 26,6 ms" inside a 26,2 ms frame during a teleport (long server
        // ticks while loading). Only the client's own tick belongs to the frame accounting.
        if (world is ClientMain)
            FrameStats.AddGameTickTicks(Stopwatch.GetTimestamp() - __state);
    }

    public static void UploadPrefix(out long __state)
    {
        UploadBegin?.Invoke();
        __state = Stopwatch.GetTimestamp();
    }

    public static void UploadPostfix(long __state)
    {
        FrameStats.AddUploadMs((Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency);
        UploadEnd?.Invoke();
    }

    public static void TesselatePrefix(out (long time, long alloc) __state)
        => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    public static void TesselatePostfix((long time, long alloc) __state, int __result, bool skipChunkCenter)
    {
        // a zero result is a chunk that was skipped (empty, not loaded yet, requeued) -
        // counting those would dilute the per-chunk cost with no-ops.
        // The allocation delta is thread-local and answers where a process-wide 869 MB/s
        // came from when the player stood still in front of a chiselled building.
        if (__result > 0)
            TesselationStats.AddChunk(Stopwatch.GetTimestamp() - __state.time, skipChunkCenter,
                GC.GetAllocatedBytesForCurrentThread() - __state.alloc);
    }

    public static void NeighbourPrefix(out (long time, long alloc) __state)
        => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    public static void NeighbourPostfix((long time, long alloc) __state)
    {
        // __state stays default if another patch cancelled the original before our prefix ran -
        // folding "now minus zero" into the average would poison it for the whole session
        if (__state.time != 0)
            TesselationStats.AddNeighbourTicks(Stopwatch.GetTimestamp() - __state.time,
                GC.GetAllocatedBytesForCurrentThread() - __state.alloc);
    }

    public static void AllocPrefix(out long __state) => __state = GC.GetAllocatedBytesForCurrentThread();

    /// <summary>
    /// Nesting depth of the part-clone bracket on this thread. Every overload of
    /// populateTesselatedChunkPart carries the bracket, and an engine build may route one
    /// overload through another: only the outermost call books, so a nested call never
    /// counts the same bytes twice. Thread-static because the bracket runs on the tesselation
    /// thread and (in the verify harness) on the test thread.
    /// </summary>
    [ThreadStatic] private static int partsDepth;

    public static void PartsAllocPrefix(out long __state)
        => __state = partsDepth++ == 0 ? GC.GetAllocatedBytesForCurrentThread() : -1;

    public static void PartsAllocPostfix(long __state)
    {
        if (__state < 0) { partsDepth--; return; }
        // outermost: the depth is known to be exactly one here; resetting rather than
        // decrementing means an inner overload that threw past its postfix cannot leave
        // this thread's bracket stuck at a depth where nothing books ever again
        partsDepth = 0;
        TesselationStats.AddPartsAlloc(GC.GetAllocatedBytesForCurrentThread() - __state);
    }

    public static void JsonAllocPostfix(long __state)
        => TesselationStats.AddJsonAlloc(GC.GetAllocatedBytesForCurrentThread() - __state);

    public static void RelightPrefix(out (long time, long alloc) __state)
        => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    public static void RelightPostfix((long time, long alloc) __state)
        => TesselationStats.AddRelightTicks(Stopwatch.GetTimestamp() - __state.time,
            GC.GetAllocatedBytesForCurrentThread() - __state.alloc);

    // ---- mesh pool creation ----------------------------------------------------------------

    public static void PoolAllocPrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void PoolAllocPostfix(long __state)
        => FrameStats.AddPoolAlloc((Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency);
}
