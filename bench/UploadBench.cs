using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// The scalar store loop ClientPlatformWindows uses to fill persistently mapped chunk VBOs,
/// against a bulk copy of the same bytes.
///
/// This runs against ordinary heap memory, which is the *conservative* case: the real
/// destination is GL_MAP_PERSISTENT | GL_MAP_COHERENT storage, i.e. uncached write-combined
/// memory, where 4-byte scalar stores keep flushing partially filled write-combine buffers
/// and the gap widens further.
/// </summary>
internal static class UploadBench
{
    private static unsafe void ScalarFloat(float[] data, int offset, int count, byte* vbo)
    {
        float* ptr = (float*)vbo;
        ptr += offset / 4;
        for (int i = 0; i < count; i++) *(ptr++) = data[i];
    }

    private static unsafe void BulkFloat(float[] data, int offset, int count, byte* vbo)
    {
        long bytes = (long)count * 4;
        ref byte src = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetArrayDataReference(data));
        Unsafe.CopyBlockUnaligned(ref Unsafe.AsRef<byte>(vbo + (nint)(offset / 4) * 4), ref src, (uint)bytes);
    }

    public static unsafe void Run()
    {
        Console.WriteLine("chunk mesh upload: scalar store loop vs bulk copy (ordinary RAM - the conservative case)");
        Console.WriteLine($"{"vertices/frame",16}{"bytes",12}{"scalar",12}{"bulk",12}{"speedup",10}");

        // one frame's worth of chunk vertex data: xyz(12B) + uv(8B) + rgba(4B) + flags(4B) + custom(4B)
        const int bytesPerVertex = 32;

        foreach (int verts in new[] { 150_000, 600_000, 2_500_000 })
        {
            int floats = verts * bytesPerVertex / 4;
            var data = new float[floats];
            for (int i = 0; i < floats; i++) data[i] = i * 0.5f;

            nint raw = Marshal.AllocHGlobal(floats * 4 + 64);
            var vbo = (byte*)raw;

            // upload happens in per-chunk-part chunks, not one giant block
            const int partFloats = 24_000;

            double scalar = Time(() =>
            {
                for (int off = 0; off + partFloats <= floats; off += partFloats)
                    ScalarFloat(data, off * 4, partFloats, vbo);
            });
            double bulk = Time(() =>
            {
                for (int off = 0; off + partFloats <= floats; off += partFloats)
                    BulkFloat(data, off * 4, partFloats, vbo);
            });

            Marshal.FreeHGlobal(raw);
            Console.WriteLine($"{verts,16:N0}{(long)verts * bytesPerVertex / 1024 / 1024 + " MB",12}{scalar,10:F2}ms{bulk,10:F2}ms{scalar / bulk,9:F1}x");
        }
        Console.WriteLine();
    }

    private static double Time(Action a)
    {
        for (int i = 0; i < 3; i++) a();
        const int iters = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }
}
