using System;
using System.Collections.Generic;
using System.Globalization;

namespace Komet.Measure;

/// <summary>
/// Text the player reads, in the player's language.
///
/// LOGS NEVER GO THROUGH HERE. A log line, a report and a hitch line are diagnostic artefacts:
/// they get pasted into bug reports, compared against an earlier session and read by people who
/// do not run the client that produced them. Those stay English, always, whatever the game is
/// set to. What passes through this class is the HUD and the chat replies of the .komet command
/// - the two surfaces that are only ever read by the player sitting in front of them.
///
/// Every call carries its English text as the argument, not just a key. Three situations need
/// it and all three are normal: the verify harness runs without a game and therefore without a
/// loaded language file; KometBaseline shows the same HUD but ships no assets of its own; and a
/// translation can simply be missing from a language the mod does not know yet. In each case
/// the English text in the source is what appears - never a raw key, never an empty line.
/// </summary>
public static class Loc
{
    /// <summary>Keys this process asked for, with the English text of each - the verify
    /// harness reads it to prove that every key the code uses exists in every language file
    /// and that no file carries a key the code never asks for.</summary>
    public static readonly Dictionary<string, string> Used = new(StringComparer.Ordinal);

    /// <summary>Set false once the engine's Lang is unusable (no game, no assets), so the
    /// fallback path costs nothing but a bool after the first miss.</summary>
    private static bool langUsable = true;

    /// <summary>The key prefix; the mod id the language files live under.</summary>
    public const string Domain = "komet:";

    /// <summary>
    /// The translation for <paramref name="key"/>, or <paramref name="english"/> when there is
    /// none. The key is recorded either way, so a translation added later is never missed and
    /// verify can see the full set.
    /// </summary>
    public static string T(string key, string english)
    {
        Used[key] = english;
        if (!langUsable) return english;
        try
        {
            return Vintagestory.API.Config.Lang.GetIfExists(key) ?? english;
        }
        catch (Exception)
        {
            // no game around this code (verify, bench): stop trying, keep the English text
            langUsable = false;
            return english;
        }
    }

    /// <summary>
    /// The formatted translation. Numbers follow the client's culture, exactly like the rest of
    /// the HUD - a German client reads "3,5 ms" in a German sentence.
    /// </summary>
    public static string T(string key, string english, params object[] args)
    {
        var template = T(key, english);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // a translation with a broken placeholder must not take the HUD down with it
            return string.Format(CultureInfo.CurrentCulture, english, args);
        }
    }

    /// <summary>
    /// A HUD label: the English label doubles as the key ("cpu cores" -&gt;
    /// "komet:hud-cpu-cores"), so a row cannot drift apart from its translation entry.
    /// </summary>
    public static string Hud(string englishLabel)
        => T(Domain + "hud-" + englishLabel.Replace(' ', '-'), englishLabel);

    /// <summary>verify: forget what was collected, and try the engine again.</summary>
    internal static void Reset()
    {
        Used.Clear();
        langUsable = true;
    }
}
