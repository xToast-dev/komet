using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Runtime;

/// <summary>
/// Swaps the chunk opaque program's fragment shader for a trivial one, live and reversibly.
///
/// The bottom-of-pipe probe prices the camera opaque pass (5,2 ms for six million triangles,
/// 23 million fragments on 3,7 million pixels) but cannot say whether that is the FRAGMENT
/// SHADER - fog, two shadow cascades' PCF, sky colour, effects - or the FRONT END rasterising
/// six million mostly sub-pixel triangles. The two want opposite work: the first is shader
/// optimisation, the second is triangle count. This decides it in one toggle: the same vertex
/// shader, the same alpha test (so the same fragments survive and the depth buffer is the
/// same), and nothing else - one texture fetch, the colour written as is. The pass row in the
/// report, read with the swap on, is the answer.
///
/// It is done on the engine's own program object, not a copy: ShaderProgram.Compile() compiles
/// the Shader objects' Code, links a NEW GL program and rebuilds the uniform and texture
/// location tables, so the engine's typed uniform setters keep working - provided every
/// uniform the original declared is still declared, or a setter would throw on a missing
/// name. So the swap keeps everything above <c>main()</c> and replaces only the body. The
/// engine's own shader reload does exactly this to every program, which is what makes it
/// safe; the ids it orphans are deleted here rather than leaked.
///
/// A diagnostic: it changes the picture completely (no fog, no shadows on chunks), safemode
/// and leaving the world put the original back, and a shader reload from elsewhere (a
/// graphics setting) drops it on its own - detected by the program instance changing.
/// </summary>
public static class ChunkShaderSwap
{
    /// <summary>The program the swap is on, or null. Compared by reference against
    /// ShaderPrograms.Chunkopaque so an engine reload is noticed.</summary>
    private static ShaderProgramChunkopaque swapped;
    private static string originalCode;

    public static string LastError { get; private set; }

    /// <summary>Whether the swap is in effect right now.</summary>
    public static bool Active => swapped != null && ReferenceEquals(swapped, ShaderPrograms.Chunkopaque);

    /// <summary>
    /// The flat source, pure: everything of the original above its <c>main</c> - the version
    /// line, the prefix defines' slot, every uniform, varying and output, the includes - and a
    /// body that keeps only the alpha test. Null when the original has no <c>main</c>.
    /// </summary>
    internal static string FlatSource(string original)
    {
        if (string.IsNullOrEmpty(original)) return null;
        var at = original.LastIndexOf("void main", StringComparison.Ordinal);
        if (at < 0) return null;
        return original.Substring(0, at) + FlatMain;
    }

    /// <summary>The engine's alpha test, term for term, on the raw texel - so the same
    /// fragments are discarded and the depth buffer comes out the same.</summary>
    internal const string FlatMain =
        "void main()\n" +
        "{\n" +
        "\tvec4 texColor = texture(terrainTex, uv) * rgba;\n" +
        "#if NORMALVIEW == 0\n" +
        "\tfloat aTest = texColor.a + max(0.0, 1.0 - rgba.a) * min(1.0, texColor.a * 10.0) - lod0Fade;\n" +
        "\tif (aTest < alphaTest || rgba.a < 0.005) discard;\n" +
        "#endif\n" +
        "\toutColor = texColor;\n" +
        "\toutGlow = vec4(0.0, 0.0, 0.0, min(1.0, fogAmount + texColor.a));\n" +
        "#if SSAOLEVEL > 0\n" +
        "\toutGPosition = vec4(camPos.xyz, fogAmount * 2.0 + glowLevel);\n" +
        "\toutGNormal = gnormal;\n" +
        "#endif\n" +
        "}\n";

    /// <summary>Puts the flat fragment shader on. Render thread only (a GL context is needed).</summary>
    public static bool Enable()
    {
        LastError = null;
        if (Active) return true;
        var program = ShaderPrograms.Chunkopaque;
        if (program == null || program.FragmentShader == null || program.VertexShader == null || program.ProgramId == 0)
        {
            LastError = "chunkopaque program not loaded";
            return false;
        }

        var original = program.FragmentShader.Code;
        var flat = FlatSource(original);
        if (flat == null)
        {
            LastError = "no main() in the chunkopaque fragment shader";
            return false;
        }

        if (!Recompile(program, flat, out var error))
        {
            // the original program is intact: Compile() stops before linking, and the old
            // program keeps its old shaders attached
            program.FragmentShader.Code = original;
            LastError = error;
            return false;
        }
        swapped = program;
        originalCode = original;
        return true;
    }

    /// <summary>Puts the engine's fragment shader back. A no-op when nothing is swapped, or
    /// when the engine reloaded its shaders in the meantime.</summary>
    public static bool Restore()
    {
        LastError = null;
        var program = swapped;
        swapped = null;
        if (program == null) return true;
        if (!ReferenceEquals(program, ShaderPrograms.Chunkopaque) || program.Disposed)
        {
            originalCode = null;
            return true; // the engine replaced it; the new instance carries its own source
        }
        var ok = Recompile(program, originalCode, out var error);
        if (!ok) LastError = error;
        originalCode = null;
        return ok;
    }

    /// <summary>Recompiles a program in place with a new fragment source, deleting the GL objects the
    /// engine's Compile() orphans. Shared with the far-mesh shader variant.</summary>
    internal static bool Recompile(ShaderProgramBase program, string fragmentCode, out string error)
    {
        error = null;
        var oldProgram = program.ProgramId;
        var oldVs = program.VertexShader.ShaderId;
        var oldFs = program.FragmentShader.ShaderId;
        var oldGs = program.GeometryShader?.ShaderId ?? 0;

        program.FragmentShader.Code = fragmentCode;
        bool ok;
        try { ok = program.Compile(); }
        catch (Exception e)
        {
            error = "compile threw " + e.GetType().Name + ": " + e.Message;
            return false;
        }
        if (!ok)
        {
            error = "compile or link failed (see client-main.log)";
            return false;
        }

        // Compile() made new shader and program objects; the engine's Dispose would have
        // deleted the old ones, so this does.
        try
        {
            if (oldProgram != 0 && oldProgram != program.ProgramId)
            {
                if (oldVs != 0) { GL.DetachShader(oldProgram, oldVs); GL.DeleteShader(oldVs); }
                if (oldFs != 0) { GL.DetachShader(oldProgram, oldFs); GL.DeleteShader(oldFs); }
                if (oldGs != 0) { GL.DetachShader(oldProgram, oldGs); GL.DeleteShader(oldGs); }
                GL.DeleteProgram(oldProgram);
            }
        }
        catch (Exception) { /* a leaked id is not worth a failure */ }
        return true;
    }
}
