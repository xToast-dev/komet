using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Komet.Culling;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

// Standalone harness: builds a synthetic chunk-mesh pool of the size a real
// viewDistance=1536 client keeps around, then checks that FastCuller produces
// byte-identical output to MeshDataPool.FrustumCull and times both.

internal static class Program
{
    private const int ChunkSize = 32;

    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> LocationsRef =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");

    private static MeshDataPool NewPool(int maxParts)
    {
        var ctor = typeof(MeshDataPool).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(int), typeof(int) }, null);
        var pool = (MeshDataPool)ctor.Invoke(new object[] { 500000, 750000, maxParts });
        pool.indicesStartsByte = new int[maxParts * 2];
        pool.indicesSizes = new int[maxParts];
        return pool;
    }

    /// <summary>
    /// Chunk column coordinates within a radius, ordered by distance from the origin. Cached per
    /// radius because every pool wants the same ordering.
    /// </summary>
    private static readonly Dictionary<int, (int x, int z)[]> RingOrder = new();

    private static (int x, int z)[] DistanceOrdered(int radiusChunks)
    {
        if (RingOrder.TryGetValue(radiusChunks, out var cached)) return cached;
        var all = new List<(int x, int z)>();
        for (int x = -radiusChunks; x <= radiusChunks; x++)
            for (int z = -radiusChunks; z <= radiusChunks; z++)
                all.Add((x, z));
        all.Sort((a, b) => (a.x * a.x + a.z * a.z).CompareTo(b.x * b.x + b.z * b.z));
        var arr = all.ToArray();
        RingOrder[radiusChunks] = arr;
        return arr;
    }

    /// <summary>
    /// Fills a pool with chunk parts in tesselation order: sorted by distance from the player,
    /// so consecutive parts form a RING around the origin rather than a compact blob.
    ///
    /// This used to draw positions uniformly at random while its own comment claimed a spiral,
    /// and that gap silently decided a tuning constant. A uniformly filled square is the best
    /// possible case for a fine spatial grid - every cell gets its share of parts - whereas a
    /// distance-ordered ring makes each pool span the entire view, so fine cells are mostly
    /// empty and cost more in per-cell tests than they save in skipped parts. Measured against
    /// the random layout the grid wanted 32 parts per cell; in the game that made the sweep
    /// slower, which is what sent me back to this function. The ring structure is not a guess:
    /// it is the same fact that made per-64-part bounding boxes useless (see README).
    /// </summary>
    private static MeshDataPool BuildPool(int count, int radiusChunks, Random rnd, int seedOffset)
    {
        var pool = NewPool(count + 8);
        var locs = LocationsRef(pool);
        (int x, int z)[] ring = DistanceOrdered(radiusChunks);
        int indices = 0;
        for (int i = 0; i < count; i++)
        {
            // Walk outwards; several parts per column, as a real chunk contributes several
            // mesh parts, and wrap round for pools larger than the ring.
            (int x, int z) col = ring[(i / 3 + seedOffset) % ring.Length];
            int cx = col.x;
            int cz = col.z;
            int cy = rnd.Next(3, 6);
            int len = 300 + rnd.Next(6000);
            len -= len % 3;

            // every 313th part gets an out-of-range LOD level, which vanilla's switch
            // treats as permanently invisible
            int lod = (i % 313) == 0 ? 7 : (i + seedOffset) % 4;
            var loc = new ModelDataPoolLocation
            {
                IndicesStart = indices,
                IndicesEnd = indices + len,
                VerticesStart = 0,
                VerticesEnd = 0,
                PoolId = 0,
                LodLevel = lod,
                Hide = (i % 97) == 0,
                FrustumCullSphere = Sphere.BoundingSphereForCube(cx * ChunkSize, cy * ChunkSize, cz * ChunkSize, ChunkSize)
            };
            // occlusion culling marks whole chunks invisible via a shared Bools
            if ((i % 11) == 0) loc.CullVisible = new Bools(false, false);
            locs.Add(loc);
            indices += len;
        }
        return pool;
    }

    private static FrustumCulling BuildCuller(int viewDistance, double yaw, bool lod0Disabled = false)
    {
        var culler = new FrustumCulling();
        culler.UpdateViewDistance(viewDistance);
        float lod0 = Math.Min(640, viewDistance) * 0.33f;
        float lod2 = Math.Min(640, viewDistance) * 0.75f;
        culler.lod0BiasSq = lod0Disabled ? 0f : lod0 * lod0;
        culler.lod2BiasSq = lod2 * lod2;
        culler.shadowRangeX = 220.0;
        culler.shadowRangeZ = 220.0;

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70.0 * Math.PI / 180.0, 2560.0 / 1440.0, 0.3, viewDistance * 1.2);

        double[] view = Mat4d.Create();
        double[] eye = { 0.0, 140.0, 0.0 };
        double[] center = { Math.Sin(yaw) * 100.0, 130.0, Math.Cos(yaw) * 100.0 };
        double[] up = { 0.0, 1.0, 0.0 };
        Mat4d.LookAt(view, eye, center, up);

        culler.CalcFrustumEquations(new BlockPos(0, 140, 0, 0), proj, view);
        return culler;
    }

    /// <summary>
    /// Canonical form of what a pool will draw: the byte ranges of the index buffer, with
    /// back-to-back ranges coalesced. glMultiDrawElements renders the concatenation of its
    /// ranges in order, so two pools that produce the same canonical form draw exactly the
    /// same triangles in the same sequence - whether or not the mod merged them.
    /// </summary>
    private static (List<(int start, int end)> runs, int rendered, int allocated) Snapshot(MeshDataPool p)
    {
        var runs = new List<(int, int)>();
        for (int i = 0; i < p.indicesGroupsCount; i++)
        {
            int start = p.indicesStartsByte[i * 2];
            int end = start + p.indicesSizes[i] * 4;
            if (runs.Count > 0 && runs[^1].Item2 == start) runs[^1] = (runs[^1].Item1, end);
            else runs.Add((start, end));
        }
        return (runs, p.RenderedTriangles, p.AllocatedTris);
    }

    private static bool Same(
        (List<(int start, int end)> runs, int rendered, int allocated) a,
        (List<(int start, int end)> runs, int rendered, int allocated) b)
    {
        if (a.rendered != b.rendered || a.allocated != b.allocated) return false;
        if (a.runs.Count != b.runs.Count) return false;
        for (int i = 0; i < a.runs.Count; i++) if (a.runs[i] != b.runs[i]) return false;
        return true;
    }

    private static void Main()
    {
        if (Environment.GetCommandLineArgs().Length > 1) Micro.Run();
        // The sweep timings below are only meaningful against the threads the game actually
        // uses; without this the batch falls back to running inline on this thread.
        FastCuller.StartWorkers();
        DrawRanges.Run();
        ChunkCullerBench.Run();
        UploadBench.Run();
        WindowBench.Run();
        QueueSweepBench.Run();
        FarLodBench.Run();

        var modes = new[]
        {
            EnumFrustumCullMode.CullNormal,
            EnumFrustumCullMode.CullInstant,
            EnumFrustumCullMode.CullInstantShadowPassNear,
            EnumFrustumCullMode.CullInstantShadowPassFar,
            EnumFrustumCullMode.NoCull
        };

        // ---- equivalence over many camera orientations and pool layouts ----
        // Run for the vector kernel AND for the scalar one. They are two implementations of the
        // same decision and only one of them runs on any given CPU, so checking whichever
        // happens to be active here would leave the other unverified - including the scalar
        // tail that finishes every bucket whose length is not a multiple of four.
        // Gap bridging deliberately deviates from vanilla's byte ranges, so the byte-identity
        // sections run without it; its own section below checks the deviation against the
        // verifier's rule instead.
        FastCuller.GapMergeDrawRanges = false;
        FastCuller.PoolLevelCulling = false;
        int checks = 0, mismatches = 0;
        foreach (bool useVector in FastCuller.VectorAvailable ? new[] { true, false } : new[] { false })
        {
            FastCuller.VectorCulling = useVector;
            for (int layout = 0; layout < 12; layout++)
            {
                var rnd = new Random(1234 + layout);
                var pool = BuildPool(1200, 20, rnd, layout);
                for (int step = 0; step < 24; step++)
                {
                    var culler = BuildCuller(1536, step * Math.PI / 12.0);
                    foreach (var mode in modes)
                    {
                        pool.FrustumCull(culler, mode);
                        var vanilla = Snapshot(pool);
                        FastCuller.Cull(pool, culler, mode);
                        var fast = Snapshot(pool);
                        checks++;
                        if (!Same(vanilla, fast))
                        {
                            mismatches++;
                            Console.WriteLine($"  MISMATCH simd={useVector} layout={layout} step={step} mode={mode} " +
                                              $"runs {vanilla.runs.Count}/{fast.runs.Count} tris {vanilla.rendered}/{fast.rendered}");
                        }
                    }
                }
            }
        }
        FastCuller.VectorCulling = FastCuller.VectorAvailable;
        // lod0BiasSq == 0 is the "LOD 0 never renders" case vanilla short-circuits on
        for (int layout = 0; layout < 4; layout++)
        {
            var rnd = new Random(555 + layout);
            var pool = BuildPool(900, 16, rnd, layout);
            for (int step = 0; step < 12; step++)
            {
                var culler = BuildCuller(1536, step * Math.PI / 6.0, lod0Disabled: true);
                foreach (var mode in modes)
                {
                    pool.FrustumCull(culler, mode);
                    var vanilla = Snapshot(pool);
                    FastCuller.Cull(pool, culler, mode);
                    checks++;
                    if (!Same(vanilla, Snapshot(pool))) { mismatches++; Console.WriteLine($"  MISMATCH lod0off layout={layout} step={step} mode={mode}"); }
                }
            }
        }
        Console.WriteLine($"equivalence: {checks - mismatches}/{checks} identical (same triangles, same order)\n");

        // ---- and again with the pool-level box rejection enabled ----
        FastCuller.PoolLevelCulling = true;
        int poolChecks = 0, poolMismatch = 0;
        for (int layout = 0; layout < 12; layout++)
        {
            var rnd = new Random(77 + layout);
            // tight pools, the case where whole-pool rejection can fire
            var pool = BuildPool(600, 4, rnd, layout);
            for (int warm = 0; warm < 2; warm++)
                foreach (var mode in modes) FastCuller.Cull(pool, BuildCuller(1536, 0), mode);

            for (int step = 0; step < 24; step++)
            {
                var culler = BuildCuller(1536, step * Math.PI / 12.0);
                foreach (var mode in modes)
                {
                    pool.FrustumCull(culler, mode);
                    var vanilla = Snapshot(pool);
                    FastCuller.Cull(pool, culler, mode);
                    var fast = Snapshot(pool);
                    poolChecks++;
                    if (!Same(vanilla, fast)) poolMismatch++;
                }
            }
        }
        Console.WriteLine($"equivalence (pool-box on): {poolChecks - poolMismatch}/{poolChecks} identical\n");

        // ---- gap bridging: pixel-equivalence under the verifier's rule ----
        // With bridging on the byte ranges may legally differ from vanilla: a range may cross
        // parts that are provably outside the frustum (the GPU clips them - identical pixels).
        // CullVerifier.Compare with the allowed-list is the arbiter, exactly as in game: every
        // vanilla part drawn, in order, and every extra byte attributed to a clipped part.
        // RenderedTriangles must not move - bridged filler is not "rendered".
        FastCuller.GapMergeDrawRanges = true;
        int gapChecks = 0, gapMismatch = 0;
        long gapBridgedBefore = FastCuller.StatRangesBridged;
        var gapWant = new List<int>(2048);
        var gapAllowed = new List<int>(2048);
        foreach (bool poolBox in new[] { false, true })
        {
            FastCuller.PoolLevelCulling = poolBox;
            for (int layout = 0; layout < 12; layout++)
            {
                var rnd = new Random(4321 + layout);
                var pool = BuildPool(1200, 20, rnd, layout);
                List<ModelDataPoolLocation> locs = LocationsRef(pool);
                for (int step = 0; step < 24; step++)
                {
                    var culler = BuildCuller(1536, step * Math.PI / 12.0);
                    foreach (var mode in modes)
                    {
                        if (mode == EnumFrustumCullMode.NoCull) continue; // no frustum, no bridge
                        pool.FrustumCull(culler, mode);
                        int vanillaTris = pool.RenderedTriangles;
                        FastCuller.Cull(pool, culler, mode);

                        gapWant.Clear();
                        gapAllowed.Clear();
                        foreach (ModelDataPoolLocation loc in locs)
                        {
                            if (CullVerifier.VanillaVisible(loc, mode, culler))
                            {
                                gapWant.Add(loc.IndicesStart * 4);
                                gapWant.Add(loc.IndicesEnd - loc.IndicesStart);
                            }
                            else if (!culler.InFrustum(loc.FrustumCullSphere))
                            {
                                gapAllowed.Add(loc.IndicesStart * 4);
                                gapAllowed.Add(loc.IndicesEnd - loc.IndicesStart);
                            }
                        }

                        gapChecks++;
                        string problem = CullVerifier.Compare(pool.indicesStartsByte, pool.indicesSizes,
                                                              pool.indicesGroupsCount, gapWant, gapAllowed);
                        if (problem == null && pool.RenderedTriangles != vanillaTris)
                            problem = $"rendered tris moved: {pool.RenderedTriangles} vs {vanillaTris}";
                        if (problem != null)
                        {
                            gapMismatch++;
                            Console.WriteLine($"  GAP MISMATCH poolbox={poolBox} layout={layout} step={step} mode={mode}: {problem}");
                        }
                    }
                }
            }
        }
        long gapBridged = FastCuller.StatRangesBridged - gapBridgedBefore;
        Console.WriteLine($"gap bridging: {gapChecks - gapMismatch}/{gapChecks} pixel-equivalent, "
                        + $"{gapBridged} gaps bridged across the run\n");
        if (gapBridged == 0)
            Console.WriteLine("  WARNING: no gap was ever bridged - the section proved nothing\n");

        // ---- throughput ----
        // One frame of a real client: opaque + shadow far + shadow near over every
        // tesselated part. 24000 parts is what a viewDistance=1536 world settles at.
        const int parts = 24000;
        const int poolsOf = 1500;
        var pools = new List<MeshDataPool>();
        var r2 = new Random(9);
        for (int i = 0; i < parts / poolsOf; i++) pools.Add(BuildPool(poolsOf, 30, r2, i));

        var cullers = new FrustumCulling[8];
        for (int i = 0; i < cullers.Length; i++) cullers[i] = BuildCuller(1536, i * Math.PI / 4.0);

        FastCuller.PoolLevelCulling = false;

        // Warm every code path to tier 1 before timing anything, otherwise whichever variant
        // is measured first pays for the JIT promoting it mid-measurement.
        for (int round = 0; round < 400; round++)
            foreach (var mode in modes)
                foreach (var p in pools)
                {
                    p.FrustumCull(cullers[round % cullers.Length], mode);
                    FastCuller.Cull(p, cullers[round % cullers.Length], mode);
                }

        Console.WriteLine($"throughput: {parts} mesh parts, 3 sweeps/frame (opaque + shadow far + shadow near)");
        Console.WriteLine($"{"",-22}{"vanilla",12}{"fast",12}{"speedup",10}");
        foreach (var mode in modes)
        {
            double v = Time(pools, cullers, mode, false);
            double f = Time(pools, cullers, mode, true);
            Console.WriteLine($"{mode,-22}{v,10:F3}ms{f,10:F3}ms{v / f,9:F2}x");
        }

        FastCuller.MergeDrawRanges = false;
        double noMerge = 0;
        foreach (var mode in new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear })
            noMerge += Time(pools, cullers, mode, true);
        FastCuller.MergeDrawRanges = true;

        double frameV = 0, frameF = 0;
        foreach (var mode in new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear })
        {
            frameV += Time(pools, cullers, mode, false);
            frameF += Time(pools, cullers, mode, true);
        }
        Console.WriteLine($"\nper frame (one sweep each): {frameV:F3} ms -> {frameF:F3} ms  ({frameV - frameF:F3} ms saved, {frameV / frameF:F2}x)");
        Console.WriteLine($"  of which range merging costs {frameF - noMerge:F3} ms (fast without merging: {noMerge:F3} ms)");

        // Worst case: a pool whose contents changed this frame has to rebuild its cache
        // before the first sweep. It must still beat three vanilla sweeps.
        // The real client has hundreds of small pools, not a handful of big ones. Per-sweep
        // overhead (cache lookup, plane load, LOD table) is amortised over far fewer parts.
        Console.WriteLine($"\npool shape at a constant {parts:N0} parts (three sweeps per frame):");
        Console.WriteLine($"{"pools",8}{"parts/pool",12}{"vanilla",12}{"fast",12}{"ns/part",10}");
        foreach (int perPool in new[] { 1500, 750, 300, 120, 50 })
        {
            var ps = new List<MeshDataPool>();
            var rr = new Random(9);
            for (int i = 0; i < parts / perPool; i++) ps.Add(BuildPool(perPool, 30, rr, i));

            var three = new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear };
            for (int w = 0; w < 60; w++)
                foreach (var m in three) foreach (var p2 in ps) { p2.FrustumCull(cullers[w % 8], m); FastCuller.Cull(p2, cullers[w % 8], m); }

            double sv = 0, sf = 0;
            foreach (var m in three) { sv += Time(ps, cullers, m, false); sf += Time(ps, cullers, m, true); }
            Console.WriteLine($"{ps.Count,8:N0}{perPool,12:N0}{sv,10:F3}ms{sf,10:F3}ms{sf * 1e6 / (parts * 3),9:F1}");
        }

        // The shape an actual viewDistance=1536 client settles at. The mod's own counters name
        // it: ~290 sweeps a frame over three stages is ~96 pools, and a pool holds up to
        // ClientSettings.ModelDataPoolMaxParts * 2 = 3000 parts. 600 x 640 was the earlier
        // guess here and it is the wrong shape in both directions - six times the pools, a
        // quarter of the parts each - which flatters per-sweep overhead and punishes anything
        // that amortises over a long run of parts.
        {
            // Measured, not estimated (komet report 1.47.0, eingeschwungen, 2026-08-30):
            //   "1.299 sweeps/frame ueber 556 pools", "258.689 teile getestet"
            // The old 96 x 1500 was inferred from a sweep count and is 5.8x off in pool count,
            // which matters because the per-sweep fixed cost is what dominates at this shape:
            // the bench's own pool-shape table puts 16 x 1500 at 2,6 ns/part and 480 x 50 at
            // 13,5. Tuning the cell size against the wrong shape tunes it against the wrong
            // regime - a pool of 300 parts gets one or two grid cells at a target of 160, so
            // the spatial index it was tuned to exercise is barely present.
            const int realPools = 600, realPerPool = 295;
            FastCuller.ForgetAllPools(); // otherwise this section also culls every pool above
            var ps = new List<MeshDataPool>();
            var rr = new Random(9);
            for (int i = 0; i < realPools; i++) ps.Add(BuildPool(realPerPool, 30, rr, i));
            var three = new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear };
            for (int w = 0; w < 12; w++)
                foreach (var m in three) foreach (var p2 in ps) { p2.FrustumCull(cullers[w % 8], m); FastCuller.Cull(p2, cullers[w % 8], m); }

            double sv = 0, sf = 0, sScalar = 0;
            foreach (var m in three) { sv += Time(ps, cullers, m, false); sf += Time(ps, cullers, m, true); }

            // the same sweep with the vector kernel switched off, so the four-lane plane test
            // is measured against the scalar one rather than against a memory of it
            FastCuller.VectorCulling = false;
            foreach (var m in three) sScalar += Time(ps, cullers, m, true);
            FastCuller.VectorCulling = FastCuller.VectorAvailable;

            Console.WriteLine($"\nin-game shape ({realPools} pools x {realPerPool} parts = {realPools * realPerPool:N0}): " +
                              $"{sv:F2} ms -> {sf:F2} ms  ({sv / sf:F1}x)");
            Console.WriteLine(FastCuller.VectorAvailable
                ? $"  of which the vector kernel: {sScalar:F2} ms scalar -> {sf:F2} ms with AVX2 ({sScalar / sf:F2}x)"
                : "  (no AVX2 on this CPU - the scalar kernel is what runs)");

            // Grid cell size, re-measured for the vector kernel.
            //
            // Interleaved across three rounds and scored on the minimum, not measured one
            // setting after another: run sequentially this sweep contradicted itself between
            // invocations (160 was both the worst and the best entry on different runs),
            // because each entry takes seconds and the machine drifts over the sweep. Same
            // reasoning as the in-game stress test's neighbour baselines - a measurement whose
            // answer depends on its position in the schedule is not measuring the setting.
            int[] targets = { 16, 24, 32, 48, 96, 160 };
            var cellMs = new double[targets.Length];
            for (int i = 0; i < cellMs.Length; i++) cellMs[i] = double.MaxValue;

            for (int round = 0; round < 3; round++)
                for (int i = 0; i < targets.Length; i++)
                {
                    FastCuller.PartsPerCellTarget = targets[i];
                    FastCuller.InvalidateAll();
                    foreach (var m in three) foreach (var p2 in ps) FastCuller.Cull(p2, cullers[0], m);
                    double t = 0;
                    foreach (var m in three) t += Time(ps, cullers, m, true);
                    if (t < cellMs[i]) cellMs[i] = t;
                }

            Console.Write("  parts per grid cell:");
            for (int i = 0; i < targets.Length; i++) Console.Write($"  {targets[i]}:{cellMs[i]:F2}ms");
            Console.WriteLine();

            FastCuller.PartsPerCellTarget = 160;
            FastCuller.InvalidateAll();
        }

        double rebuild = TimeWithRebuild(pools, cullers);
        Console.WriteLine($"worst case (every pool dirty every frame): {rebuild:F3} ms vs {frameV:F3} ms vanilla");

        // How it scales, so an in-game part count can be mapped onto a millisecond figure.
        Console.WriteLine($"\nscaling ({"parts",8}{"vanilla",12}{"fast",12}{"saved",10}) - per frame, three sweeps");
        foreach (int count in new[] { 6000, 12000, 24000, 48000 })
        {
            FastCuller.ForgetAllPools(); // each row must measure its own pools, not the previous ones
            var ps = new List<MeshDataPool>();
            var rr = new Random(9);
            for (int i = 0; i < Math.Max(1, count / poolsOf); i++) ps.Add(BuildPool(Math.Min(poolsOf, count), 30, rr, i));

            // These pools are brand new, so the fast path would otherwise be charged for
            // building its spatial index on the very first timed sweep while vanilla, which has
            // no such structure, pays nothing. Warm both - the steady state is what scales.
            var modes3 = new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear };
            for (int w = 0; w < 12; w++)
                foreach (var m in modes3)
                foreach (var p2 in ps) { p2.FrustumCull(cullers[w % cullers.Length], m); FastCuller.Cull(p2, cullers[w % cullers.Length], m); }

            double sv = 0, sf = 0;
            foreach (var mode in modes3)
            {
                sv += Time(ps, cullers, mode, false);
                sf += Time(ps, cullers, mode, true);
            }
            Console.WriteLine($"{"",9}{count,8:N0}{sv,10:F3}ms{sf,10:F3}ms{sv - sf,8:F3}ms");
        }
    }

    /// <summary>All three sweeps, but with every cache forcibly invalidated first.</summary>
    private static double TimeWithRebuild(List<MeshDataPool> pools, FrustumCulling[] cullers)
    {
        var modes = new[] { EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstantShadowPassFar, EnumFrustumCullMode.CullInstantShadowPassNear };
        for (int i = 0; i < 20; i++)
            foreach (var p in pools) { FastCuller.Invalidate(p); foreach (var m in modes) FastCuller.Cull(p, cullers[i % cullers.Length], m); }

        const int iters = 200;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            foreach (var p in pools) { FastCuller.Invalidate(p); foreach (var m in modes) FastCuller.Cull(p, cullers[i % cullers.Length], m); }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    /// <summary>
    /// Best of three runs, not the average.
    ///
    /// A single timed run of this harness drifts by 5-15 % between invocations - the scheduler,
    /// turbo residency and whatever else the machine is doing all only ever ADD time, never
    /// remove it. Averaging folds that noise into the result; the minimum is the run that was
    /// least interrupted, which is the number the code is actually capable of. Two consecutive
    /// measurements of the same mode used to disagree by 50 % in this file, which is enough to
    /// "prove" an optimisation either way.
    /// </summary>
    private static double Time(List<MeshDataPool> pools, FrustumCulling[] cullers, EnumFrustumCullMode mode, bool fast)
    {
        // warmup
        for (int i = 0; i < 20; i++)
            foreach (var p in pools)
                if (fast) FastCuller.Cull(p, cullers[i % cullers.Length], mode);
                else p.FrustumCull(cullers[i % cullers.Length], mode);

        const int iters = 200;
        double best = double.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++)
                foreach (var p in pools)
                    if (fast) FastCuller.Cull(p, cullers[i % cullers.Length], mode);
                    else p.FrustumCull(cullers[i % cullers.Length], mode);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / iters;
            if (ms < best) best = ms;
        }
        return best;
    }
}
