using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet;

/// <summary>
/// Builds the tesselator's 34x34x34 neighbourhood window for the NEXT queued chunk on a
/// worker thread, while the tesselation thread is still meshing the current one.
///
/// BuildExtendedChunkData is 25-38 % of the per-chunk cost and runs on the one tesselation
/// thread the engine allows. The build for chunk n+1 depends only on chunk data, not on the
/// meshing of chunk n - so it can overlap. The catch that kept this deferred:
/// ClientChunkData.BuildFastBlockAccessArray writes the STATIC
/// BlockChunkDataLayer.blocksByPaletteIndex, which GetRange_Faster reads through its
/// SelectDelegateBlockClient delegates - two window builds running at once corrupt each
/// other. Every build here therefore holds <see cref="BuildLock"/>, and the patch makes the
/// tesselation thread's own build hold it too.
///
/// The result is COPIED into the tesselator's arrays on a hit (three Array.Copy, ~0.05 ms
/// against the ~1.2 ms build) rather than swapping references - the target fields are
/// readonly, and a copy leaves the engine's object graph untouched.
///
/// Staleness safety, in vanilla's own terms: vanilla reads chunk data without a lock and
/// heals races via dirty retesselation, so a window built a few milliseconds early has the
/// same semantics as vanilla's own unlocked read. Two guards close the gaps that would NOT
/// heal: a window is rejected when any of its 27 chunks' Data object was replaced since the
/// build (a re-sent chunk), and when a SunRelightChunk ran after the build started (relight
/// runs at pop time, before vanilla's build, so a prebuilt window would bake pre-relight
/// light values).
///
/// The fill order is replayed from precomputed plans - flat int arrays describing exactly
/// the GetRange/GetOne calls vanilla makes. The plans are pure arithmetic, built once and
/// verified against a first-principles mapping in the test suite; the engine methods then do
/// the same work on the same data in the same order.
/// </summary>
public static class WindowPrebuilder
{
    public static bool Enabled;

    /// <summary>Windows delivered by the worker and used by the tesselation thread.</summary>
    public static long StatHits;
    /// <summary>Vanilla builds: no window ready, wrong chunk predicted, or validation mode.</summary>
    public static long StatMisses;
    /// <summary>Ready windows rejected as stale (data replaced or relight ran).</summary>
    public static long StatStale;
    /// <summary>Validated hits: window compared element-wise against a vanilla build.</summary>
    public static long StatValidated;

    /// <summary>
    /// While positive, a would-be hit instead lets vanilla build and compares the two windows
    /// element by element - the counter-check discipline applied to real game data, since no
    /// offline test can fake 27 live chunks.
    /// </summary>
    public static int ValidateRemaining;

    /// <summary>
    /// Validation mismatches seen. A single mismatch is NOT proof of a transcription bug:
    /// the comparison rebuilds the window a few milliseconds later, and any block or light
    /// that changed in between (lighting settling right after world join, a server block
    /// update) legitimately differs - vanilla's fresh build wins on that chunk either way.
    /// A real transcription bug is systematic: the fill plan is deterministic, so it would
    /// fail every window, not two in two hundred. Field data that shaped this rule: two
    /// mismatches in the first minute after world join, then 200 identical.
    /// </summary>
    public static int StatValidationMismatches;

    /// <summary>Mismatches at which sporadic stops being believable and the feature stops.</summary>
    internal const int MismatchHardLimit = 5;

    /// <summary>Set when the feature disabled itself (mismatch limit, worker crashes). The
    /// stress test and safemode restore paths must not resurrect a self-disabled feature;
    /// only an explicit `.komet toggle prebuild` clears this.</summary>
    public static bool HardDisabled;

    public static Action<string> Log;

    /// <summary>
    /// Serialises every window build in the process - the worker's and the tesselation
    /// thread's own - because of the static palette table described above. The patch class
    /// acquires it around the original BuildExtendedChunkData.
    /// </summary>
    internal static readonly object BuildLock = new();

    private const int WindowDim = 34;
    private const int WindowCells = WindowDim * WindowDim * WindowDim; // 39304
    private const long ExtraDimensionsStart = 4503599627370496L;

    // ---- engine access -------------------------------------------------------------

    private static readonly AccessTools.FieldRef<ChunkTesselator, ClientMain> GameRef =
        AccessTools.FieldRefAccess<ChunkTesselator, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> BlocksExtRef =
        AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> FluidsExtRef =
        AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkFluidBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, int[]> RgbsExtRef =
        AccessTools.FieldRefAccess<ChunkTesselator, int[]>("currentChunkRgbsExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> BlocksFastRef =
        AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("blocksFast");
    private static readonly AccessTools.FieldRef<ChunkTesselator, ColorUtil.LightUtil> LightConvRef =
        AccessTools.FieldRefAccess<ChunkTesselator, ColorUtil.LightUtil>("lightConverter");

    // The dirty-queue refs live in ClientQueues, shared with the tesselation patches and
    // the edge-retess priority sweep.

    // ClientChunkData and BlockChunkDataLayer are internal types, so their public methods are
    // reachable only through emitted thunks (a plain reflection Invoke per window cell row
    // would cost more than the build itself).
    private delegate void GetNeighboursDel(ClientWorldMap map, ClientChunk[] into, int cx, int cy, int cz);
    private delegate void BuildFastDel(object data, Block[] blocks);
    private delegate void GetRangeDel(object data, Block[] blocks, Block[] fluids, int[] rgbs,
                                      int extIdx, int idx, int idxEnd, Block[] blocksFast, ColorUtil.LightUtil conv);
    private delegate int GetOneDel(object data, out ushort lightOut, out int lightSatOut, out int fluidBlockId, int index3d);
    private delegate void ClearPaletteDel(object layer, int maxValue);

    private static GetNeighboursDel getNeighbours;
    private static BuildFastDel buildFastAccess;
    private static GetRangeDel getRangeFaster;
    private static GetRangeDel getRange;
    private static GetOneDel getOne;
    private static ClearPaletteDel clearPalette;
    private static FieldInfo blocksLayerField;

    /// <summary>ldarg*, castclass on the receiver, call, ret - visibility checks skipped.</summary>
    private static TDel Emit<TDel>(MethodInfo target) where TDel : Delegate
    {
        MethodInfo invoke = typeof(TDel).GetMethod("Invoke");
        ParameterInfo[] pars = invoke.GetParameters();
        Type[] argTypes = new Type[pars.Length];
        for (int i = 0; i < pars.Length; i++) argTypes[i] = pars[i].ParameterType;

        var dm = new DynamicMethod("komet_" + target.Name, invoke.ReturnType, argTypes,
                                   typeof(WindowPrebuilder).Module, skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        if (!target.IsStatic && argTypes[0] == typeof(object))
            il.Emit(OpCodes.Castclass, target.DeclaringType);
        for (int i = 1; i < argTypes.Length; i++) il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return (TDel)dm.CreateDelegate(typeof(TDel));
    }

    public static void EnsureReady()
    {
        if (getNeighbours != null) return;

        if (GameRef == null || BlocksExtRef == null || FluidsExtRef == null || RgbsExtRef == null
            || BlocksFastRef == null || LightConvRef == null
            || ClientQueues.Dirty == null || ClientQueues.DirtyPrio == null
            || ClientQueues.DirtyLock == null || ClientQueues.DirtyPrioLock == null)
            throw new InvalidOperationException("ChunkTesselator/ClientMain internals not found");

        Type dataType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientChunkData")
                        ?? throw new InvalidOperationException("ClientChunkData not found");
        blocksLayerField = AccessTools.Field(dataType, "blocksLayer")
                           ?? throw new InvalidOperationException("blocksLayer not found");

        getNeighbours = Emit<GetNeighboursDel>(
            AccessTools.Method(typeof(ClientWorldMap), "GetNeighbouringChunks")
            ?? throw new InvalidOperationException("GetNeighbouringChunks not found"));
        buildFastAccess = Emit<BuildFastDel>(
            AccessTools.Method(dataType, "BuildFastBlockAccessArray")
            ?? throw new InvalidOperationException("BuildFastBlockAccessArray not found"));
        getRangeFaster = Emit<GetRangeDel>(
            AccessTools.Method(dataType, "GetRange_Faster")
            ?? throw new InvalidOperationException("GetRange_Faster not found"));
        getRange = Emit<GetRangeDel>(
            AccessTools.Method(dataType, "GetRange")
            ?? throw new InvalidOperationException("GetRange not found"));
        getOne = Emit<GetOneDel>(
            AccessTools.Method(dataType, "GetOne")
            ?? throw new InvalidOperationException("GetOne not found"));
        clearPalette = Emit<ClearPaletteDel>(
            AccessTools.Method(blocksLayerField.FieldType, "ClearPaletteOutsideMaxValue")
            ?? throw new InvalidOperationException("ClearPaletteOutsideMaxValue not found"));
    }

    // ---- fill plans ----------------------------------------------------------------
    // A plan is the exact sequence of engine calls vanilla's BuildExtendedChunkData makes,
    // with all the index arithmetic done once up front. GetRange* writes with pre-increment
    // semantics: extIndex3d is the index BEFORE the first written cell.

    /// <summary>Center plan: triples of (extIndex3d, srcStart, srcEnd) for GetRange_Faster on the center chunk.</summary>
    internal static int[] BuildCenterPlan(bool skipChunkCenter)
    {
        var plan = new List<int>(skipChunkCenter ? 32 * 32 * 3 * 2 : 32 * 32 * 3);
        int src = 0;
        const int dstBase = (1 * WindowDim + 1) * WindowDim; // 1190: window cell (y=1, z=1, x=0)

        for (int i = 0; i < 32; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                int dst = (i * WindowDim + j) * WindowDim + dstBase;
                if (!skipChunkCenter || (i + 2) % 32 <= 3 || (j + 2) % 32 <= 3)
                {
                    plan.Add(dst); plan.Add(src); plan.Add(src + 32);
                    src += 32;
                }
                else
                {
                    // edge-only: the first two and last two cells of the row
                    plan.Add(dst); plan.Add(src); plan.Add(src + 2);
                    plan.Add(dst + 30); plan.Add(src + 30); plan.Add(src + 32);
                    src += 32;
                }
            }
        }
        return plan.ToArray();
    }

    internal const int OpOne = 0;
    internal const int OpRange = 1;

    /// <summary>
    /// Border plan: quads of (op, neighbourIndex, srcIndex, dstIndex). OpOne writes exactly
    /// the cell dstIndex; OpRange is a GetRange with extIndex3d = dstIndex (32 cells at
    /// dstIndex+1..dstIndex+32). Neighbour indices follow GetNeighbouringChunks' layout:
    /// (dx+1)*9 + (dy+1)*3 + (dz+1), center = 13.
    /// </summary>
    internal static int[] BuildBorderPlan()
    {
        var plan = new List<int>(WindowDim * WindowDim * 4 * 3);
        int dst = -1;

        for (int m = 0; m < WindowDim; m++)          // window y
        {
            for (int n = 0; n < WindowDim; n++)      // window z
            {
                int dy = m == 0 ? 0 : m == WindowDim - 1 ? 2 : 1;
                int dz = n == 0 ? 0 : n == WindowDim - 1 ? 2 : 1;
                int srcRow = (((m - 1) & 0x1F) * 32 + ((n - 1) & 0x1F)) * 32;
                int chunk = dy * 3 + dz;             // dx = -1 layer

                plan.Add(OpOne); plan.Add(chunk); plan.Add(srcRow + 31); plan.Add(++dst);

                chunk += 9;                          // dx = 0 layer
                if (chunk == 13)
                {
                    dst += 32;                       // interior row: the center plan owns it
                }
                else
                {
                    plan.Add(OpRange); plan.Add(chunk); plan.Add(srcRow); plan.Add(dst);
                    dst += 32;
                }

                chunk += 9;                          // dx = +1 layer
                plan.Add(OpOne); plan.Add(chunk); plan.Add(srcRow); plan.Add(++dst);
            }
        }
        return plan.ToArray();
    }

    private static readonly int[] CenterPlanFull = BuildCenterPlan(false);
    private static readonly int[] CenterPlanSkip = BuildCenterPlan(true);
    private static readonly int[] BorderPlan = BuildBorderPlan();

    /// <summary>The staleness rule, stated once: a window is only current when nothing that
    /// vanilla's fresh read would have seen happened after the build began.</summary>
    internal static bool WindowIsCurrent(long builtAt, long lastRelightAt, bool dataRefsMatch)
        => dataRefsMatch && builtAt > lastRelightAt;

    /// <summary>
    /// The staleness test that actually holds: no chunk of the neighbourhood may have been
    /// marked dirty since the window build started.
    ///
    /// The original guard compared <c>chunk.Data</c> by reference, which is blind to the
    /// common case: a block change, a server light update or an arriving neighbour mutate the
    /// chunk's data IN PLACE, leaving the same object. Stale windows therefore passed the
    /// guard and were caught only by the element-wise validation - which stops after the
    /// first N windows, so from then on a stale window silently produced a chunk mesh with
    /// wrong blocks and wrong light: chunk-shaped lighting errors in the world.
    ///
    /// Every one of those mutations marks the chunk dirty (SetChunkDirty / MarkChunkDirty),
    /// so a per-chunk timestamp of the last mark is the exact signal, and it covers the light
    /// path (ClientSystemRelight) as well as block edits.
    /// </summary>
    internal static bool NeighbourhoodUnchanged(long[] keys, long builtAt)
    {
        for (int i = 0; i < keys.Length; i++)
            if (ChunkMarkClock.LastMark(keys[i]) >= builtAt) return false;
        return true;
    }

    // ---- worker state --------------------------------------------------------------

    private static Thread worker;
    private static readonly AutoResetEvent wake = new(false);
    private static volatile bool stop;

    private static ChunkTesselator tess;
    private static ClientMain game;

    private static readonly object reqLock = new();
    private static long reqKey = long.MinValue;
    private static int reqGen, doneGen;

    // buffers + the snapshot describing what they contain
    private static Block[] blocksExt, fluidsExt;
    private static int[] rgbsExt;
    private static readonly ClientChunk[] hood = new ClientChunk[27];
    private static readonly object[] datas = new object[27];
    private static readonly ClientChunk[] snapChunks = new ClientChunk[27];
    private static readonly object[] snapDatas = new object[27];
    private static readonly long[] snapKeys = new long[27];

    private static readonly AccessTools.FieldRef<object, int> MapMulXRef =
        AccessTools.FieldRefAccess<int>(AccessTools.TypeByName("Vintagestory.Common.WorldMap"), "index3dMulX");
    private static readonly AccessTools.FieldRef<object, int> MapMulZRef =
        AccessTools.FieldRefAccess<int>(AccessTools.TypeByName("Vintagestory.Common.WorldMap"), "index3dMulZ");

    private static volatile bool ready;
    private static int readyCx, readyCy, readyCz;
    private static bool readySkip;
    private static long builtAt;
    private static long lastRelightAt;

    private static bool pendingValidate;
    private static int failures;

    /// <summary>Windows between two canary validations once the initial run is done.</summary>
    private const int CanarySampleEvery = 64;
    private static int sinceValidation;

    public static void NoteRelight() => Volatile.Write(ref lastRelightAt, Stopwatch.GetTimestamp());

    /// <summary>First contact with the (single) tesselator instance; sizes the buffers.</summary>
    private static bool Bind(ChunkTesselator t)
    {
        if (ReferenceEquals(tess, t) && ReferenceEquals(game, GameRef(t))) return true;

        Block[] engineArr = BlocksExtRef(t);
        if (engineArr == null || engineArr.Length != WindowCells)
        {
            // a different chunk size than the plans were built for - stand down entirely
            Enabled = false;
            return false;
        }

        ready = false;
        tess = t;
        game = GameRef(t);
        blocksExt ??= new Block[WindowCells];
        fluidsExt ??= new Block[WindowCells];
        rgbsExt ??= new int[WindowCells];

        if (worker is not { IsAlive: true })
        {
            stop = false;
            worker = new Thread(WorkerLoop)
            {
                Name = "komet-window-prebuild",
                IsBackground = true
            };
            worker.Start();
        }
        return true;
    }

    /// <summary>
    /// Called on the tesselation thread, under <see cref="BuildLock"/>, right before vanilla
    /// would build the window. True = the tesselator's arrays now hold the prebuilt window
    /// and the original build can be skipped.
    /// </summary>
    public static bool TryUse(ChunkTesselator t, int cx, int cy, int cz, bool skipChunkCenter)
    {
        if (!Enabled || !Bind(t)) return false;

        if (!ready || readyCx != cx || readyCy != cy || readyCz != cz || readySkip != skipChunkCenter)
        {
            StatMisses++;
            return false;
        }

        // consumed either way from here on - a stale or validated window must not linger
        ready = false;

        bool refsMatch = true;
        for (int i = 0; i < 27 && refsMatch; i++)
            refsMatch = ReferenceEquals(((IWorldChunk)snapChunks[i]).Data, snapDatas[i]);

        if (!WindowIsCurrent(builtAt, Volatile.Read(ref lastRelightAt), refsMatch)
            || !NeighbourhoodUnchanged(snapKeys, builtAt))
        {
            StatStale++;
            StatMisses++;
            return false;
        }

        // Validation is no longer a phase that ends: after the initial run every Nth window is
        // still compared against vanilla's. The first version stopped after N, so from then on
        // a stale window could reach the mesh with nothing watching - which is exactly how
        // chunk-shaped lighting errors got into the world unnoticed. A canary that costs
        // 1/64th of the windows is the price of never having to trust the guard blindly.
        if (!skipChunkCenter && (ValidateRemaining > 0 || ++sinceValidation >= CanarySampleEvery))
        {
            if (ValidateRemaining == 0) sinceValidation = 0;
            // let vanilla build, then compare in AfterBuild - counts as a miss for throughput
            pendingValidate = true;
            StatMisses++;
            return false;
        }

        Array.Copy(blocksExt, BlocksExtRef(t), WindowCells);
        Array.Copy(fluidsExt, FluidsExtRef(t), WindowCells);
        Array.Copy(rgbsExt, RgbsExtRef(t), WindowCells);
        StatHits++;
        return true;
    }

    /// <summary>
    /// Called on the tesselation thread, still under <see cref="BuildLock"/>, after the
    /// window phase (vanilla's or ours). Finishes a pending validation, then predicts the
    /// next queue entry and wakes the worker so its window is ready when meshing ends.
    /// </summary>
    public static void AfterBuild(ChunkTesselator t, int cx, int cy, int cz, bool skipChunkCenter)
    {
        if (!Enabled || tess == null) return;

        if (pendingValidate)
        {
            pendingValidate = false;
            if (readyCx == cx && readyCy == cy && readyCz == cz && !skipChunkCenter)
                CompareAgainstVanilla(t);
        }

        RequestNext();
    }

    /// <summary>
    /// Element-wise comparison of our window against the one vanilla just built from the
    /// same data under the same lock. Blocks compare by reference (both sides pick objects
    /// out of the same blocksFast table), light as int. A mismatch means the plan replay is
    /// NOT equivalent to vanilla - that is a bug here, so the feature turns itself off.
    /// </summary>
    private static void CompareAgainstVanilla(ChunkTesselator t)
    {
        Block[] vb = BlocksExtRef(t); Block[] vf = FluidsExtRef(t); int[] vr = RgbsExtRef(t);
        for (int i = 0; i < WindowCells; i++)
        {
            if (!ReferenceEquals(vb[i], blocksExt[i]) || !ReferenceEquals(vf[i], fluidsExt[i]) || vr[i] != rgbsExt[i])
            {
                bool disable = NoteMismatch();
                Log?.Invoke($"window prebuild validation mismatch {StatValidationMismatches}/{MismatchHardLimit} at cell {i} "
                    + $"(blocks {!ReferenceEquals(vb[i], blocksExt[i])}, fluids {!ReferenceEquals(vf[i], fluidsExt[i])}, "
                    + $"rgb {vr[i] != rgbsExt[i]}) - window discarded, vanilla build used"
                    + (disable ? "; limit reached, feature disabled for this session" : ""));
                return;
            }
        }
        StatValidated++;
        if (--ValidateRemaining == 0)
            Log?.Invoke($"window prebuild: {StatValidated} windows validated against vanilla"
                + (StatValidationMismatches > 0
                    ? $", {StatValidationMismatches} mismatches discarded (in-between world changes)"
                    : ", all identical") + " - validation done");
    }

    /// <summary>Counts a mismatch; true once sporadic has become systematic.</summary>
    internal static bool NoteMismatch()
    {
        if (++StatValidationMismatches < MismatchHardLimit) return false;
        Enabled = false;
        HardDisabled = true;
        return true;
    }

    /// <summary>Peeks the front of the tesselation queue - the chunk the thread pops next.</summary>
    private static void RequestNext()
    {
        ClientMain g = game;
        if (g == null) return;

        long key = long.MinValue;
        UniqueQueue<long> prio = ClientQueues.DirtyPrio(g);
        object prioLock = ClientQueues.DirtyPrioLock(g);
        if (prio != null && prioLock != null && prio.Count > 0)
        {
            lock (prioLock) { foreach (long k in prio) { key = k; break; } }
        }
        if (key == long.MinValue)
        {
            UniqueQueue<long> dirty = ClientQueues.Dirty(g);
            object dirtyLock = ClientQueues.DirtyLock(g);
            if (dirty == null || dirtyLock == null || dirty.Count == 0) return;
            lock (dirtyLock) { foreach (long k in dirty) { key = k; break; } }
        }
        if (key == long.MinValue || (key & 0x7FFFFFFFFFFFFFFFL) >= ExtraDimensionsStart) return;

        lock (reqLock)
        {
            reqKey = key;
            reqGen++;
        }
        wake.Set();
    }

    private static void WorkerLoop()
    {
        var pos = new Vec3i();
        while (!stop)
        {
            try
            {
                wake.WaitOne(500);
                if (stop) return;

                long key;
                int gen;
                lock (reqLock)
                {
                    if (reqGen == doneGen || reqKey == long.MinValue) continue;
                    key = reqKey;
                    gen = reqGen;
                }

                ClientMain g = game;
                ClientWorldMap map = g?.WorldMap;
                if (g == null || g.disposed || map == null) continue;

                bool skip = key < 0;
                MapUtil.PosInt3d(key & 0x7FFFFFFFFFFFFFFFL, map.index3dMulX, map.index3dMulZ, pos);

                lock (BuildLock)
                {
                    BuildWindow(map, pos.X, pos.Y, pos.Z, skip);
                }

                lock (reqLock) { doneGen = gen; }
            }
            catch (Exception)
            {
                // a chunk vanished mid-build, the world is shutting down, or similar - the
                // prebuilder is an accelerator, never a dependency
                ready = false;
                if (++failures > 50) { Enabled = false; HardDisabled = true; return; }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The faithful replay of BuildExtendedChunkData, into our buffers.</summary>
    private static void BuildWindow(ClientWorldMap map, int cx, int cy, int cz, bool skipChunkCenter)
    {
        ready = false;
        long startedAt = Stopwatch.GetTimestamp();

        getNeighbours(map, hood, cx, cy, cz);        // locks chunksLock internally
        if (hood[13] == null || hood[13].Empty) return;

        int blockCount = game.Blocks.Count;
        for (int i = 26; i >= 0; i--)
        {
            hood[i].Unpack();                        // idempotent under the chunk's own lock
            object data = ((IWorldChunk)hood[i]).Data;
            datas[i] = data;
            object layer = blocksLayerField.GetValue(data);
            if (layer != null) clearPalette(layer, blockCount);
        }

        Block[] blocksFast = BlocksFastRef(tess);    // id -> Block, filled once at startup
        ColorUtil.LightUtil conv = LightConvRef(tess);
        buildFastAccess(datas[13], blocksFast);      // writes the static palette table (under BuildLock)

        int[] centerPlan = skipChunkCenter ? CenterPlanSkip : CenterPlanFull;
        object center = datas[13];
        for (int p = 0; p < centerPlan.Length; p += 3)
            getRangeFaster(center, blocksExt, fluidsExt, rgbsExt,
                centerPlan[p], centerPlan[p + 1], centerPlan[p + 2], blocksFast, conv);

        int[] border = BorderPlan;
        for (int p = 0; p < border.Length; p += 4)
        {
            object data = datas[border[p + 1]];
            int src = border[p + 2];
            int dst = border[p + 3];
            if (border[p] == OpOne)
            {
                int id = getOne(data, out ushort light, out int lightSat, out int fluidId, src);
                blocksExt[dst] = blocksFast[id];
                fluidsExt[dst] = blocksFast[fluidId];
                rgbsExt[dst] = conv.ToRgba(light, lightSat);
            }
            else
            {
                getRange(data, blocksExt, fluidsExt, rgbsExt, dst, src, src + 32, blocksFast, conv);
            }
        }

        Block air = blocksFast[0];
        for (int i = 0; i < WindowCells; i++)
            if (blocksExt[i] == null) blocksExt[i] = air;

        for (int i = 0; i < 27; i++)
        {
            snapChunks[i] = hood[i];
            snapDatas[i] = datas[i];
            hood[i] = null;
            datas[i] = null;
        }

        // The 27 keys this window was built from, so TryUse can ask whether any of them was
        // marked dirty since - the guard that a Data reference compare cannot provide.
        int mulX = MapMulXRef(map), mulZ = MapMulZRef(map);
        int k = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    snapKeys[k++] = ChunkMarkClock.Key(cx + dx, cy + dy, cz + dz, mulX, mulZ);

        readyCx = cx; readyCy = cy; readyCz = cz;
        readySkip = skipChunkCenter;
        builtAt = startedAt;
        ready = true;
    }

    public static void Shutdown()
    {
        stop = true;
        wake.Set();
        ready = false;
        game = null;
        tess = null;
        // chunk keys are world-specific; carrying them into the next world would compare
        // timestamps against marks that belong to a different map
        ChunkMarkClock.Clear();
    }
}
