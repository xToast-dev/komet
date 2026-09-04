using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Chunk mesh uploads copy element by element into persistently mapped GPU memory.
///
/// ClientPlatformWindows allocates chunk VBOs with GL_MAP_PERSISTENT_BIT |
/// GL_MAP_COHERENT_BIT and then writes them like this:
///
///     float* ptr = (float*)vboPtr; ptr += offset / 4;
///     for (int i = 0; i &lt; count; i++) *(ptr++) = data[i];
///
/// A scalar store loop is the worst possible way to fill that buffer. The memory is
/// write-combined, so 4-byte stores keep flushing partially filled write-combine buffers
/// instead of handing the bus full cache lines, and the loop cannot be vectorised because of
/// the array bounds check. A bulk copy issues wide stores and fills WC buffers completely.
///
/// This matters specifically at high view distance: ChunkTesselatorManager sizes its
/// per-frame upload budget from frustumCuller.ViewDistanceSq, so the number of vertices
/// pushed through these loops each frame grows with the square of the view distance.
///
/// The replacement is a plain memory copy of the exact same bytes - identical semantics,
/// including the GL_ARRAY_BUFFER bind vanilla does first. Only the persistent path is
/// touched; when the driver has no ARB_buffer_storage the original glBufferSubData path runs
/// unchanged.
/// </summary>
public static class MeshUploadPatches
{
    /// <summary>Bytes moved through the bulk path, and calls that fell through to vanilla.</summary>
    public static long StatBytesCopied;
    public static long StatBulkCalls;
    public static long StatFallbackCalls;

    private const int GlArrayBuffer = 34962;
    private const int GlElementArrayBuffer = 34963;

    public static void Apply(Harmony harmony)
    {
        var platform = typeof(ClientPlatformWindows);
        var patched = 0;

        patched += PatchOne(harmony, platform, "updateVAO", typeof(float[]), nameof(FloatPrefix));
        patched += PatchOne(harmony, platform, "updateVAO", typeof(int[]), nameof(IntPrefix));
        patched += PatchOne(harmony, platform, "updateVAO", typeof(short[]), nameof(ShortPrefix));
        patched += PatchOne(harmony, platform, "updateVAO", typeof(ushort[]), nameof(UShortPrefix));
        patched += PatchOne(harmony, platform, "updateVAO", typeof(byte[]), nameof(BytePrefix));

        var indices = AccessTools.Method(platform, "updateIndices",
            new[] { typeof(int[]), typeof(int), typeof(int), typeof(VAO), typeof(bool) });
        if (indices != null)
        {
            harmony.Patch(indices, prefix: new HarmonyMethod(AccessTools.Method(typeof(MeshUploadPatches), nameof(IndicesPrefix))));
            patched++;
        }

        if (patched < 6) throw new InvalidOperationException($"only {patched}/6 mesh upload helpers found");
    }

    private static int PatchOne(Harmony harmony, Type platform, string name, Type arrayType, string prefix)
    {
        var target = AccessTools.Method(platform, name,
            new[] { arrayType, typeof(int), typeof(int), typeof(int), typeof(nint), typeof(bool) });
        if (target == null) return 0;
        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(MeshUploadPatches), prefix)));
        return 1;
    }

    /// <summary>
    /// Shared body. Returns false when the copy was done here, true to let vanilla run.
    /// The offset vanilla applies is a byte offset that it scales through a T* pointer, so the
    /// destination has to be derived the same way.
    /// </summary>
    private static unsafe bool BulkCopy<T>(T[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        where T : unmanaged
    {
        if (!pers || vboPtr == 0)
        {
            StatFallbackCalls++;
            return true; // non-persistent buffers go through glBufferSubData as before
        }

        GL.BindBuffer((BufferTarget)GlArrayBuffer, vboId);

        if (count <= 0) return false; // vanilla binds and writes nothing

        if (data == null || count > data.Length)
        {
            // let vanilla reproduce its own IndexOutOfRangeException rather than overreading
            StatFallbackCalls++;
            return true;
        }

        var bytes = (long)count * sizeof(T);
        ref var src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(data));
        ref var dst = ref Unsafe.AsRef<byte>((byte*)vboPtr + (nint)(offset / sizeof(T)) * sizeof(T));
        Unsafe.CopyBlockUnaligned(ref dst, ref src, (uint)bytes);

        StatBulkCalls++;
        StatBytesCopied += bytes;
        return false;
    }

    public static bool FloatPrefix(float[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        => BulkCopy(data, offset, count, vboId, vboPtr, pers);

    public static bool IntPrefix(int[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        => BulkCopy(data, offset, count, vboId, vboPtr, pers);

    public static bool ShortPrefix(short[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        => BulkCopy(data, offset, count, vboId, vboPtr, pers);

    public static bool UShortPrefix(ushort[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        => BulkCopy(data, offset, count, vboId, vboPtr, pers);

    public static bool BytePrefix(byte[] data, int offset, int count, int vboId, nint vboPtr, bool pers)
        => BulkCopy(data, offset, count, vboId, vboPtr, pers);

    /// <summary>Same treatment for the index buffer, which uses a different bind target.</summary>
    public static unsafe bool IndicesPrefix(int[] Indices, int IndicesOffset, int IndicesCount, VAO vao, bool pers)
    {
        if (Indices == null) return false; // vanilla returns immediately
        if (!pers || vao.indicesPtr == 0)
        {
            StatFallbackCalls++;
            return true;
        }
        if (IndicesCount > Indices.Length)
        {
            StatFallbackCalls++;
            return true;
        }

        GL.BindBuffer((BufferTarget)GlElementArrayBuffer, vao.vboIdIndex);

        if (IndicesCount > 0)
        {
            var bytes = (long)IndicesCount * sizeof(int);
            var dst = (byte*)vao.indicesPtr + (nint)(IndicesOffset / sizeof(int)) * sizeof(int);
            ref var from = ref Unsafe.As<int, byte>(ref MemoryMarshal.GetArrayDataReference(Indices));
            Unsafe.CopyBlockUnaligned(ref Unsafe.AsRef<byte>(dst), ref from, (uint)bytes);
            StatBulkCalls++;
            StatBytesCopied += bytes;
        }

        GL.BindBuffer((BufferTarget)GlElementArrayBuffer, 0);
        vao.IndicesCount = IndicesCount;
        return false;
    }
}
