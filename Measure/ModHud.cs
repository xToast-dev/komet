using System;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;

namespace Komet.Measure;

/// <summary>
/// The second overlay: not "where does the frame go" but "which mod is it".
///
/// A separate box rather than another section of the performance HUD, and that is deliberate.
/// The performance HUD answers a question about the ENGINE and is what every measurement in
/// this project is screenshotted from; its compact view exists because a player wants six rows,
/// not thirty. Mod attribution is a different question asked at a different moment ("I installed
/// eleven things and it got worse - which one"), it is only meaningful while the renderer
/// attribution is armed, and it wants the screen's other corner so both can be read at once.
///
/// Everything below the text is inherited: the same off-thread cairo raster, the same adaptive
/// cadence, the same rule that an overlay reporting stutter must not cause any. Shift+F7
/// cycles it off -> compact -> full, exactly like F7 does for the performance HUD - the shifted
/// variant of the key this mod already owns, because the keys that look free are not (F6 was
/// tried and is a minimap macro). '.komet mods hud' does the same for anyone who prefers it.
/// </summary>
public sealed class ModHud : DebugHud
{
    /// <summary>Whether the renderer profiler is wrapping every stage or only the Before one.
    /// The difference decides what this HUD can see at all, so it is a parameter of the text
    /// rather than something the reader is expected to remember.</summary>
    public Func<bool> AllRenderersWrapped = () => false;

    public ModHud(ICoreClientAPI capi, string title) : base(capi, title)
    {
        // The performance HUD owns the right edge; two boxes there would overlap.
        AnchorLeft = true;
    }

    /// <summary>Nothing to sample: this overlay reads the profiler's buckets, and those are
    /// folded at the frame boundary whether or not anybody is looking.</summary>
    protected override void SampleWorld()
    {
    }

    protected override string ComposeText()
        => ComposeMods(Title, Compact, FrameStats.AvgFrameMs, AllRenderersWrapped());

    /// <summary>
    /// The whole text, pure - no GL, no engine, no clock. The verify harness composes it from
    /// a hand-built index, which is the only way to check a table that in the game depends on
    /// which mods somebody happens to have installed.
    /// </summary>
    public static string ComposeMods(string title, bool compact, double frameMs, bool allRenderers)
    {
        var sb = new StringBuilder(compact ? 400 : 1200);
        var ci = CultureInfo.CurrentCulture;

        sb.Append(title).Append('\n');

        // Off in the config means there is no index and never will be one in this session -
        // "indexing mods ..." would sit there forever looking like a hang.
        if (!ModProfiler.Enabled)
        {
            sb.Append(Loc.T("komet:hud-mods-off", " mod profiling is off (ProfileMods in komet.json)"));
            return sb.ToString();
        }
        if (!ModProfiler.Indexed)
        {
            sb.Append(Loc.T("komet:hud-mods-indexing", " indexing mods ..."));
            return sb.ToString();
        }

        Row(sb, Loc.Hud("mods"), N(ModProfiler.ModCount), null,
            Loc.T("komet:hud-mods-with-code", "{0} of them with code", ModProfiler.CodeModCount));

        // ---- what they cost, per frame ----
        Section(sb, Loc.Hud("per frame"));
        var top = ModProfiler.Top(compact ? 5 : 10);
        if (top.Count == 0)
        {
            // An empty table and a broken one look the same; say which this is.
            Row(sb, Loc.Hud("mods"), null, null, Loc.T("komet:hud-mods-none", "nothing measurable"));
        }
        else
        {
            foreach (var e in top) CostRow(sb, e, frameMs);
            Row(sb, "= " + Loc.Hud("all together"), Pct(ModProfiler.TotalMs, frameMs),
                Ms(ModProfiler.TotalMs), Bar(ModProfiler.TotalMs, frameMs));
        }

        // The caveat that decides how to read every row above it, in both views: with the
        // renderer profiler off, only the Before stage is wrapped, so a mod's renderers in
        // opaque, oit and ortho are simply not in these numbers.
        if (!allRenderers)
            sb.Append(Loc.T("komet:hud-mods-before-only",
                " renderers: before stage only - '.komet toggle profiler'\n"));

        if (compact)
        {
            sb.Append(Loc.T("komet:hud-mods-hint", " Shift+F7: details, again: off"));
            return sb.ToString();
        }

        // ---- what they do, whatever they cost ----
        // A mod can read 0,00 ms above and still be the reason the frame looks like this: a
        // patch runs inside the engine method it rewrites, and its time is booked there.
        var reach = ModProfiler.TopReach(8);
        if (reach.Count > 0)
        {
            Section(sb, Loc.Hud("what they do"));
            foreach (var e in reach)
            {
                var tail = new StringBuilder(48);
                if (e.PatchedMethods > 0)
                    tail.Append(Loc.T("komet:hud-mods-patches", "{0} patches", e.PatchedMethods));
                if (e.PatchedForeign > 0)
                    tail.Append(Loc.T("komet:hud-mods-foreign", " ({0} foreign)", e.PatchedForeign));
                if (e.Transpilers > 0)
                    tail.Append(Loc.T("komet:hud-mods-transpilers", " · {0} transpiler", e.Transpilers));
                if (e.Classes > 0)
                {
                    if (tail.Length > 0) tail.Append(" · ");
                    tail.Append(Loc.T("komet:hud-mods-classes", "{0} classes", e.Classes));
                }
                Row(sb, Label(e), Instances(e), null, tail.ToString());
            }
        }

        // ---- what they cost once, at load ----
        var load = ModProfiler.TopLoad(6);
        if (load.Count > 0)
        {
            Section(sb, Loc.Hud("at load"));
            foreach (var e in load)
            {
                var tail = e.SlowestPhase != null
                    ? Loc.T("komet:hud-mods-phase", "mostly {0} ({1} s)", e.SlowestPhase,
                        (e.SlowestPhaseMs / 1000.0).ToString("F1", ci))
                    : null;
                Row(sb, Label(e), (e.LoadTotalMs / 1000.0).ToString("F1", ci) + " s",
                    null, tail);
            }
        }

        // ---- and what none of it covers ----
        Section(sb, Loc.Hud("not attributed"));
        sb.Append(Loc.T("komet:hud-mods-gaps",
            " block entity ticks · mod threads · gui dialogs\n patched engine code (runs inside the engine's own)\n"));

        sb.Append(Loc.T("komet:hud-mods-hint", " Shift+F7: details, again: off"));
        return sb.ToString();
    }

    /// <summary>One mod's share of the frame: percent, milliseconds, bar, and which of the two
    /// measured sources it came from - a mod that is all renderer is a different problem from
    /// one that is all game tick.</summary>
    private static void CostRow(StringBuilder sb, ModProfiler.Entry e, double frameMs)
    {
        string source;
        if (e.RenderMs > 0.005 && e.TickMs > 0.005) source = Loc.Hud("render") + "+" + Loc.Hud("tick");
        else if (e.TickMs > e.RenderMs) source = Loc.Hud("tick");
        else source = Loc.Hud("render");

        Row(sb, Label(e), Pct(e.Ms, frameMs), Ms(e.Ms),
            Bar(e.Ms, frameMs).PadRight(11) + source);
    }

    /// <summary>The mod id, cut to the label column. Game content is marked: it is on the list
    /// because it costs something, not because a player could remove it.</summary>
    private static string Label(ModProfiler.Entry e)
    {
        var id = e.ModId ?? "?";
        if (e.GameContent && id.Length <= 11) id = "*" + id;
        return id.Length > 13 ? id.Substring(0, 13) : id;
    }

    /// <summary>Wrapped instances, in the value column: "8/2" is eight renderers and two tick
    /// listeners. Blank when the mod has neither - most content mods.</summary>
    private static string Instances(ModProfiler.Entry e)
        => e.Renderers == 0 && e.Listeners == 0 ? null : e.Renderers + "/" + e.Listeners;
}
