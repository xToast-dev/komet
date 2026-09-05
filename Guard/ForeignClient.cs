using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Komet.Measure;
using Vintagestory.API.Common;

namespace Komet.Guard;

/// <summary>
/// Recognises the two performance projects komet is known to be incompatible with, so the
/// player hears it at world join instead of finding out from a bisect.
///
/// Optimum is a forked client: it ships a modified VintagestoryLib and is not a mod, so no
/// mod list shows it. Its assembly carries a marker type (Optimum.OptimumInfo, the version a
/// constant on it) and it appends itself to the game version string ("v1.22.7 (Stable) +
/// Optimum v0.3.14"); the type is checked first, the string is the fallback for a build that
/// renames the type. OptiTime is a Harmony mod (modid "optitime") and is looked up in the
/// mod loader like any other.
///
/// Both replace the same engine code komet's transcriptions replace (task drain, culling,
/// entity loop, ...), each side unaware of the other: komet's prefix returns false and the
/// other side's change is gone, or the other side's IL rewrite runs under komet's
/// measurement and the numbers describe nothing. Komet stays enabled - which side should
/// win is the player's call - but says so at every world join: a log line, a chat line and
/// a dialog (<see cref="ForeignClientDialog"/>), and the report names the client.
/// </summary>
public static class ForeignClient
{
    public sealed class Finding
    {
        public string Name;
        public string Version;
        /// <summary>How it was recognised - the report says it, so a false positive can be traced.</summary>
        public string How;

        public override string ToString() => string.IsNullOrEmpty(Version) ? Name : Name + " v" + Version;
    }

    /// <summary>The marker type the Optimum fork compiles into VintagestoryLib.</summary>
    public const string OptimumMarkerType = "Optimum.OptimumInfo";
    public const string OptiTimeModId = "optitime";

    private static readonly Regex OptimumTag =
        new(@"Optimum\s*v?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What the last <see cref="Scan"/> found, in a fixed order: Optimum, then OptiTime.</summary>
    public static readonly List<Finding> Findings = new();

    /// <summary>
    /// Runs every detector. <paramref name="findType"/> resolves a type name in the engine
    /// assembly, <paramref name="longGameVersion"/> is GameVersion.LongGameVersion, and
    /// <paramref name="modInfo"/> answers a mod id with its info or null - all three injected
    /// so verify can drive the detectors without a game. A lookup that throws counts as
    /// "not found": this runs at client start and must never take the mod down.
    /// </summary>
    public static void Scan(System.Func<string, Type> findType, string longGameVersion, System.Func<string, ModInfo> modInfo)
    {
        Findings.Clear();
        var optimum = DetectOptimum(findType, longGameVersion);
        if (optimum != null) Findings.Add(optimum);
        var optiTime = DetectOptiTime(modInfo);
        if (optiTime != null) Findings.Add(optiTime);
    }

    internal static Finding DetectOptimum(System.Func<string, Type> findType, string longGameVersion)
    {
        Type marker = null;
        try { marker = findType?.Invoke(OptimumMarkerType); }
        catch (Exception) { /* the string below still decides */ }
        if (marker != null)
        {
            string version = null;
            try { version = marker.GetField("Version")?.GetRawConstantValue() as string; }
            catch (Exception) { /* a marker without the constant: the version comes from the tag, or stays empty */ }
            return new Finding
            {
                Name = "Optimum",
                Version = version ?? VersionFromTag(longGameVersion),
                How = "marker type " + OptimumMarkerType,
            };
        }
        if (longGameVersion != null && longGameVersion.Contains("Optimum", StringComparison.OrdinalIgnoreCase))
            return new Finding { Name = "Optimum", Version = VersionFromTag(longGameVersion), How = "game version string" };
        return null;
    }

    private static string VersionFromTag(string longGameVersion)
    {
        if (longGameVersion == null) return null;
        var m = OptimumTag.Match(longGameVersion);
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static Finding DetectOptiTime(System.Func<string, ModInfo> modInfo)
    {
        ModInfo info = null;
        try { info = modInfo?.Invoke(OptiTimeModId); }
        catch (Exception) { /* not loaded, or a loader that objects: either way not running */ }
        if (info == null) return null;
        return new Finding
        {
            Name = string.IsNullOrEmpty(info.Name) ? "OptiTime" : info.Name,
            Version = info.Version,
            How = "mod id " + OptiTimeModId,
        };
    }

    /// <summary>"Optimum v0.3.14, OptiTime v1.5.16" - the list every message carries.</summary>
    public static string Describe()
    {
        var parts = new string[Findings.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = Findings[i].ToString();
        return string.Join(", ", parts);
    }

    // The player-facing texts, in the player's language (English in the source, see Loc).
    // The log line is not here: logs never go through Loc.

    public static string Title() => Loc.T("komet:foreign-title", "Komet: incompatible client");

    public static string Button() => Loc.T("komet:foreign-ok", "Understood");

    // One literal per text, not a concatenation: verify reads the English from the source
    // with a regex and would otherwise see only the first piece.
    public static string DialogText(string what)
        => Loc.T("komet:foreign-text", "Hey - this is incompatible: {0} detected.\n\nKomet and this one replace the same engine code, each unaware of the other. Komet's rewrites silently undo what the other side changed, and Komet's numbers no longer describe anything. Run one or the other, not both.\n\nKomet stays active for this session and warns again at every world join.", what);

    public static string ChatText(string what)
        => Loc.T("komet:foreign-chat", "[Komet] Hey - this is incompatible: {0} detected. Komet and this one replace the same engine code; run one or the other, not both.", what);
}
