using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

/// <summary>
/// How many separate index ranges does one frame hand to glMultiDrawElements, and how many
/// of them are actually adjacent in the index buffer?
///
/// The engine pools chunk meshes in tesselation order, and ChunkTesselatorManager sorts that
/// queue by a distance-derived priority before uploading. So a pool's location list is
/// roughly distance-sorted, and the slice of it that survives frustum culling tends to be
/// long contiguous runs - each of which glMultiDrawElements is currently told to draw as a
/// separate range.
/// </summary>
internal static class DrawRanges
{
    private const int ChunkSize = 32;

    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> LocationsRef =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");

    private sealed class Part
    {
        public int Cx, Cy, Cz, Lod, Verts, Indices;
        public double Dist;
    }

    /// <summary>
    /// Models one render pass at a given view distance: every chunk column in range gets a few
    /// vertical chunks, each of which contributes LOD variants, and the whole set is pooled in
    /// the order the tesselator would have produced it.
    /// </summary>
    private static List<MeshDataPool> BuildRealisticPools(int viewDistanceBlocks, Random rnd, double shuffleFraction)
    {
        int radius = viewDistanceBlocks / ChunkSize;
        var parts = new List<Part>();

        for (int cx = -radius; cx <= radius; cx++)
        for (int cz = -radius; cz <= radius; cz++)
        {
            double dist = Math.Sqrt((double)cx * cx + (double)cz * cz) * ChunkSize;
            if (dist > viewDistanceBlocks) continue;

            // near chunks carry full detail, far chunks only the coarse LOD
            int[] lods = dist < 200 ? new[] { 0, 1 } : dist < 480 ? new[] { 1, 2 } : new[] { 3 };
            for (int cy = 3; cy <= 5; cy++)
            foreach (int lod in lods)
            {
                if (rnd.NextDouble() < 0.35) continue; // not every subchunk has geometry in this pass
                int verts = 400 + rnd.Next(4000);
                parts.Add(new Part { Cx = cx, Cy = cy, Cz = cz, Lod = lod, Verts = verts, Indices = verts / 4 * 6, Dist = dist });
            }
        }

        // tesselation order: nearest first (ChunkTesselatorManager sorts by RecalcPriority)
        parts.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        // a long session fragments that order as chunks are removed and squeezed back in
        if (shuffleFraction > 0)
        {
            int swaps = (int)(parts.Count * shuffleFraction);
            for (int i = 0; i < swaps; i++)
            {
                int a = rnd.Next(parts.Count), b = rnd.Next(parts.Count);
                (parts[a], parts[b]) = (parts[b], parts[a]);
            }
        }

        const int poolVerts = 500000;
        const int poolIndices = 750000;
        const int maxParts = 3000; // ClientSettings.ModelDataPoolMaxParts * 2

        var pools = new List<MeshDataPool>();
        MeshDataPool cur = null;
        List<ModelDataPoolLocation> curLocs = null;
        int vpos = 0, ipos = 0;

        foreach (Part p in parts)
        {
            if (cur == null || vpos + p.Verts > poolVerts || ipos + p.Indices > poolIndices || curLocs.Count >= maxParts)
            {
                cur = NewPool(maxParts);
                curLocs = LocationsRef(cur);
                pools.Add(cur);
                vpos = 0;
                ipos = 0;
            }

            curLocs.Add(new ModelDataPoolLocation
            {
                IndicesStart = ipos,
                IndicesEnd = ipos + p.Indices,
                VerticesStart = vpos,
                VerticesEnd = vpos + p.Verts,
                LodLevel = p.Lod,
                FrustumCullSphere = Sphere.BoundingSphereForCube(p.Cx * ChunkSize, p.Cy * ChunkSize, p.Cz * ChunkSize, ChunkSize)
            });
            vpos += p.Verts;
            ipos += p.Indices;
        }
        return pools;
    }

    private static MeshDataPool NewPool(int maxParts)
    {
        ConstructorInfo ctor = typeof(MeshDataPool).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(int), typeof(int) }, null);
        var pool = (MeshDataPool)ctor.Invoke(new object[] { 500000, 750000, maxParts });
        pool.indicesStartsByte = new int[maxParts * 2];
        pool.indicesSizes = new int[maxParts];
        return pool;
    }

    private static FrustumCulling BuildCuller(int viewDistance, double yaw)
    {
        var culler = new FrustumCulling();
        culler.UpdateViewDistance(viewDistance);
        float lod0 = Math.Min(640, viewDistance) * 0.33f;
        float lod2 = Math.Min(640, viewDistance) * 0.75f;
        culler.lod0BiasSq = lod0 * lod0;
        culler.lod2BiasSq = lod2 * lod2;
        culler.shadowRangeX = culler.shadowRangeZ = 220.0;

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70.0 * Math.PI / 180.0, 2560.0 / 1440.0, 0.3, viewDistance * 1.2);
        double[] view = Mat4d.Create();
        Mat4d.LookAt(view,
            new double[] { 0, 140, 0 },
            new double[] { Math.Sin(yaw) * 100.0, 130.0, Math.Cos(yaw) * 100.0 },
            new double[] { 0, 1, 0 });
        Mat4d.Multiply(Mat4d.Create(), proj, view);
        culler.CalcFrustumEquations(new BlockPos(0, 140, 0, 0), proj, view);
        return culler;
    }

    /// <summary>Counts ranges as vanilla emits them, and how few they would be if merged.</summary>
    private static (int parts, int ranges, int merged) Measure(List<MeshDataPool> pools, FrustumCulling culler)
    {
        int totalParts = 0, ranges = 0, merged = 0;
        foreach (MeshDataPool pool in pools)
        {
            totalParts += LocationsRef(pool).Count;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            int g = pool.indicesGroupsCount;
            ranges += g;

            int prevEndByte = -1;
            for (int i = 0; i < g; i++)
            {
                int startByte = pool.indicesStartsByte[i * 2];
                int sizeBytes = pool.indicesSizes[i] * 4;
                if (startByte != prevEndByte) merged++;
                prevEndByte = startByte + sizeBytes;
            }
        }
        return (totalParts, ranges, merged);
    }

    public static void Run()
    {
        Console.WriteLine("draw ranges handed to glMultiDrawElements, one render pass, one frame");
        Console.WriteLine($"{"view dist",10}{"parts pooled",14}{"visible ranges",16}{"after merging",15}{"reduction",11}");

        foreach (int vd in new[] { 256, 512, 1024, 1536 })
        {
            foreach (double shuffle in new[] { 0.0, 0.5 })
            {
                var rnd = new Random(7);
                List<MeshDataPool> pools = BuildRealisticPools(vd, rnd, shuffle);

                long parts = 0, ranges = 0, merged = 0;
                const int views = 8;
                for (int v = 0; v < views; v++)
                {
                    var (p, r, m) = Measure(pools, BuildCuller(vd, v * Math.PI / 4.0));
                    parts += p; ranges += r; merged += m;
                }

                string label = shuffle == 0 ? $"{vd}" : $"{vd} frag";
                Console.WriteLine($"{label,10}{parts / views,14:N0}{ranges / views,16:N0}{merged / views,15:N0}{(double)ranges / Math.Max(1, merged),10:F1}x");
            }
        }
        Console.WriteLine();
    }
}
