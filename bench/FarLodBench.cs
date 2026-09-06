using System;
using System.Diagnostics;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

/// <summary>
/// What the far LOD build costs on the tesselation thread, on a chunk shaped like the game's:
/// a rolling heightfield with a ridge (grass tops in a TopSoil part, soil sides in an Opaque
/// part), tall grass on two fifths of the columns (crosses, OpaqueNoCull) and a few trees
/// (logs as cubes, leaves as cubes rotated about y, so they are rest faces). Tier 1 from
/// the parts, tier 2 from tier 1's outputs - the pair the game runs per chunk.
/// </summary>
internal static class FarLodBench
{
    private static MeshData Empty(int quads, bool topsoil)
    {
        var m = new MeshData(quads * 4) { VerticesPerFace = 4, IndicesPerFace = 6 };
        m.CustomInts = new CustomMeshDataPartInt(quads * 4) { InterleaveStride = 4 };
        if (topsoil) m.CustomShorts = new CustomMeshDataPartShort(quads * 8) { InterleaveStride = 4 };
        return m;
    }

    private static int Normal(int axis, bool positive)
    {
        var sgn = positive ? 1f : -1f;
        return VertexFlags.PackNormal(new Vec3f(axis == 0 ? sgn : 0, axis == 1 ? sgn : 0, axis == 2 ? sgn : 0));
    }

    private static void Face(MeshData m, int axis, float plane, int b, int c, int flags)
    {
        int[] db = { 0, 1, 1, 0 }, dc = { 0, 0, 1, 1 };
        var ab = (axis + 1) % 3; var ac = (axis + 2) % 3;
        var d = m.VerticesCount;
        for (var j = 0; j < 4; j++)
        {
            var o = (d + j) * 3;
            m.xyz[o + axis] = plane;
            m.xyz[o + ab] = b + db[j];
            m.xyz[o + ac] = c + dc[j];
            m.Uv[(d + j) * 2] = 0.1f + 0.1f * db[j];
            m.Uv[(d + j) * 2 + 1] = 0.2f + 0.1f * dc[j];
            for (var k = 0; k < 4; k++) m.Rgba[(d + j) * 4 + k] = 255;
            m.Flags[d + j] = flags;
            m.CustomInts.Values[d + j] = 0x1234;
            if (m.CustomShorts != null) { m.CustomShorts.Values[(d + j) * 2] = 1000; m.CustomShorts.Values[(d + j) * 2 + 1] = 2001; }
        }
        Close(m, d);
    }

    private static void Quad(MeshData m, ReadOnlySpan<float> xyz, int flags)
    {
        var d = m.VerticesCount;
        for (var j = 0; j < 4; j++)
        {
            var o = (d + j) * 3;
            m.xyz[o] = xyz[j * 3]; m.xyz[o + 1] = xyz[j * 3 + 1]; m.xyz[o + 2] = xyz[j * 3 + 2];
            m.Uv[(d + j) * 2] = 0.1f + 0.1f * (j & 1);
            m.Uv[(d + j) * 2 + 1] = 0.2f + 0.1f * (j >> 1);
            for (var k = 0; k < 4; k++) m.Rgba[(d + j) * 4 + k] = 255;
            m.Flags[d + j] = flags;
            m.CustomInts.Values[d + j] = 0x40;
        }
        Close(m, d);
    }

    private static void Close(MeshData m, int d)
    {
        m.CustomInts.Count = d + 4;
        if (m.CustomShorts != null) m.CustomShorts.Count = (d + 4) * 2;
        var i = m.IndicesCount;
        m.Indices[i] = d; m.Indices[i + 1] = d + 1; m.Indices[i + 2] = d + 2;
        m.Indices[i + 3] = d; m.Indices[i + 4] = d + 2; m.Indices[i + 5] = d + 3;
        m.VerticesCount = d + 4;
        m.IndicesCount = i + 6;
    }

    /// <summary>A cube's six faces rotated by angle about y around the block's centre.</summary>
    private static void RotatedCube(MeshData m, float x, float y, float z, float angle, int flags)
    {
        float cx = x + 0.5f, cz = z + 0.5f;
        var c = MathF.Cos(angle); var s = MathF.Sin(angle);
        Span<float> v = stackalloc float[12];
        for (var face = 0; face < 6; face++)
        {
            var axis = face / 2; var positive = (face & 1) != 0;
            var ab = (axis + 1) % 3; var ac = (axis + 2) % 3;
            int[] db = { 0, 1, 1, 0 }, dc = { 0, 0, 1, 1 };
            for (var j = 0; j < 4; j++)
            {
                Span<float> p = stackalloc float[3];
                p[axis] = positive ? 1 : 0;
                p[ab] = db[j];
                p[ac] = dc[j];
                var lx = p[0] - 0.5f; var lz = p[2] - 0.5f;
                v[j * 3] = cx + lx * c - lz * s;
                v[j * 3 + 1] = y + p[1];
                v[j * 3 + 2] = cz + lx * s + lz * c;
            }
            Quad(m, v, flags);
        }
    }

    /// <summary>
    /// A world the recycler will accept: it reads nothing but ElapsedMilliseconds off it.
    /// Without a recycler every output mesh allocates its basic arrays too, which is not what
    /// the game does and would drown the number this bench is about.
    /// </summary>
    public class FakeWorld : System.Reflection.DispatchProxy
    {
        protected override object Invoke(System.Reflection.MethodInfo target, object[] args)
            => target.ReturnType.IsValueType ? Activator.CreateInstance(target.ReturnType) : null;
    }

    public static void Run()
    {
        MeshData.Recycler ??= new MeshDataRecycler(
            System.Reflection.DispatchProxy.Create<IClientWorldAccessor, FakeWorld>());
        var rnd = new Random(2026);
        var h = new int[32, 32];
        for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++)
        {
            var v = 10 + (int)(3.0 * Math.Sin(x * 0.4) + 2.5 * Math.Cos(z * 0.3)) + rnd.Next(3) - 1;
            if (x > 12 && x < 20) v += 6;
            h[x, z] = Math.Clamp(v, 0, 27);
        }
        var tops = Empty(32 * 32, true);
        var sides = Empty(32 * 32 * 16, false);
        var foliage = Empty(32 * 32 * 2 + 6 * 400, false);
        for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++)
        {
            var hh = h[x, z];
            Face(tops, 1, hh + 1, z, x, Normal(1, true));
            if (x + 1 < 32) for (var y = h[x + 1, z] + 1; y <= hh; y++) Face(sides, 0, x + 1, y, z, Normal(0, true));
            if (x - 1 >= 0) for (var y = h[x - 1, z] + 1; y <= hh; y++) Face(sides, 0, x, y, z, Normal(0, false));
            if (z + 1 < 32) for (var y = h[x, z + 1] + 1; y <= hh; y++) Face(sides, 2, z + 1, x, y, Normal(2, true));
            if (z - 1 >= 0) for (var y = h[x, z - 1] + 1; y <= hh; y++) Face(sides, 2, z, x, y, Normal(2, false));
            if (rnd.Next(5) < 2)
            {
                var ox = x + 0.15f + (float)rnd.NextDouble() * 0.2f;
                var oz = z + 0.15f + (float)rnd.NextDouble() * 0.2f;
                float y0 = hh + 1;
                Quad(foliage, new[] { ox, y0, oz, ox + 0.7f, y0, oz + 0.7f, ox + 0.7f, y0 + 1, oz + 0.7f, ox, y0 + 1, oz }, 2 << 25);
                Quad(foliage, new[] { ox + 0.7f, y0, oz, ox, y0, oz + 0.7f, ox, y0 + 1, oz + 0.7f, ox + 0.7f, y0 + 1, oz }, 2 << 25);
            }
        }
        // four trees: a log column and a 5x5x4 crown of rotated leaves cubes, minus the corners
        var leaves = 0;
        for (var t = 0; t < 4; t++)
        {
            int tx = 4 + rnd.Next(24), tz = 4 + rnd.Next(24);
            var baseY = h[tx, tz] + 1;
            for (var y = baseY; y < baseY + 6 && y < 31; y++)
                for (var face = 0; face < 4; face++)
                {
                    var axis = face / 2; var positive = (face & 1) != 0;
                    Face(sides, axis, axis == 0 ? tx + (positive ? 1 : 0) : tz + (positive ? 1 : 0),
                        axis == 0 ? y : tx, axis == 0 ? tz : y, Normal(axis, positive));
                }
            for (var dy = 0; dy < 4; dy++) for (var dx = -2; dx <= 2; dx++) for (var dz = -2; dz <= 2; dz++)
            {
                if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                var y = baseY + 4 + dy;
                if (y >= 31 || tx + dx < 0 || tx + dx >= 32 || tz + dz < 0 || tz + dz >= 32) continue;
                if (foliage.VerticesCount + 24 > foliage.VerticesMax) break;
                RotatedCube(foliage, tx + dx, y, tz + dz, 10f * MathF.PI / 180f, 3 << 25);
                leaves++;
            }
        }

        var sources = new[]
        {
            new FarLodSource { Mesh = tops, TopSoil = true },
            new FarLodSource { Mesh = sides, TopSoil = false },
            new FarLodSource { Mesh = foliage, TopSoil = false },
        };
        var s2 = new FarLodSource[3];
        for (var i = 0; i < 3; i++) s2[i] = new FarLodSource();
        var faces = (tops.VerticesCount + sides.VerticesCount + foliage.VerticesCount) / 4;

        int out1 = 0, out2 = 0;
        void Once()
        {
            FarLod.Build(sources, 3, 1, false);
            var n2 = 0;
            out1 = 0;
            for (var i = 0; i < 3; i++)
            {
                if (sources[i].Output == null) continue;
                out1 += sources[i].Output.VerticesCount / 4;
                s2[n2].Mesh = sources[i].Output;
                s2[n2].TopSoil = sources[i].TopSoil;
                n2++;
            }
            FarLod.Build(s2, n2, 2, false);
            MeshData.Recycler?.DoRecycling();
            out2 = 0;
            for (var i = 0; i < n2; i++)
            {
                out2 += (s2[i].Output?.VerticesCount ?? 0) / 4;
                FarLod.Release(s2[i].Output);
                s2[i].Output = null;
            }
            for (var i = 0; i < 3; i++) { FarLod.Release(sources[i].Output); sources[i].Output = null; }
        }

        for (var i = 0; i < 20; i++) Once();

        // What one chunk's two builds hand the collector. The output meshes' basic arrays come
        // from the engine's recycler, but MeshData.Dispose nulls CustomInts/CustomShorts before
        // recycling, so without the size-class pool every output needs a fresh int[] (and a
        // short[] for topsoil) - which the alloc sample saw as 31 MB/s of Int32[] on the
        // tesselation thread.
        long Churn(bool pooled)
        {
            var was = FarLod.PoolArrays;
            FarLod.PoolArrays = pooled;
            try
            {
                for (var i = 0; i < 10; i++) Once();      // fill the pool at this size class
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 20; i++) Once();
                return (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
            }
            finally { FarLod.PoolArrays = was; }
        }
        var churnPooled = Churn(true);
        var churnPlain = Churn(false);

        const int iters = 200;
        FarLod.ResetStats();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iters; i++) Once();
        sw.Stop();
        string[] phases = { "classify", "flood", "cells", "sort", "choose", "alloc", "emit" };
        var split = "";
        for (var i = 0; i < phases.Length; i++) split += $"{(i == 0 ? "" : ", ")}{phases[i]} {FarLod.PhaseTicks[i] * 1000.0 / Stopwatch.Frequency / iters:F3}";
        Console.WriteLine($"far lod build, one chunk ({faces} faces: {tops.VerticesCount / 4} tops, {sides.VerticesCount / 4} sides, {foliage.VerticesCount / 4} foliage incl. {leaves} leaves cubes):");
        Console.WriteLine($"  tier 1 + tier 2: {sw.Elapsed.TotalMilliseconds / iters,7:F3} ms/chunk   ({out1} faces at tier 1 = {faces / (double)Math.Max(1, out1):F1}x fewer, {out2} at tier 2 = {faces / (double)Math.Max(1, out2):F1}x)");
        Console.WriteLine($"  per phase (ms, both tiers): {split}");
        Console.WriteLine($"  allocated per chunk: {churnPooled / 1024.0,7:F1} KB with the extras pool, {churnPlain / 1024.0:F1} KB without");
        Console.WriteLine();
    }
}
