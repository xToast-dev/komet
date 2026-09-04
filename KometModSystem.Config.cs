using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Komet;

/// <summary>Loading, versioning and backing up komet.json.</summary>
public partial class KometModSystem
{
    private const string ConfigFile = "komet.json";

    /// <summary>
    /// Whether a config file written by <paramref name="stored"/> should be thrown away and
    /// regenerated for <paramref name="running"/>. Pure, so the rule can be checked directly:
    /// a file with no version at all predates the check and counts as stale.
    /// </summary>
    internal static bool ShouldRegenerate(string stored, string running)
        => string.IsNullOrEmpty(stored) || stored != running;

    /// <summary>
    /// The suffix a backup is filed under. The stored version comes out of a file a user can
    /// edit, and it ends up in a path, so it is reduced to harmless characters here rather
    /// than trusted - a version of "../../x" must not decide where the copy lands.
    /// </summary>
    internal static string BackupTag(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "alt";
        var sb = new System.Text.StringBuilder(16);
        foreach (var c in stored)
        {
            if (sb.Length == 16) break;
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
        }
        return sb.Length == 0 ? "alt" : sb.ToString();
    }

    private KometConfig LoadConfig(ICoreAPI api)
    {
        // The config layout version, NOT the mod version: a release that changes no setting
        // must leave everybody's file alone. KometConfig.Current is bumped by hand when a
        // setting is added, removed or gets a new default.
        var running = KometConfig.Current;
        KometConfig cfg = null;
        try
        {
            cfg = api.LoadModConfig<KometConfig>(ConfigFile);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not read {0}, falling back to defaults", ConfigFile);
            Mod.Logger.Error(e);
        }

        // Changing a default in the source reaches nobody who already has the file, because
        // this is where it would have to take effect and the stored value simply wins. So a
        // config version bump regenerates the file - after copying the old one next to it,
        // since "regenerate" must never mean "silently discard what you had configured".
        if (cfg != null && ShouldRegenerate(cfg.ConfigVersion, running))
        {
            var tag = BackupTag(cfg.ConfigVersion);
            BackupConfig(tag);
            Mod.Logger.Notification(
                "config layout {0} found, this mod writes {1} - regenerated from current "
                + "defaults, your previous file is next to it as {2}.{3}.bak",
                string.IsNullOrEmpty(cfg.ConfigVersion) ? "(none)" : cfg.ConfigVersion,
                running, ConfigFile, tag);
            cfg = null;
        }

        cfg ??= new KometConfig();
        cfg.ConfigVersion = running;

        try { api.StoreModConfig(cfg, ConfigFile); } catch { /* read only config dir, not fatal */ }
        return cfg;
    }

    /// <summary>Copies the existing config next to itself, tagged with the layout that wrote it.</summary>
    private void BackupConfig(string tag)
    {
        try
        {
            var path = System.IO.Path.Combine(GamePaths.ModConfig, ConfigFile);
            if (System.IO.File.Exists(path))
                System.IO.File.Copy(path, path + "." + tag + ".bak", overwrite: true);
        }
        catch (Exception e)
        {
            // Losing the backup is not a reason to keep a stale config, but it is worth saying.
            Mod.Logger.Warning("could not back up {0} ({1}), regenerating anyway", ConfigFile, e.GetType().Name);
        }
    }
}
