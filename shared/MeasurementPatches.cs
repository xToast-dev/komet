using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

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

    public static void Apply(Harmony harmony)
    {
        MethodInfo stage = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage),
                                              [typeof(EnumRenderStage), typeof(float)])
                           ?? throw new InvalidOperationException("ClientMain.TriggerRenderStage not found");

        harmony.Patch(stage,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(StagePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(StagePostfix))));

        // EventManager.TriggerGameTick is the client's whole game tick; ClientEventManager does
        // not override it, so the base method is what runs.
        MethodInfo tick = AccessTools.Method(typeof(Vintagestory.Common.EventManager), "TriggerGameTick",
                                             [typeof(long), typeof(IWorldAccessor)])
                          ?? throw new InvalidOperationException("EventManager.TriggerGameTick not found");

        harmony.Patch(tick,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(TickPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(TickPostfix))));

        Type tesselator = AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkTesselatorManager")
                          ?? throw new InvalidOperationException("ChunkTesselatorManager not found");
        UploadMethod = AccessTools.Method(tesselator, "OnBeforeFrame", [typeof(float)])
                       ?? throw new InvalidOperationException("OnBeforeFrame(float) not found");

        harmony.Patch(UploadMethod,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(UploadPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(UploadPostfix))));

        // Tesselation throughput: how long one chunk takes to mesh, and how much of that is
        // spent unpacking and assembling the 27 neighbouring chunks. Runs on the tesselation
        // thread; TesselationStats does the cross-thread bookkeeping.
        MethodInfo tesselate = AccessTools.Method(tesselator, "TesselateChunk",
                                   [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()])
                               ?? throw new InvalidOperationException("TesselateChunk not found");
        harmony.Patch(tesselate,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(TesselatePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(TesselatePostfix))));

        MethodInfo buildExt = AccessTools.Method(typeof(ChunkTesselator), "BuildExtendedChunkData")
                              ?? throw new InvalidOperationException("ChunkTesselator.BuildExtendedChunkData not found");
        harmony.Patch(buildExt,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(NeighbourPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(NeighbourPostfix))));

        // Sun relighting that TesselateChunk runs inline before meshing - kept in its own
        // bucket so its share of the per-chunk cost is readable.
        MethodInfo relight = AccessTools.Method(typeof(TerrainIlluminator), "SunRelightChunk",
                                 [typeof(ClientChunk), typeof(Vintagestory.Common.Database.ChunkPos)])
                             ?? throw new InvalidOperationException("SunRelightChunk not found");
        harmony.Patch(relight,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(RelightPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(RelightPostfix))));

        // Allocation attribution INSIDE the meshing pass. A field run measured 219 MB/s on
        // the tesselation thread with the mesh recycler at 100% hits and the neighbour/relight
        // phases at zero - so the churn lives in the meshing itself, and these two brackets
        // split it: the per-part clones (populateTesselatedChunkPart -> CloneUsingRecycler,
        // whose small-mesh fallback and extra arrays allocate fresh) versus the per-block
        // JSON shape tesselation. Alloc-only brackets: two thread-local reads per call.
        MethodInfo populate = AccessTools.Method(typeof(ChunkTesselator), "populateTesselatedChunkPart")
                              ?? throw new InvalidOperationException("populateTesselatedChunkPart not found");
        harmony.Patch(populate,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(AllocPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(PartsAllocPostfix))));

        // the 5-arg overload delegates here, so this one bracket sees every call
        MethodInfo json = AccessTools.Method(typeof(JsonTesselator), "AddJsonModelDataToMesh",
                              [typeof(MeshData), typeof(int), typeof(TCTCache), typeof(IMeshPoolSupplier),
                               typeof(float[]), typeof(IJsonTesselatorHooks), typeof(int)])
                          ?? throw new InvalidOperationException("AddJsonModelDataToMesh not found");
        harmony.Patch(json,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(AllocPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(JsonAllocPostfix))));

        // The network thread's share of the allocation rate. SystemNetworkProcess handles
        // every server packet - chunk intake included - inside its own thread tick, and its
        // allocations were the largest unmeasured block in a field report (150 of 161
        // hitches with a gc pause, ~220 MB/s that no existing row could name). Pure
        // measurement: two thread-local reads per 1 ms tick, behaviour untouched.
        MethodInfo netTick = AccessTools.Method(
                                 AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemNetworkProcess"),
                                 "OnSeperateThreadGameTick")
                             ?? throw new InvalidOperationException(
                                 "SystemNetworkProcess.OnSeperateThreadGameTick not found");
        harmony.Patch(netTick,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(NetAllocPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MeasurementPatches), nameof(NetAllocPostfix))));

        // How long SwapBuffers itself takes, so "ausserhalb" splits into the swap and the
        // rest of the event loop. Under mesa_glthread every stage timing above only measures
        // command *recording*; the driver thread's real work is paid wherever the queue must
        // drain, and the swap is the frame's one guaranteed drain point. A transpiler on the
        // caller rather than a patch on SwapBuffers, because GameWindow.SwapBuffers is a
        // one-line non-virtual method the JIT inlines into window_RenderFrame - a prefix on
        // it would apply cleanly and never run (the dead-profiler lesson).
        MethodInfo renderFrame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame")
                                 ?? throw new InvalidOperationException("window_RenderFrame not found");
        harmony.Patch(renderFrame, transpiler: new HarmonyMethod(
            AccessTools.Method(typeof(MeasurementPatches), nameof(WrapSwapBuffers))));
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
        MethodInfo prefix = AccessTools.Method(typeof(MeasurementPatches), nameof(SwapPrefix));
        MethodInfo postfix = AccessTools.Method(typeof(MeasurementPatches), nameof(SwapPostfix));

        int wrapped = 0;
        foreach (CodeInstruction ins in instructions)
        {
            bool isSwap = ins.operand is MethodInfo m && m.Name == "SwapBuffers" && m.GetParameters().Length == 0;
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

    public static void StagePrefix(EnumRenderStage stage, out long __state)
    {
        if (stage == EnumRenderStage.Before) { FrameStats.BeginFrame(); FrameBoundary?.Invoke(); }
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

    public static void PartsAllocPostfix(long __state)
        => TesselationStats.AddPartsAlloc(GC.GetAllocatedBytesForCurrentThread() - __state);

    public static void JsonAllocPostfix(long __state)
        => TesselationStats.AddJsonAlloc(GC.GetAllocatedBytesForCurrentThread() - __state);

    public static void RelightPrefix(out (long time, long alloc) __state)
        => __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    public static void RelightPostfix((long time, long alloc) __state)
        => TesselationStats.AddRelightTicks(Stopwatch.GetTimestamp() - __state.time,
            GC.GetAllocatedBytesForCurrentThread() - __state.alloc);
}
