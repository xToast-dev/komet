using System.Reflection;

namespace Komet.Measure;

/// <summary>
/// What this build calls itself: the mod version plus the build stamp.
///
/// The version alone cannot identify a testbuild - a dozen of them share "1.0.0", and the
/// question a field log has to answer is "which DLL was that". So every build stamps the
/// minute it was compiled (yyMMdd.HHmm) into the assembly's InformationalVersion
/// ("1.0.0+260830.1917"), and version and stamp are shown together everywhere the mod names
/// itself: HUD title, report header, chat line.
///
/// The stamp rides in the assembly attribute instead of a generated source file or a counter
/// checked into the project, because both of those can drift from the DLL that was actually
/// shipped. This one is written by the compiler that produced the binary and cannot.
/// </summary>
public static class KometVersion
{
    /// <summary>The build stamp of this assembly, e.g. "260830.1917", or null when unstamped.</summary>
    public static readonly string Build = StampFrom(
        typeof(KometVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    /// <summary>"1.0.0 (b260830.1917)", or plain "1.0.0" when there is no stamp to show.</summary>
    public static string Display(string modVersion) => Compose(modVersion, Build);

    /// <summary>Pure so the shape of the line is checkable without building an assembly.</summary>
    internal static string Compose(string modVersion, string build)
        => string.IsNullOrEmpty(build) ? modVersion : modVersion + " (b" + build + ")";

    /// <summary>
    /// The part behind the '+' of an InformationalVersion. Everything that compiles these
    /// sources without the stamping target - verify, bench - lands here with no '+' and gets
    /// null, which is a version line without a build number rather than a crash.
    /// </summary>
    internal static string StampFrom(string informational)
    {
        if (string.IsNullOrEmpty(informational)) return null;
        int plus = informational.IndexOf('+');
        if (plus < 0 || plus + 1 >= informational.Length) return null;
        return informational.Substring(plus + 1);
    }
}
