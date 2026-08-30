using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

// Micro-experiment: which shape of the 5-plane AABB test is fastest on this CPU.
internal static class Micro
{
    public struct FP { public double Nx, Ny, Nz, D; public float Sx, Sy, Sz; }

    /// <summary>The same plane, broadcast into four lanes.</summary>
    public struct FPV { public Vector256<double> Nx, Ny, Nz, D, Sx, Sy, Sz; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Out1(ref FP p, float x, float y, float z, float ex, float ey, float ez)
        => ((double)x + ex * p.Sx) * p.Nx + ((double)y + ey * p.Sy) * p.Ny + ((double)z + ez * p.Sz) * p.Nz + p.D < 0.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Dist(ref FP p, float x, float y, float z, float ex, float ey, float ez)
        => ((double)x + ex * p.Sx) * p.Nx + ((double)y + ey * p.Sy) * p.Ny + ((double)z + ez * p.Sz) * p.Nz + p.D;

    // A: array indexing + short circuit (what FastCuller does now)
    private static int RunA(FP[] p, float[] geo, int n)
    {
        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            int g = i * 6;
            float x = geo[g], y = geo[g + 1], z = geo[g + 2], ex = geo[g + 3], ey = geo[g + 4], ez = geo[g + 5];
            if (!Out1(ref p[0], x, y, z, ex, ey, ez) && !Out1(ref p[1], x, y, z, ex, ey, ez) &&
                !Out1(ref p[2], x, y, z, ex, ey, ez) && !Out1(ref p[3], x, y, z, ex, ey, ez) &&
                !Out1(ref p[4], x, y, z, ex, ey, ez)) hits++;
        }
        return hits;
    }

    // B: ref locals + short circuit
    private static int RunB(FP[] p, float[] geo, int n)
    {
        ref FP p0 = ref p[0]; ref FP p1 = ref p[1]; ref FP p2 = ref p[2]; ref FP p3 = ref p[3]; ref FP p4 = ref p[4];
        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            int g = i * 6;
            float x = geo[g], y = geo[g + 1], z = geo[g + 2], ex = geo[g + 3], ey = geo[g + 4], ez = geo[g + 5];
            if (!Out1(ref p0, x, y, z, ex, ey, ez) && !Out1(ref p1, x, y, z, ex, ey, ez) &&
                !Out1(ref p2, x, y, z, ex, ey, ez) && !Out1(ref p3, x, y, z, ex, ey, ez) &&
                !Out1(ref p4, x, y, z, ex, ey, ez)) hits++;
        }
        return hits;
    }

    // C: ref locals + branchless combine
    private static int RunC(FP[] p, float[] geo, int n)
    {
        ref FP p0 = ref p[0]; ref FP p1 = ref p[1]; ref FP p2 = ref p[2]; ref FP p3 = ref p[3]; ref FP p4 = ref p[4];
        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            int g = i * 6;
            float x = geo[g], y = geo[g + 1], z = geo[g + 2], ex = geo[g + 3], ey = geo[g + 4], ez = geo[g + 5];
            bool vis = !(Dist(ref p0, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p1, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p2, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p3, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p4, x, y, z, ex, ey, ez) < 0.0);
            if (vis) hits++;
        }
        return hits;
    }

    // D: two cheap planes with short circuit, then the remaining three branchless
    private static int RunD(FP[] p, float[] geo, int n)
    {
        ref FP p0 = ref p[0]; ref FP p1 = ref p[1]; ref FP p2 = ref p[2]; ref FP p3 = ref p[3]; ref FP p4 = ref p[4];
        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            int g = i * 6;
            float x = geo[g], y = geo[g + 1], z = geo[g + 2], ex = geo[g + 3], ey = geo[g + 4], ez = geo[g + 5];
            if (Dist(ref p0, x, y, z, ex, ey, ez) < 0.0) continue;
            bool vis = !(Dist(ref p1, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p2, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p3, x, y, z, ex, ey, ez) < 0.0)
                     & !(Dist(ref p4, x, y, z, ex, ey, ez) < 0.0);
            if (vis) hits++;
        }
        return hits;
    }

    /// <summary>
    /// E: four parts at a time, planar layout, Vector256&lt;double&gt;.
    ///
    /// Bit-identical to C by construction: every lane performs the same operations in the same
    /// order as the scalar version, with no FMA contraction (explicit Multiply/Add intrinsics
    /// cannot be fused by the JIT), and the "inside" test is AndNot of the (d &lt; 0) mask rather
    /// than (d &gt;= 0), so a NaN counts as inside exactly like vanilla's !(dist &lt; 0).
    /// </summary>
    private static int RunE(FPV[] p, float[] planar, int n)
    {
        ref float g = ref MemoryMarshal.GetArrayDataReference(planar);
        nuint oy = (nuint)n, oz = (nuint)(2 * n), oex = (nuint)(3 * n), oey = (nuint)(4 * n), oez = (nuint)(5 * n);
        Vector256<double> zero = Vector256<double>.Zero;

        int hits = 0;
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            nuint k = (nuint)i;
            Vector256<double> x = Widen(ref g, k);
            Vector256<double> y = Widen(ref g, k + oy);
            Vector256<double> z = Widen(ref g, k + oz);
            Vector256<double> ex = Widen(ref g, k + oex);
            Vector256<double> ey = Widen(ref g, k + oey);
            Vector256<double> ez = Widen(ref g, k + oez);

            Vector256<double> mask = Vector256<double>.AllBitsSet;
            for (int pi = 0; pi < 5; pi++)
            {
                ref FPV q = ref p[pi];
                Vector256<double> ax = Avx.Add(x, Avx.Multiply(ex, q.Sx));
                Vector256<double> ay = Avx.Add(y, Avx.Multiply(ey, q.Sy));
                Vector256<double> az = Avx.Add(z, Avx.Multiply(ez, q.Sz));
                Vector256<double> d = Avx.Add(
                    Avx.Add(Avx.Add(Avx.Multiply(ax, q.Nx), Avx.Multiply(ay, q.Ny)), Avx.Multiply(az, q.Nz)),
                    q.D);
                mask = Avx.AndNot(Avx.CompareLessThan(d, zero), mask);
            }
            hits += System.Numerics.BitOperations.PopCount((uint)Avx.MoveMask(mask));
        }
        return hits;
    }

    /// <summary>Four consecutive floats widened to four doubles - CVTPS2PD, always exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Widen(ref float g, nuint at)
        => Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref g, at));

    public static void Run()
    {
        const int n = 24000;
        var rnd = new Random(4);
        var geo = new float[n * 6];
        for (int i = 0; i < n; i++)
        {
            int g = i * 6;
            geo[g] = rnd.Next(-960, 960);
            geo[g + 1] = rnd.Next(96, 192);
            geo[g + 2] = rnd.Next(-960, 960);
            geo[g + 3] = geo[g + 4] = geo[g + 5] = 16f;
        }

        var p = new FP[6];
        for (int i = 0; i < 6; i++)
        {
            double nx = rnd.NextDouble() - 0.5, ny = rnd.NextDouble() - 0.5, nz = rnd.NextDouble() - 0.5;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            p[i].Nx = nx / len; p[i].Ny = ny / len; p[i].Nz = nz / len; p[i].D = 200.0;
            p[i].Sx = p[i].Nx > 0 ? 1f : -1f;
            p[i].Sy = p[i].Ny > 0 ? 1f : -1f;
            p[i].Sz = p[i].Nz > 0 ? 1f : -1f;
        }

        // the same geometry, planar: six blocks of n floats instead of n records of six
        var planar = new float[n * 6];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 6; c++)
                planar[c * n + i] = geo[i * 6 + c];

        var pv = new FPV[6];
        for (int i = 0; i < 6; i++)
        {
            pv[i].Nx = Vector256.Create(p[i].Nx);
            pv[i].Ny = Vector256.Create(p[i].Ny);
            pv[i].Nz = Vector256.Create(p[i].Nz);
            pv[i].D = Vector256.Create(p[i].D);
            pv[i].Sx = Vector256.Create((double)p[i].Sx);
            pv[i].Sy = Vector256.Create((double)p[i].Sy);
            pv[i].Sz = Vector256.Create((double)p[i].Sz);
        }

        Console.WriteLine("plane-test variants, 24000 parts, one sweep:");
        Bench("A array + short circuit ", () => RunA(p, geo, n));
        Bench("B ref    + short circuit ", () => RunB(p, geo, n));
        Bench("C ref    + branchless    ", () => RunC(p, geo, n));
        Bench("D ref    + 1 then 4 flat ", () => RunD(p, geo, n));
        if (Avx.IsSupported)
        {
            // Equal counts are necessary but not sufficient; the real proof that the vector
            // path draws the same triangles is the 1680/1680 equivalence run in Program.cs.
            int c = RunC(p, geo, n), e = RunE(pv, planar, n);
            Bench("E planar + AVX2 x4      ", () => RunE(pv, planar, n));
            Console.WriteLine(c == e
                ? $"  E agrees with C ({c} visible)"
                : $"  E DISAGREES with C: {c} vs {e}");
        }
        else Console.WriteLine("  (no AVX2 on this CPU - vector path not measured)");
        Console.WriteLine();
    }

    private static void Bench(string name, Func<int> f)
    {
        for (int i = 0; i < 50; i++) f();
        const int iters = 500;
        var sw = Stopwatch.StartNew();
        int acc = 0;
        for (int i = 0; i < iters; i++) acc += f();
        sw.Stop();
        Console.WriteLine($"  {name} {sw.Elapsed.TotalMilliseconds / iters,7:F3} ms   (visible {acc / iters})");
    }
}
