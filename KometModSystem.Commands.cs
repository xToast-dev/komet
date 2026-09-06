using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Komet.Culling;
using Komet.Guard;
using Komet.Measure;
using Komet.Patches;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Komet;

/// <summary>
/// The .komet chat command and the runtime toggles behind it. These exist because every
/// visual complaint used to cost a restart per guess; now "is it the mod?" (safemode) and
/// "which part?" (toggle) are answerable while the artefact is on screen.
/// </summary>
public partial class KometModSystem
{
    private bool safeMode;
    private int savedSunInterval = 1;
    private bool savedGlErrorSkip;
    private bool savedFirepitGate = true;
    private bool savedEntityTess = true;
    private bool savedEdgeCoalesce = true;
    private bool savedEdgePriority = true;
    private readonly Stopwatch uptime = Stopwatch.StartNew();

    /// <summary>What the mod ships with, so the stress phase can name the other side.</summary>
    private const int DefaultCellTarget = 32;

    /// <summary>
    /// Changes the grid's cell size and drops every cached grid, because the existing ones were
    /// built at the old size. Without the invalidation the setting would only take effect on
    /// pools that happened to change, and a stress phase would measure a mixture of both.
    /// </summary>
    private static void SetCellTarget(int target)
    {
        if (FastCuller.PartsPerCellTarget == target) return;
        FastCuller.PartsPerCellTarget = target;
        FastCuller.InvalidateAll();
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        var root = api.ChatCommands.Create("komet")
            .WithDescription(Loc.T("komet:cmd-root", "Vintage Story performance patches: status and counters"));

        // The help text's key is derived from the subcommand's name, exactly like a HUD label's
        // (see Loc.Hud): a command and its description cannot end up under different keys, and
        // adding one cannot forget the entry. The parser shape is the only thing that varies.
        void Sub(string name, string help, System.Func<string> run)
            => root.BeginSubCommand(name)
                   .WithDescription(Loc.T("komet:cmd-" + name, help))
                   .HandleWith(_ => TextCommandResult.Success(run()))
                   .EndSubCommand();

        void SubArg(string name, string help, System.Func<string, string> run, bool required = false)
            => root.BeginSubCommand(name)
                   .WithDescription(Loc.T("komet:cmd-" + name, help))
                   .WithArgs(required ? api.ChatCommands.Parsers.Word("system")
                                      : api.ChatCommands.Parsers.OptionalWord("arg"))
                   .HandleWith(args => TextCommandResult.Success(run(args[0] as string)))
                   .EndSubCommand();

        Sub("hud", "Toggle the on-screen performance overlay (same as F7)", CmdHud);
        Sub("stats", "Show what the culling patch has been doing since the last reset", LoggedStats);
        SubArg("hitch", "Hitch log: every frame over the threshold with its cause and the camera movement. 'reset' clears it", CmdHitch);
        Sub("report", "Everything at once: environment, settings that differ from the default, frame breakdown, hitch log. Lands as one block in client-main.log", CmdReport);
        SubArg("toggle", "Turn a single system on or off: cull, occlusion, reclaim, sunquery, glerror, prebuild, firepit, entload, entsync ... - for bisecting or A/B measuring", ToggleSystem, required: true);
        SubArg("shadownear", "Size of the near shadow cascade's map in pixels (e.g. 4096), 'off' for the engine's; rebuilds the framebuffers live. No argument: show both maps", HandleShadowNear);
        SubArg("shadowneardepth", "Cap on the near cascade's depth in blocks (e.g. 80), 'off' for the engine's 150-200. The one number that sets how much geometry the near shadow pass draws", HandleShadowNearDepth);
        SubArg("alloctrace", "Records where the game allocates, with call stacks, for N seconds (default 20) into a .nettrace next to the logs - for the repository's alloctool", HandleAllocTrace);
        SubArg("farmesh", "Distance in blocks beyond which the far LOD picture replaces the engine's mesh (e.g. 500), 'off' or 'on' for the switch, no argument for the state. 0 = the default rule (0.35 x view distance, at least 400)", HandleFarMesh);
        SubArg("shadowfoliagerange", "Range in blocks beyond which leaves and plants cast no shadow (e.g. 100), 'off' for the cascade's own. The largest single GPU item there is", HandleShadowFoliageRange);
        SubArg("foliagerange", "Range in blocks beyond which leaves and plants are not drawn (e.g. 600), 'off' for the view distance. Priced by the report's triangles-by-distance rows", HandleFoliageRange);
        SubArg("shadownearskip", "How many frames the near shadow cascade is drawn in (1 = every frame). Live, so the GPU stage line prices it within a minute", HandleShadowNearSkip);
        SubArg("stress", "Automatic measurement run, drift-proof by interleaving baselines - moving or flying is fine. Optional: seconds per slice (default 2), or 'stop'", HandleStress);
        SubArg("retess", "Who marks chunks dirty? Counters and a sampled ranking of the sources. 'reset' clears it", CmdRetess);
        Sub("conflicts", "Who patches komet's methods or komet's own code, and does the engine differ from the verified build? Rescans immediately", CmdConflicts);
        SubArg("mods", "What the other mods cost per frame and at load, and what they do (patches, registered classes). 'hud' cycles the overlay (same as Shift+F7), 'reset' clears the per-frame figures", CmdMods);
        Sub("safemode", "Every optimisation that changes what is drawn, on or off at once - settles in seconds whether a visual glitch comes from komet", ToggleSafeMode);
        Sub("reset", "Reset the counters", CmdReset);
        Sub("gui", "Open the performance window (same as '.komet' with no argument, or Ctrl+F7)", OpenOverview);

        // '.komet' with no argument opens the window. It used to print the counters, and
        // that text has not gone anywhere - it is '.komet stats', which is also what the
        // window's own views are built from.
        root.HandleWith(_ => TextCommandResult.Success(OpenOverview()));
    }

    /// <summary>'.komet hud': the same flip F7 does.</summary>
    private string CmdHud()
    {
        hud.Visible = !hud.Visible;
        return hud.Visible
            ? Loc.T("komet:msg-hud-on", "HUD on (F7 toggles)")
            : Loc.T("komet:msg-hud-off", "HUD off");
    }

    private string OpenOverview() => OpenWindow(Gui.KometView.Overview);

    // ---- the commands, as methods -----------------------------------------------------
    // Each of these used to be a lambda inside the registration above. They are methods now
    // for one reason: the window's buttons call them. A button that reimplemented "generate a
    // report" or "clear the hitch log" would be a second implementation to keep in agreement
    // with the first, and the first is the one people paste into bug reports.

    /// <summary>'.komet hitch [reset]'.</summary>
    internal string CmdHitch(string arg)
    {
        if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            HitchLog.Reset();
            return Loc.T("komet:msg-hitch-cleared", "hitch log cleared.");
        }

        var report = HitchLog.BuildReport();
        Mod.Logger.Notification("hitch report:\n{0}", report);
        return report;
    }

    /// <summary>'.komet report': the whole thing into the log, and a pointer to it in the chat.</summary>
    internal string CmdReport()
    {
        var report = BuildFullReport();
        // The log, not the chat: this is several hundred characters wide by design and the
        // chat window wraps it into something nobody can copy back out.
        Mod.Logger.Notification("full report:\n{0}", report);
        return Loc.T("komet:msg-report-written",
            "the report is in client-main.log (between '==== komet report ====' and "
            + "'==== end ===='). Copy the whole block.");
    }

    /// <summary>The report itself, for the window's page and its copy button.</summary>
    internal string ReportText() => BuildFullReport();

    /// <summary>'.komet retess [reset]'.</summary>
    internal string CmdRetess(string arg)
    {
        if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            RetessSourcePatches.Reset();
            return Loc.T("komet:msg-retess-cleared", "dirty mark counters cleared.");
        }

        var report = RetessSourcePatches.BuildReport();
        Mod.Logger.Notification("retess report:\n{0}", report);
        return report;
    }

    /// <summary>'.komet conflicts': rescan, then the guard's lines.</summary>
    internal string CmdConflicts()
    {
        if (!PatchGuard.EngineChecked)
            PatchGuard.CheckEngine(Vintagestory.API.Config.GameVersion.LongGameVersion);
        PatchGuard.Scan();
        var text = PatchGuard.ReportLines();
        Mod.Logger.Notification("patch guard:\n{0}", text);
        return text;
    }

    /// <summary>'.komet mods [hud|reset]'.</summary>
    internal string CmdMods(string arg)
    {
        if (string.Equals(arg, "hud", StringComparison.OrdinalIgnoreCase))
            return CycleModHud();
        if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            ModProfiler.Reset();
            return Loc.T("komet:msg-mods-reset", "per-mod counters cleared.");
        }

        var text = ModProfileText(10, rescan: true);
        if (ModProfiler.Enabled) Mod.Logger.Notification("mod profile:\n{0}", text);
        return text;
    }

    /// <summary>
    /// The per-mod table, for the chat reply and for the window's Mods view.
    ///
    /// The chat reply rescans the inventory first, because a table nobody refreshed is a stale
    /// one and mods patch lazily throughout a session. The window does NOT: it composes several
    /// times a second, and a scan over every loaded assembly's patches at that rate would be
    /// this window costing more than the thing it reports. It reads what the ten-second scan
    /// listener already collected, and its Rescan button forces one.
    /// </summary>
    internal string ModProfileText(int count, bool rescan)
    {
        if (!ModProfiler.Enabled)
            return Loc.T("komet:msg-mods-off", "mod profiling is off (ProfileMods in komet.json).");

        if (rescan) ScanMods();
        var sb = new StringBuilder(1200);
        ModProfiler.Write(sb, CultureInfo.CurrentCulture, FrameStats.AvgFrameMs,
            RendererProfiler.Enabled, count);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>'.komet safemode'.</summary>
    internal string CmdSafeMode() => ToggleSafeMode();

    /// <summary>'.komet stress [seconds|stop]'.</summary>
    internal string CmdStress(string arg) => HandleStress(arg);

    /// <summary>'.komet reset'.</summary>
    internal string CmdReset()
    {
        ResetStats();
        return Loc.T("komet:msg-counters-reset", "komet counters reset.");
    }

    /// <summary>
    /// Opens the window, or brings it to the view named. Created on first use rather than at
    /// world start: a player who never types '.komet' never pays for its existence, and the
    /// window holds a cairo surface and a GL texture once it does exist.
    /// </summary>
    internal string OpenWindow(Gui.KometView view)
    {
        if (capi == null) return Loc.T("komet:msg-gui-no-world", "the window needs a world.");

        try
        {
            window ??= new Gui.KometDialog(capi, this);
            return window.OpenAt(view)
                ? Loc.T("komet:msg-gui-open", "komet window open - Ctrl+F7 or Esc closes it, '.komet stats' prints the same figures as text.")
                : Loc.T("komet:msg-gui-failed", "the window could not be opened - see client-main.log.");
        }
        catch (Exception e)
        {
            // A window that will not open must answer in the chat, not throw out of the chat
            // command dispatcher. The counters are still reachable as text either way.
            Mod.Logger.Error("could not open the komet window:\n{0}", e);
            try { window?.TryClose(); } catch { /* it never got far enough to be open */ }
            window = null;
            return Loc.T("komet:msg-gui-failed", "the window could not be opened - see client-main.log.");
        }
    }

    /// <summary>
    /// '.komet shadownear [px|off]': the near cascade's map size, live. A resize rebuilds
    /// every framebuffer (what the graphics menu does on a change), so this is one hitch per
    /// call and then the new size - the way to compare 7168 against 4096 against 3072 on the
    /// GPU stage line within a minute instead of a restart per candidate.
    /// </summary>
    private string HandleShadowNear(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return Loc.T("komet:msg-shadow-maps", "shadow maps: far {0}px, near {1}px ({2}px configured, 0 = as far)",
                ShadowResPatches.EffectiveMapSize, ShadowResPatches.EffectiveNearMapSize,
                ShadowResPatches.NearMapSize);

        // 'far' is a second spelling of 'off' here: 0 means the near map follows the far one
        if (string.Equals(arg, "far", StringComparison.OrdinalIgnoreCase)) arg = "off";
        if (!TryValue(arg, ShadowResPatches.NearMapMin, ShadowResPatches.NearMapMax, out var px))
            return Loc.T("komet:msg-shadownear-arg", "give a size in pixels (512-16384) or 'off'");
        var size = (int)px;

        var platform = capi?.World is Vintagestory.Client.NoObf.ClientMain game
            ? game.Platform as Vintagestory.Client.NoObf.ClientPlatformWindows
            : null;
        var result = ShadowResPatches.TryResizeNear(platform, size);
        Mod.Logger.Notification("shadownear {0}: {1}", arg, result);
        return result;
    }

    /// <summary>
    /// The argument shape most of the live-tuning commands share: a number inside a range, or
    /// 'off' for zero. Returns false when the word is neither, so the caller answers with its
    /// own "give a ... " line - the ranges and the wording differ per command, the parsing
    /// does not. Invariant culture on purpose: a command argument is typed, not localised.
    /// </summary>
    private static bool TryValue(string arg, double min, double max, out double value)
    {
        if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
        return double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && value >= min && value <= max;
    }

    /// <summary>'.komet shadowfoliagerange [blocks|off]': how far foliage casts, live.</summary>
    private string HandleShadowFoliageRange(string arg)
    {
        var ci = CultureInfo.CurrentCulture;
        if (string.IsNullOrWhiteSpace(arg))
            return FastCuller.ShadowFoliageRangeSq > 0
                ? Loc.T("komet:msg-shadowfoliagerange", "leaves and plants cast a shadow to {0} blocks",
                    Math.Sqrt(FastCuller.ShadowFoliageRangeSq).ToString("F0", ci))
                : Loc.T("komet:msg-shadowfoliagerange-off", "leaves and plants cast to the cascade's own range (vanilla)");

        if (!TryValue(arg, 16, 4096, out var blocks))
            return Loc.T("komet:msg-shadowfoliagerange-arg", "give a range in blocks (16-4096) or 'off'");

        FastCuller.ShadowFoliageRangeSq = blocks > 0 ? blocks * blocks : 0;
        var result = blocks > 0
            ? Loc.T("komet:msg-shadowfoliagerange-set", "leaves and plants cast a shadow to {0} blocks - the gpu row prices it within a minute; look at a distant forest before keeping it", blocks.ToString("F0", ci))
            : Loc.T("komet:msg-shadowfoliagerange-off", "leaves and plants cast to the cascade's own range (vanilla)");
        Mod.Logger.Notification("shadowfoliagerange {0}: {1}", arg, result);
        return result;
    }

    /// <summary>'.komet foliagerange [blocks|off]': the foliage passes' draw range, live.</summary>
    private string HandleFoliageRange(string arg)
    {
        var ci = CultureInfo.CurrentCulture;
        if (string.IsNullOrWhiteSpace(arg))
            return FastCuller.FoliageRangeSq > 0
                ? Loc.T("komet:msg-foliagerange", "foliage drawn to {0} blocks (leaves and plants; the terrain to the view distance)",
                    Math.Sqrt(FastCuller.FoliageRangeSq).ToString("F0", ci))
                : Loc.T("komet:msg-foliagerange-off", "foliage drawn to the view distance (vanilla)");

        if (!TryValue(arg, 32, 4096, out var blocks))
            return Loc.T("komet:msg-foliagerange-arg", "give a range in blocks (32-4096) or 'off'");

        FastCuller.FoliageRangeSq = blocks > 0 ? blocks * blocks : 0;
        var result = blocks > 0
            ? Loc.T("komet:msg-foliagerange-set", "foliage drawn to {0} blocks - trees beyond it are trunks; the report's triangle rows price it", blocks.ToString("F0", ci))
            : Loc.T("komet:msg-foliagerange-off", "foliage drawn to the view distance (vanilla)");
        Mod.Logger.Notification("foliagerange {0}: {1}", arg, result);
        return result;
    }

    /// <summary>'.komet alloctrace [seconds]': the process records its own allocations with stacks.</summary>
    private string HandleAllocTrace(string arg)
    {
        var seconds = 20;
        if (!string.IsNullOrWhiteSpace(arg)
            && (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                || seconds < 3 || seconds > 300))
            return Loc.T("komet:msg-alloctrace-arg", "give a duration in seconds (3-300), default 20");

        var api = capi;
        var path = AllocTrace.Start(seconds, Vintagestory.API.Config.GamePaths.Logs, result =>
        {
            Mod.Logger.Notification("alloctrace: {0}", result);
            api?.Event.EnqueueMainThreadTask(() => api.ShowChatMessage("[komet] " + result), "komet-alloctrace");
        });
        if (path == null)
            return Loc.T("komet:msg-alloctrace-fail", "allocation trace not started: {0}", AllocTrace.LastError);
        Mod.Logger.Notification("alloctrace: recording {0} s to {1}", seconds, path);
        return Loc.T("komet:msg-alloctrace-start", "recording allocations with stacks for {0} s - keep doing what hitches; the file lands next to the logs", seconds);
    }

    /// <summary>'.komet farmesh [blocks|off|on]': the far LOD's distance and switch, live.</summary>
    private string HandleFarMesh(string arg)
    {
        var ci = CultureInfo.CurrentCulture;
        string State()
        {
            if (!FarMeshPatches.Installed)
                return Loc.T("komet:msg-no-farmesh", "the far lod patch is not installed - FarMesh in komet.json.");
            if (!FarMesh.Enabled)
                return Loc.T("komet:msg-farmesh-off", "far lod OFF: every chunk drawn as tesselated, at every distance (vanilla)");
            var dist = FarMesh.LastEffectiveSq > 0
                ? Math.Sqrt(FarMesh.LastEffectiveSq).ToString("F0", ci) + (FarMesh.DistanceSq > 0 ? "" : " (default rule)")
                : "?";
            return Loc.T("komet:msg-farmesh", "far lod ON beyond {0} blocks ({1} pictures in the pools, {2} engine parts stopped at the distance){3}",
                dist, FarMeshPatches.TrackedFar, FarMeshPatches.TrackedNear,
                FarMesh.Active ? "" : Loc.T("komet:msg-farmesh-inactive", " - not drawn right now: the sweep is off"));
        }

        if (string.IsNullOrWhiteSpace(arg)) return State();
        if (!FarMeshPatches.Installed) return State();

        if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
        {
            FarMesh.Enabled = false;
            Mod.Logger.Notification("farmesh off");
            return State();
        }
        if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
        {
            FarMesh.Enabled = true;
            Mod.Logger.Notification("farmesh on");
            return State();
        }
        if (!double.TryParse(arg, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var blocks)
            || blocks < 0 || blocks > 4096)
            return Loc.T("komet:msg-farmesh-arg", "give a distance in blocks (0 = the default rule, up to 4096), 'off' or 'on'");

        FarMesh.DistanceSq = blocks > 0 ? blocks * blocks : 0;
        FarMesh.Enabled = true;
        var result = Loc.T("komet:msg-farmesh-set", "far lod beyond {0} blocks - chunks already in the pools switch on the next frame; the report's 'far lod' row prices it",
            blocks > 0 ? blocks.ToString("F0", ci) : "the default rule (0,35 of the view distance, at least 400)");
        Mod.Logger.Notification("farmesh {0}: {1}", arg, result);
        return result;
    }

    /// <summary>
    /// '.komet shadowneardepth [blocks|off]': the near cascade's depth, live. See
    /// ShadowPatches.NearDepthExtend for what it trades.
    /// </summary>
    private string HandleShadowNearDepth(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return Loc.T("komet:msg-shadowneardepth", "near cascade depth: {0} blocks (the engine wants {1})",
                ShadowPatches.NearExtendUsed.ToString("F0", CultureInfo.CurrentCulture),
                ShadowPatches.NearExtendVanilla.ToString("F0", CultureInfo.CurrentCulture));

        if (!TryValue(arg, 8, 400, out var blocks))
            return Loc.T("komet:msg-shadowneardepth-arg", "give a depth in blocks (8-400) or 'off'");

        ShadowPatches.NearDepthExtend = blocks;
        var result = Loc.T("komet:msg-shadowneardepth-set", "near cascade depth capped at {0} blocks (0 = the engine's)",
            blocks.ToString("F0", CultureInfo.CurrentCulture));
        Mod.Logger.Notification("shadowneardepth {0}: {1}", arg, result);
        return result;
    }

    /// <summary>
    /// '.komet shadownearskip [n]': how often the near cascade is drawn, live.
    ///
    /// The far cascade has been throttled since 1.43.0 and the near one has not, because it
    /// covers the ground right around the player where lag is easiest to see. But when a GPU
    /// report puts the near cascade at twenty of twenty-four milliseconds, halving how often it
    /// is drawn is the largest single number on the table - and the retained map is reprojected
    /// exactly for camera movement (ShadowThrottlePatches.OffsetShadowMatrix), so what actually
    /// goes stale is only what MOVED: entities, and foliage in the wind. This makes that a
    /// one-command experiment instead of a config edit and a restart.
    /// </summary>
    private string HandleShadowNearSkip(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return Loc.T("komet:msg-shadownearskip", "near cascade: drawn every {0} frame(s)",
                ShadowThrottlePatches.NearInterval);

        if (!int.TryParse(arg, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var frames)
            || frames < 1 || frames > 8)
            return Loc.T("komet:msg-shadownearskip-arg", "give a number of frames (1-8)");

        ShadowThrottlePatches.SetIntervals(ShadowThrottlePatches.FarInterval,
            frames, ShadowThrottlePatches.FarMaxSkip);
        var result = Loc.T("komet:msg-shadownearskip", "near cascade: drawn every {0} frame(s)",
            ShadowThrottlePatches.NearInterval);
        Mod.Logger.Notification("shadownearskip {0}: {1}", arg, result);
        return result;
    }

    private string HandleStress(string arg)
    {
        if (string.Equals(arg, "stop", StringComparison.OrdinalIgnoreCase))
            return StressTest.Stop("on request");
        if (safeMode)
            return Loc.T("komet:msg-safemode-blocks", "Safemode is on - take it back with '.komet safemode' first, then test.");

        double sliceSeconds = 2;
        if (arg != null && double.TryParse(arg, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed))
            sliceSeconds = parsed;

        return StressTest.Start(BuildStressPhases(), sliceSeconds, roundCount: 3, report =>
        {
            Mod.Logger.Notification("stresstest:\n{0}", report);
            capi?.ShowChatMessage(report);
        });
    }

    /// <summary>
    /// Every system with a runtime switch, each restoring exactly what komet.json
    /// configured. Baselines are not listed - the scheduler interleaves one before and
    /// after every test slice, which is what makes the deltas drift-proof.
    /// </summary>
    private List<StressTest.Phase> BuildStressPhases() => new()
    {
        new StressTest.Phase { Name = "sweep off (vanilla)",
            Enter = () => FastCuller.Enabled = false,
            Exit = () => FastCuller.Enabled = config.FastFrustumCulling },
        new StressTest.Phase { Name = "occlusion off (vanilla)",
            Enter = () => FastChunkCuller.Enabled = false,
            Exit = () => FastChunkCuller.Enabled = config.FastOcclusionCulling },
        // Default is vanilla's window build since 1.42.2, so the phase turns the pipeline ON.
        // It measured -0,05 +-0,10 here, i.e. nothing - the throughput it buys is chunk LOADING,
        // which a frame-time delta cannot see.
        new StressTest.Phase { Name = "window pipe on",
            Enter = () => WindowPrebuilder.Enabled = !WindowPrebuilder.HardDisabled,
            // never resurrect a feature that disabled itself (validation limit, worker crash)
            Exit = () => WindowPrebuilder.Enabled = config.TesselationWindowPipelining && !WindowPrebuilder.HardDisabled },
        new StressTest.Phase { Name = "firepit gate off",
            Enter = () => FirepitPatches.Enabled = false,
            Exit = () => FirepitPatches.Enabled = true },
        new StressTest.Phase { Name = "glerror skip on",
            Enter = () => GlErrorPatches.SkipEnabled = true,
            Exit = () => GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck },
        new StressTest.Phase { Name = "sun query every frame",
            Enter = () => SunQueryPatches.Interval = 1,
            Exit = () => SunQueryPatches.Interval = config.SunOcclusionQueryInterval },
        new StressTest.Phase { Name = "entity tess budget off",
            Enter = () => EntityTessPatches.Enabled = false,
            Exit = () => EntityTessPatches.Enabled = config.EntityTesselationBudgetMs > 0 },
        // Off = everything held finishes at once and every packet goes straight to vanilla.
        // Only streaming scenes (join flood, flying) have entity loads to measure.
        // Off = vanilla's 200 pieces per tick; only measurable while the minimap fills.
        new StressTest.Phase { Name = "minimap budget off (200/tick)",
            Enter = () => MinimapPatches.Enabled = false,
            Exit = () => MinimapPatches.Enabled = config.MinimapPieceBudgetMs > 0 },
        new StressTest.Phase { Name = "minimap direct upload off (FBO)",
            Enter = () => MinimapPatches.DirectUpload = false,
            Exit = () => MinimapPatches.DirectUpload = config.MinimapDirectUpload },
        // Off = vanilla's whole-queue drain; only a streaming scene has bursts to cut.
        new StressTest.Phase { Name = "task budget off",
            Enter = () => MainThreadTaskPatches.BudgetMs = 0,
            Exit = () => MainThreadTaskPatches.BudgetMs = Math.Max(0, config.MainThreadTaskBudgetMs) },
        // Off = every entity animates every frame, as vanilla; measurable wherever many
        // entities are loaded (a farm, a join flood).
        new StressTest.Phase { Name = "anim lod off",
            Enter = () => EntityAnimPatches.LodEnabled = false,
            Exit = () => EntityAnimPatches.LodEnabled = config.EntityAnimationLod },
        new StressTest.Phase { Name = "entity load budget off",
            Enter = () => { EntityLoadPatches.Enabled = false; EntityLoadPatches.FlushAll(); },
            Exit = () => EntityLoadPatches.Enabled = config.EntityLoadBudgetMs > 0 },
        // Server side, singleplayer only: fewer position/attribute packets means less for the
        // integrated server to build and the shared GC to collect - a GC-column effect, like
        // the recycler, not a per-frame CPU one.
        new StressTest.Phase { Name = "entity sync tuning off (server)",
            Enter = () => { EntitySyncPatches.DistanceSendRate = false; EntitySyncPatches.TrackingHysteresis = false; },
            Exit = () => { EntitySyncPatches.DistanceSendRate = config.ServerEntitySyncTuning;
                           EntitySyncPatches.TrackingHysteresis = config.ServerEntitySyncTuning; } },
        new StressTest.Phase { Name = "attribute no-op skip off (server)",
            Enter = () => EntitySyncPatches.AttributeNoOpSkip = false,
            Exit = () => EntitySyncPatches.AttributeNoOpSkip = config.ServerAttributeNoOpSkip },
        new StressTest.Phase { Name = "edge coalescing off",
            Enter = () => { EdgeCoalescePatches.Enabled = false; EdgeCoalescePatches.FlushAll(); },
            Exit = () => EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0 },
        // The shadow group. Until 1.40.0 the plan had no phase for any of it, which is why
        // "safemode is faster" could not be attributed: the symmetric box is by far the largest
        // change this mod makes to what the GPU is asked to draw - it replaces vanilla's
        // 0.78 R x 0.44 R wedge with a 2R cube, and at ShadowDistanceMultiplier 1.5 that was
        // 765 blocks of ground on the same shadow map. Every one of these four is a different
        // way to give some of that back.
        // The sphere box is default-on again since 1.43.0 (far cascade only, after the user
        // photographed vanilla's hard shadow edge), so the phase turns it OFF and the delta
        // reads as its remaining cost. The 1.42.x both-cascades version measured +0,72 +-0,08;
        // far-only must come in under that - this phase is what checks it.
        new StressTest.Phase { Name = "shadow box off (vanilla wedge)",
            Enter = () => ShadowPatches.SymmetricBox = false,
            Exit = () => ShadowPatches.SymmetricBox = config.SymmetricShadowBox },
        // The coverage margin is what makes the throttle work while MOVING, so this phase is
        // the one to read during a flight: without it the far cascade is redrawn on almost
        // every frame, with it at the staleness cap. Standing still it costs a few texels of
        // density and saves nothing - the throttle was already skipping.
        new StressTest.Phase { Name = "far shadow coverage margin off (redraw on every step)",
            Enter = () => { ShadowPatches.FarBoxMargin = 0; ShadowThrottlePatches.Invalidate(); },
            Exit = () => { ShadowPatches.FarBoxMargin = config.ShadowFarBoxMargin;
                           ShadowThrottlePatches.Invalidate(); } },
        // Default-on since 1.43.0; the phase switches it off, so the delta reads as what the
        // throttle SAVES in this scene. It used to save nothing while moving - the movement
        // rule forced a redraw almost every frame - which is what the coverage margin above
        // fixed; with the margin on, this phase reads the same standing still and flying.
        new StressTest.Phase { Name = "shadow throttle off (every frame)",
            Enter = () => ShadowThrottlePatches.SetIntervals(1, 1, 1),
            Exit = () => ShadowThrottlePatches.SetIntervals(
                config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip) },
        // Default-on since 05.09.; the phase draws every face again, so the delta is what the
        // culled back faces cost in this scene. GPU work - visible only in a GPU-bound frame,
        // which the report's "gpu" figure against the frame time tells apart.
        new StressTest.Phase { Name = "shadow backface cull off (every face)",
            Enter = () => ShadowCullPatches.Enabled = false,
            Exit = () => ShadowCullPatches.Enabled = config.ShadowCullBackfaces },
        new StressTest.Phase { Name = "shadow depth-only shader off (alpha test everywhere)",
            Enter = () => ShadowCullPatches.DepthOnly = false,
            Exit = () => ShadowCullPatches.DepthOnly = config.ShadowDepthOnlySolidPasses },
        // No phase for ShadowDistanceMultiplier any more: it has been 1.0 (vanilla) since
        // 1.40.0, so the phase set it to the value it already had and measured pure noise.
        // The grid's cell size, measured in the player's own scene rather than in the harness.
        //
        // The harness has now been wrong about this twice: it modelled 96 pools where the game
        // has 600, and it drew part positions uniformly at random while claiming tesselation
        // order - and this constant is exactly the one both of those decide. The benchmark
        // prefers 32 at the measured pool shape, but the benchmark is a model of the scene and
        // this phase is the scene. Interleaved against neighbour baselines like every other
        // phase, so the answer does not depend on which minute it was measured in.
        new StressTest.Phase { Name = "cell target 160 instead of " + DefaultCellTarget,
            Enter = () => SetCellTarget(160),
            Exit = () => SetCellTarget(config.PartsPerCellTarget) },
        // Default-on; the phase switches it OFF, so the delta reads as what bridging draw
        // ranges across frustum-clipped parts saves in this scene. The mechanism trades CPU
        // submission cost (fewer glMultiDrawElements ranges) for GPU vertex work on clipped,
        // pixel-identical geometry - measurable only where the frame is CPU-bound, which is
        // exactly what the 1.47/1.48 reports showed (gpu ~2,5 ms of ~13 ms).
        new StressTest.Phase { Name = "gap merge off",
            Enter = () => FastCuller.GapMergeDrawRanges = false,
            Exit = () => FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges },
        // Default-on; the phase hands the recycler's storage back to vanilla, so the delta
        // reads as what the size-class pool saves. Its effect is GC pressure, not per-frame
        // CPU - expect it to show only in streaming scenes (fly over fresh terrain), and
        // read it together with the gc column of the hitch log.
        new StressTest.Phase { Name = "mesh recycler off (vanilla store)",
            Enter = () => MeshRecyclerPatches.SetEnabled(false),
            Exit = () => MeshRecyclerPatches.SetEnabled(config.FastMeshRecycler) },
        new StressTest.Phase { Name = "extras pool off (fresh arrays)",
            Enter = () => { TightClonePatches.PoolExtras = false; TightClonePatches.ClearPools();
                            FarLod.PoolArrays = false; FarLod.ClearPools(); },
            Exit = () => { TightClonePatches.PoolExtras = config.PoolMeshExtras;
                           FarLod.PoolArrays = config.PoolMeshExtras; } },
        new StressTest.Phase { Name = "animatable gate off (vanilla)",
            Enter = () => AnimatableCullPatches.Enabled = false,
            Exit = () => AnimatableCullPatches.Enabled = config.CullAnimatableRenderers },
        new StressTest.Phase { Name = "lod3 out of the shadow pass",
            Enter = () => FastCuller.ShadowSkipRedundantLod = true,
            Exit = () => FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod },
        // The diagnostics group. These do not draw anything and safemode does not switch them
        // off, so they were invisible to every previous stress run - and "safemode is faster"
        // was reported again after the drawing systems had all been cleared by measurement.
        // Instrumentation the mod carries is on the same side of the ledger as the work it
        // removes; these three phases are what makes that testable rather than argued.
        new StressTest.Phase { Name = "renderer profiler on (diagnostic)",
            Enter = () => { RendererProfiler.Enabled = true; WrapRenderers(); },
            Exit = () => { RendererProfiler.Enabled = config.ProfileRenderers;
                           if (config.ProfileRenderers) WrapRenderers(); else UnwrapRenderers(); } },
        new StressTest.Phase { Name = "retess source sampling on (diagnostic)",
            Enter = () => RetessSourcePatches.SampleSources = true,
            Exit = () => RetessSourcePatches.SampleSources = config.SampleRetessSources },
        new StressTest.Phase { Name = "sweep cross-check on (diagnostic)",
            Enter = () => { CullVerifier.SampleEvery = 512; CullVerifier.Reset(); },
            Exit = () => CullVerifier.SampleEvery = config.VerifyCullSweepEvery },
        // The two always-on attributions, priced like the before-stage attribution: a few
        // Stopwatch reads per frame, but measured rather than assumed.
        new StressTest.Phase { Name = "task attribution off (vanilla drain)",
            Enter = () => MainThreadTaskPatches.Enabled = false,
            Exit = () => MainThreadTaskPatches.Enabled = config.AttributeMainThreadTasks },
        new StressTest.Phase { Name = "tick profiler off",
            Enter = () => { TickProfiler.Enabled = false; WrapTickListeners(); },
            Exit = () => { TickProfiler.Enabled = config.ProfileTickListeners; WrapTickListeners(); } },
        new StressTest.Phase { Name = "sweep vector kernel off (scalar)",
            Enter = () => FastCuller.VectorCulling = false,
            Exit = () => FastCuller.VectorCulling = config.VectorCulling && FastCuller.VectorAvailable },
        new StressTest.Phase { Name = "everything vanilla (= safemode)",
            Enter = AllVanilla,
            Exit = AllConfigured },
    };

    /// <summary>
    /// Flips exactly one system by name, so a visual artefact can be bisected while it is on
    /// screen. The systems themselves live in the toggle table (KometModSystem.Toggles.cs); this
    /// is the chat command's way in, and the window's way in is the same table - a flip made in
    /// either place runs the same code and prints the same sentence.
    /// </summary>
    private string ToggleSystem(string system)
    {
        var entry = Toggles.Find(system);
        if (entry == null)
            return Loc.T("komet:msg-unknown-system", "unknown. Systems: ") + Toggles.KeyList();

        // A machine without AVX has no vector kernel to switch. Say so and change nothing,
        // rather than reporting a flip that did not happen.
        var blocked = entry.Unavailable?.Invoke();
        if (blocked != null) return blocked;

        return Announce(entry.Flip());
    }

    /// <summary>
    /// A flip, logged and answered with the world's loading state next to it. Every toggle logs
    /// that, because the strongest confounder so far has been time itself - artefacts reported
    /// during streaming were gone once the queue drained, whatever was toggled in between.
    /// Internal so the window's toggle rows leave the same line in the log as the chat command.
    /// </summary>
    internal string Announce(string state)
    {
        var world = $"chunks {Vintagestory.Client.RuntimeStats.chunksReceived:N0} received, "
                    + $"queued {Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation:N0}, "
                    + $"uptime {uptime.Elapsed.TotalSeconds:F0}s";
        Mod.Logger.Notification("toggle: {0} | world: {1}", state, world);
        return state + " | " + world;
    }

    /// <summary>
    /// Flips everything that changes WHAT is drawn, in one place, at runtime. Measurement
    /// patches stay on - they only observe - and so do the loading-pipeline patches, which
    /// affect when chunks arrive but never how they are drawn.
    /// </summary>
    private string ToggleSafeMode()
    {
        safeMode = !safeMode;
        if (safeMode)
        {
            if (StressTest.Running) StressTest.Stop("safemode takes over");
            savedSunInterval = SunQueryPatches.Interval;
            savedGlErrorSkip = GlErrorPatches.SkipEnabled;
            savedFirepitGate = FirepitPatches.Enabled;
            savedEntityTess = EntityTessPatches.Enabled;
            savedEdgeCoalesce = EdgeCoalescePatches.Enabled;
            savedEdgePriority = EdgeRetessPriorityPatches.Enabled;
            AllVanilla();
            Mod.Logger.Notification("safemode ON | queued {0:N0}, uptime {1:F0}s",
                Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
            return Loc.T("komet:msg-safemode-on",
                "SAFEMODE ON - komet no longer draws anything differently from vanilla. "
                + "Glitch still there? Then it is not this mod. '.komet safemode' switches back.");
        }

        AllConfigured();
        // a live toggle the user made before entering safemode survives it; only what safemode
        // itself flipped comes back from config
        SunQueryPatches.Interval = savedSunInterval;
        GlErrorPatches.SkipEnabled = savedGlErrorSkip;
        FirepitPatches.Enabled = savedFirepitGate;
        EntityTessPatches.Enabled = savedEntityTess;
        EdgeCoalescePatches.Enabled = savedEdgeCoalesce;
        EdgeRetessPriorityPatches.Enabled =
            savedEdgePriority && !EdgeRetessPriorityPatches.HardDisabled;
        Mod.Logger.Notification("safemode OFF | queued {0:N0}, uptime {1:F0}s",
            Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, uptime.Elapsed.TotalSeconds);
        return Loc.T("komet:msg-safemode-off", "Safemode off - the optimisations run according to komet.json again.");
    }

    /// <summary>
    /// Everything that changes WHAT is drawn, handed back to vanilla. Shared by safemode and by
    /// the stress test's combined phase - the user reported the whole mod measuring slower than
    /// safemode, and a plan that can only flip systems one at a time cannot reproduce that
    /// observation, let alone check whether the parts add up to the whole.
    /// </summary>
    private void AllVanilla()
    {
        FastCuller.Enabled = false;                 // sweep, spatial index, batching, merging -> vanilla
        FastCuller.ShadowSkipRedundantLod = false;  // both LOD versions into the shadow map again
        FastChunkCuller.Enabled = false;            // occlusion walk -> vanilla
        PoolReclaimer.Enabled = false;              // stop reclaiming; already-empty pools stay empty
        SunQueryPatches.Interval = 1;       // sun occlusion query every frame again
        GlErrorPatches.SkipEnabled = false; // vanilla error detection back on
        FirepitPatches.Enabled = false;     // draw every firepit again
        EntityTessPatches.Enabled = false;  // tesselate entity shapes immediately again
        AnimatableCullPatches.Enabled = false; // every animated block entity draws in every stage again
        EdgeCoalescePatches.Enabled = false;
        EdgeCoalescePatches.FlushAll();     // held edge marks go out, nothing strands
        EdgeRetessPriorityPatches.Enabled = false; // vanilla queue order again
        // shadows too: box shape, fade range, distance and update cadence all back to
        // vanilla, so "is a shadow artefact ours?" is answerable with one command
        ShadowPatches.ToVanilla();
        ShadowThrottlePatches.SetIntervals(1, 1, 1);
        ShadowStabilityPatches.Enabled = false;
        ShadowDepthPatches.Enabled = false;     // the near volume back to the engine's depth
        ShadowFootprintPatches.Enabled = false; // the near pass drawn for every direction again
        ShadowCullPatches.SkipFoliage = false;  // the diagnostic skip is never part of a configuration
        FastCuller.FoliageRangeSq = 0;                  // foliage to the view distance again
        FastCuller.ShadowFoliageRangeSq = 0;            // foliage casts to the cascade's range again
        ChunkShaderSwap.Restore();                      // the engine's fragment shader again
        FastCuller.FrontToBack = false;                 // index order again
        SpatialPools.Enabled = false;                   // first-fit again for new models
        FarMesh.Enabled = false;                        // faces at every distance again (next frame)
        ShadowCullPatches.Enabled = false;      // every face into the shadow maps again
        ShadowCullPatches.DepthOnly = false;    // the engine's shader for every pass again
    }

    /// <summary>The exact inverse: everything back to what komet.json asked for.</summary>
    private void AllConfigured()
    {
        FastCuller.Enabled = config.FastFrustumCulling;
        FastCuller.GapMergeDrawRanges = config.GapMergeDrawRanges;
        FastCuller.ShadowSkipRedundantLod = config.ShadowSkipRedundantLod;
        FastChunkCuller.Enabled = config.FastOcclusionCulling;
        PoolReclaimer.Enabled = config.ReclaimEmptyPools;
        SunQueryPatches.Interval = config.SunOcclusionQueryInterval;
        GlErrorPatches.SkipEnabled = config.SkipPerFrameGlErrorCheck;
        FirepitPatches.Enabled = true;
        EntityTessPatches.Enabled = config.EntityTesselationBudgetMs > 0;
        AnimatableCullPatches.Enabled = config.CullAnimatableRenderers;
        EdgeCoalescePatches.Enabled = config.EdgeRetessCoalesceMs > 0;
        EdgeRetessPriorityPatches.Enabled =
            config.EdgeRetessPriority && !EdgeRetessPriorityPatches.HardDisabled;
        ShadowPatches.ToConfigured(config.SymmetricShadowBox, config.FixShadowFadeCutoff);
        ShadowThrottlePatches.SetIntervals(
            config.ShadowFarUpdateInterval, config.ShadowNearUpdateInterval, config.ShadowFarMaxSkip);
        ShadowStabilityPatches.Enabled = config.StabiliseShadowTexels;
        ShadowDepthPatches.Enabled = ShadowDepthPatches.ConfiguredEnabled;
        ShadowFootprintPatches.Enabled = ShadowFootprintPatches.ConfiguredEnabled;
        FastCuller.FoliageRangeSq = config.FoliageRange > 0 ? config.FoliageRange * config.FoliageRange : 0;
        FastCuller.ShadowFoliageRangeSq = config.ShadowFoliageRange > 0 ? config.ShadowFoliageRange * config.ShadowFoliageRange : 0;
        ParticlePatches.Orphan = ParticlePatches.ConfiguredOrphan;
        FastCuller.FrontToBack = FastCuller.ConfiguredFrontToBack;
        SpatialPools.Enabled = SpatialPools.ConfiguredEnabled;
        FarMesh.Enabled = FarMesh.ConfiguredEnabled && FarMeshPatches.Installed;
        FarMesh.DistanceSq = FarMesh.ConfiguredDistanceSq;
        ShadowCullPatches.Enabled = config.ShadowCullBackfaces;
        ShadowCullPatches.DepthOnly = config.ShadowDepthOnlySolidPasses;
        SetCellTarget(config.PartsPerCellTarget);
    }

    /// <summary>
    /// The mod HUD's three steps: off -> compact -> full -> off. The same rule the performance
    /// HUD's F7 uses (DebugHud.CycleF7, pure and pinned by verify), driven by Shift+F7 and by
    /// '.komet mods hud' - two boxes that cycle differently would be the real surprise.
    /// </summary>
    private string CycleModHud()
    {
        (var visible, var compact) = DebugHud.CycleF7(modHud.Visible, modHud.Compact);
        modHud.Compact = compact;
        modHud.Visible = visible;
        if (!visible) return Loc.T("komet:msg-modhud-off", "mod HUD off");
        return compact
            ? Loc.T("komet:msg-modhud-on", "mod HUD on (Shift+F7 cycles compact / full / off)")
            : Loc.T("komet:msg-modhud-full", "mod HUD: full view (Shift+F7 again turns it off)");
    }

    private static void ResetStats()
    {
        FastCuller.StatSweeps = 0;
        FastCuller.StatPartsTested = 0;
        FastCuller.StatPoolsSkipped = 0;
        FastCuller.StatRebuilds = 0;
        FastCuller.StatRangesRaw = 0;
        FastCuller.StatRangesEmitted = 0;
        FastCuller.StatRangesBridged = 0;
        FastCuller.StatPartsBridged = 0;
        FastCuller.StatTrisBridged = 0;
        FastChunkCuller.StatPasses = 0;
        FastChunkCuller.StatPeakMs = 0;
        MeshUploadPatches.StatBulkCalls = 0;
        MeshUploadPatches.StatFallbackCalls = 0;
        UploadBudget.Reset();
        PrioUploadPatches.ResetStats();
        EntityTessPatches.ResetStats();
        EntityLoadPatches.ResetStats();
        MinimapPatches.ResetStats();
        MainThreadTaskPatches.Reset();
        TickProfiler.Reset();
        EntityAnimPatches.ResetStats();
        ServerAllocPatches.ResetStats();
        ClientAllocPatches.ResetStats();
        AllocSampler.ResetStats();
        PacketSourcePatches.ResetStats();
        Measure.GpuBusy.Reset();
        EntitySyncPatches.ResetStats();
        JobScheduler.ResetStats();
        MeshRecyclerPatches.ResetStats();
        TightClonePatches.ResetStats();
        AnimatableCullPatches.ResetStats();
        FastCuller.StatIncInserts = 0;
        FastCuller.StatIncRemovals = 0;
        EdgeRetessPriorityPatches.StatPromoted = 0;
        EdgeRetessPriorityPatches.StatSweeps = 0;
        EdgeRetessPriorityPatches.StatBusySkips = 0;
        PoolReclaimer.Reset();
        RendererProfiler.Reset();
        ModProfiler.Reset();
        FrameStats.Reset();
        HitchLog.Reset();
    }
}
