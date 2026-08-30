using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Vintagestory.API.MathTools;
using Komet;

/// <summary>
/// Vanilla ChunkCuller ray walk vs the hoisted one, on a synthetic chunk map.
/// Checks that both mark exactly the same chunks visible, then times them.
/// </summary>
internal static class ChunkCullerBench
{
    private sealed class FakeChunk
    {
        public bool VisibleVanilla, VisibleFast;
        public ushort Traversability;
        public bool Fresh;

        public bool IsTraversable(int from, int to)
        {
            if (!Fresh) return true;
            return ((Traversability >> ((from * 6 + to) % 15)) & 1) > 0;
        }
    }

    private sealed class World
    {
        public Dictionary<long, FakeChunk> Chunks = new();
        public FakeChunk[] Grid;
        public int MinX, MinY, MinZ, SizeX, SizeY, SizeZ;
        public int MapSizeXZ, MapSizeY;

        public long Index(int cx, int cy, int cz) => ((long)cy * MapSizeXZ + cz) * MapSizeXZ + cx;

        public bool IsValidChunkPos(int cx, int cy, int cz) =>
            cx >= 0 && cx < MapSizeXZ && cy >= 0 && cy < MapSizeY && cz >= 0 && cz < MapSizeXZ;

        public FakeChunk FromGrid(int cx, int cy, int cz)
        {
            int gx = cx - MinX, gy = cy - MinY, gz = cz - MinZ;
            if ((uint)gx >= (uint)SizeX || (uint)gy >= (uint)SizeY || (uint)gz >= (uint)SizeZ) return null;
            return Grid[(gy * SizeZ + gz) * SizeX + gx];
        }
    }

    /// <summary>Surface at chunk y 4; below is solid rock, above is mostly open sky.</summary>
    private static World BuildWorld(int viewDistanceBlocks, int centerX, int centerZ, int mapSizeXZ, int mapSizeY)
    {
        var rnd = new Random(31);
        var w = new World { MapSizeXZ = mapSizeXZ, MapSizeY = mapSizeY };
        int r = viewDistanceBlocks / 32 + 1;

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

        for (int cx = centerX - r; cx <= centerX + r; cx++)
        for (int cz = centerZ - r; cz <= centerZ + r; cz++)
        {
            double d = Math.Sqrt((double)(cx - centerX) * (cx - centerX) + (double)(cz - centerZ) * (cz - centerZ));
            if (d > r) continue;
            for (int cy = 0; cy < 8; cy++)
            {
                if (!w.IsValidChunkPos(cx, cy, cz)) continue;
                var c = new FakeChunk { Fresh = true };
                // underground: nearly opaque. surface: partly. sky: fully open.
                c.Traversability = cy < 4 ? (ushort)(rnd.Next(8) == 0 ? 0x7fff : 0)
                                 : cy == 4 ? (ushort)rnd.Next(0x8000)
                                 : (ushort)0x7fff;
                w.Chunks[w.Index(cx, cy, cz)] = c;
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
            }
        }

        w.MinX = minX; w.MinY = minY; w.MinZ = minZ;
        w.SizeX = maxX - minX + 1; w.SizeY = maxY - minY + 1; w.SizeZ = maxZ - minZ + 1;
        w.Grid = new FakeChunk[w.SizeX * w.SizeY * w.SizeZ];
        foreach (KeyValuePair<long, FakeChunk> kv in w.Chunks)
        {
            long k = kv.Key;
            int cx = (int)(k % w.MapSizeXZ);
            int cz = (int)(k / w.MapSizeXZ % w.MapSizeXZ);
            int cy = (int)(k / w.MapSizeXZ / w.MapSizeXZ);
            w.Grid[((cy - minY) * w.SizeZ + (cz - minZ)) * w.SizeX + (cx - minX)] = kv.Value;
        }
        return w;
    }

    private static Vec3i[] ShellVectors(int viewDistance, int chunkMapSizeY)
    {
        var set = new HashSet<Vec3i>();
        foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, viewDistance / 32 + 1))
            for (int y = -chunkMapSizeY; y <= chunkMapSizeY; y++) set.Add(new Vec3i(p.X, y, p.Y));
        for (int k = 0; k < viewDistance / 32 + 1; k++)
            foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, k))
            {
                set.Add(new Vec3i(p.X, -chunkMapSizeY, p.Y));
                set.Add(new Vec3i(p.X, chunkMapSizeY, p.Y));
            }
        var arr = new Vec3i[set.Count];
        set.CopyTo(arr);
        return arr;
    }

    // ---------------- faithful copy of the vanilla walk ----------------

    private sealed class Vanilla
    {
        private readonly World w;
        private readonly Ray ray = new();
        private readonly Vec3d planePosition = new();
        private readonly Vec3i curpos = new();
        private readonly Vec3i toPos = new();
        public bool AboveHeightLimit = false;

        public Vanilla(World w) { this.w = w; }

        public void Traverse(Vec3i fromPos, Vec3i toPosRel, double yoffset, double xoffset)
        {
            ray.origin.Set(fromPos.X + xoffset, fromPos.Y + yoffset, fromPos.Z + 0.5);
            ray.dir.Set(toPosRel.X + xoffset, toPosRel.Y + yoffset, toPosRel.Z + 0.5);
            toPos.Set(fromPos.X + toPosRel.X, fromPos.Y + toPosRel.Y, fromPos.Z + toPosRel.Z);
            curpos.Set(fromPos);
            BlockFacing blockFacing = null;
            int num = fromPos.ManhattanDistanceTo(toPos);
            int num2;
            while ((num2 = curpos.ManhattanDistanceTo(fromPos)) <= num + 2)
            {
                BlockFacing exitingFace = GetExitingFace(curpos);
                if (exitingFace == null) break;
                w.Chunks.TryGetValue(w.Index(curpos.X, curpos.Y, curpos.Z), out FakeChunk value);
                if (value != null)
                {
                    value.VisibleVanilla = true;
                    if (num2 > 1 && !value.IsTraversable(blockFacing.Index, exitingFace.Index)) break;
                }
                curpos.Set(curpos.X + exitingFace.Normali.X, curpos.Y + exitingFace.Normali.Y, curpos.Z + exitingFace.Normali.Z);
                blockFacing = exitingFace.Opposite;
                if (!w.IsValidChunkPos(curpos.X, curpos.Y, curpos.Z) && (!AboveHeightLimit || curpos.Y <= 0)) break;
            }
        }

        private BlockFacing GetExitingFace(Vec3i pos)
        {
            for (int i = 0; i < 6; i++)
            {
                BlockFacing blockFacing = BlockFacing.ALLFACES[i];
                Vec3i normali = blockFacing.Normali;
                double num = normali.X * ray.dir.X + normali.Y * ray.dir.Y + normali.Z * ray.dir.Z;
                if (num <= 1E-05) continue;
                planePosition.Set(pos).Add(blockFacing.PlaneCenter);
                double num2 = planePosition.X - ray.origin.X;
                double num3 = planePosition.Y - ray.origin.Y;
                double num4 = planePosition.Z - ray.origin.Z;
                double num5 = (num2 * normali.X + num3 * normali.Y + num4 * normali.Z) / num;
                if (num5 >= 0.0
                    && Math.Abs(ray.origin.X + ray.dir.X * num5 - planePosition.X) <= 0.5
                    && Math.Abs(ray.origin.Y + ray.dir.Y * num5 - planePosition.Y) <= 0.5
                    && Math.Abs(ray.origin.Z + ray.dir.Z * num5 - planePosition.Z) <= 0.5)
                {
                    return blockFacing;
                }
            }
            return null;
        }
    }

    // ---------------- the optimised sink ----------------

    private struct GridSink : RayTraversal.IChunkSink
    {
        public World W;

        public bool Visit(int cx, int cy, int cz, int fromFace, int toFace, bool checkBlocking)
        {
            FakeChunk c = W.FromGrid(cx, cy, cz);
            if (c == null) return true;
            c.VisibleFast = true;
            return !checkBlocking || c.IsTraversable(fromFace, toFace);
        }

        public bool IsValidChunkPos(int cx, int cy, int cz) => W.IsValidChunkPos(cx, cy, cz);
    }

    private struct DictSink : RayTraversal.IChunkSink
    {
        public World W;

        public bool Visit(int cx, int cy, int cz, int fromFace, int toFace, bool checkBlocking)
        {
            W.Chunks.TryGetValue(W.Index(cx, cy, cz), out FakeChunk c);
            if (c == null) return true;
            c.VisibleFast = true;
            return !checkBlocking || c.IsTraversable(fromFace, toFace);
        }

        public bool IsValidChunkPos(int cx, int cy, int cz) => W.IsValidChunkPos(cx, cy, cz);
    }

    public static void Run()
    {
        // warm every path to tier 1 first, otherwise the first row measured is inflated
        {
            World w0 = BuildWorld(256, 2048, 2048, 4096, 32);
            Vec3i[] s0 = ShellVectors(256, 32);
            var from0 = new Vec3i(2048, 4, 2048);
            var v0 = new Vanilla(w0);
            for (int rep = 0; rep < 3; rep++)
            {
                foreach (Vec3i rel in s0) { v0.Traverse(from0, rel, 0.25, 0.5); v0.Traverse(from0, rel, 0.75, 0.5); v0.Traverse(from0, rel, 0.75, 0.0); }
                var g0 = new GridSink { W = w0 }; foreach (Vec3i rel in s0) TraceThree(ref g0, from0, rel);
                var d0 = new DictSink { W = w0 }; foreach (Vec3i rel in s0) TraceThree(ref d0, from0, rel);
                Parallel.For(0, s0.Length, () => new GridSink { W = w0 }, (i, _, sk) => { TraceThree(ref sk, from0, s0[i]); return sk; }, _ => { });
            }
        }

        Console.WriteLine("occlusion culling ray walk (ChunkCuller.CullInvisibleChunks)");
        Console.WriteLine($"{"view dist",10}{"rays",10}{"vanilla",12}{"hoisted",12}{"+flat grid",12}{"+parallel",12}{"total",9}");

        foreach (int vd in new[] { 256, 512, 1024, 1536 })
        {
            const int mapSizeXZ = 4096, mapSizeY = 32;
            int centerX = 2048, centerZ = 2048, centerY = 4;
            World w = BuildWorld(vd, centerX, centerZ, mapSizeXZ, mapSizeY);
            Vec3i[] shell = ShellVectors(vd, mapSizeY);
            var from = new Vec3i(centerX, centerY, centerZ);

            // ---- correctness: same visible set ----
            var van = new Vanilla(w);
            foreach (Vec3i rel in shell)
            {
                van.Traverse(from, rel, 0.25, 0.5);
                van.Traverse(from, rel, 0.75, 0.5);
                van.Traverse(from, rel, 0.75, 0.0);
            }
            var sink = new GridSink { W = w };
            foreach (Vec3i rel in shell)
            {
                RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.5, 0.25, false);
                RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.5, 0.75, false);
                RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.0, 0.75, false);
            }
            int diff = 0, visible = 0;
            foreach (FakeChunk c in w.Chunks.Values)
            {
                if (c.VisibleVanilla) visible++;
                if (c.VisibleVanilla != c.VisibleFast) diff++;
            }

            double tVanilla = Time(() => { foreach (Vec3i rel in shell) { van.Traverse(from, rel, 0.25, 0.5); van.Traverse(from, rel, 0.75, 0.5); van.Traverse(from, rel, 0.75, 0.0); } });
            double tDict = Time(() => { var s = new DictSink { W = w }; foreach (Vec3i rel in shell) TraceThree(ref s, from, rel); });
            double tGrid = Time(() => { var s = new GridSink { W = w }; foreach (Vec3i rel in shell) TraceThree(ref s, from, rel); });
            double tPar = Time(() => Parallel.For(0, shell.Length, () => new GridSink { W = w },
                (i, _, s) => { TraceThree(ref s, from, shell[i]); return s; }, _ => { }));

            Console.WriteLine($"{vd,10}{shell.Length * 3,10:N0}{tVanilla,10:F1}ms{tDict,10:F1}ms{tGrid,10:F1}ms{tPar,10:F1}ms{tVanilla / tPar,8:F1}x");
            if (diff != 0) Console.WriteLine($"           !! {diff} chunks differ (of {visible} visible) !!");
        }
        Console.WriteLine("           hoisted = per-ray constants out of the loop; flat grid = array lookup instead of dictionary");
        Console.WriteLine();
    }

    private static void TraceThree<T>(ref T sink, Vec3i from, Vec3i rel) where T : struct, RayTraversal.IChunkSink
    {
        RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.5, 0.25, false);
        RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.5, 0.75, false);
        RayTraversal.Trace(ref sink, from.X, from.Y, from.Z, rel.X, rel.Y, rel.Z, 0.0, 0.75, false);
    }

    private static double Time(Action a)
    {
        a(); a();
        var sw = Stopwatch.StartNew();
        const int iters = 3;
        for (int i = 0; i < iters; i++) a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }
}
