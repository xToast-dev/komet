using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;

namespace Komet.Measure;

/// <summary>
/// What the OTHER mods in this process cost per frame, and what they do.
///
/// Every attribution this mod has built so far names a part of the ENGINE - a render stage, a
/// renderer, a tick listener, a task code. None of them answers the question a player with
/// forty mods installed actually asks, which is "which of my mods is it". The names in those
/// tables are types, and a type says nothing about who shipped it - unless somebody maps it
/// back to the mod whose assembly it came out of. That map is the whole idea here: a renderer
/// instance, a tick handler's target, a Harmony patch method and a registered block class each
/// name their assembly, and the mod loader knows which mod each assembly belongs to.
///
/// So this class measures nothing of its own. It rides on the two wrappers that already exist
/// (<see cref="Komet.Patches.RendererProfiler"/>, <see cref="Komet.Patches.TickProfiler"/>):
/// each decorator resolves its owning mod once at wrap time and adds its ticks to that mod's
/// bucket as well as to its own. The per-frame price of the entire mod attribution is one
/// field add per measured call - everything else was already being paid by the profilers.
///
/// What it CANNOT see is printed on the HUD rather than quietly left out:
///
///   * Harmony patches. A patch runs INSIDE the engine method it patches, so its time is part
///     of that method's - and there is no honest way to split it out without patching every
///     foreign patch. The patch inventory is therefore reported next to the timings: a mod
///     with thirty transpilers in hot engine methods is a suspect even when its measured
///     share reads zero.
///   * Block entity ticks (the tick profiler deliberately leaves those thousands of listeners
///     alone) and anything a mod runs on its own threads.
///   * GUI dialogs: the engine draws every dialog inside one renderer of its own, so a mod's
///     dialog is booked to the engine's "guimanager".
///
/// The numbers are shares of the main thread, which is what a frame is made of. A mod that
/// reads 0,00 ms here is not proven innocent - it is proven invisible to the four things above.
/// </summary>
public static class ModProfiler
{
    /// <summary>Collect at all. Off means the wrappers skip the extra field add and the
    /// periodic inventory scan does not run; the index stays, so switching it on is instant.</summary>
    public static bool Enabled = true;

    /// <summary>One mod. Assembled from three sources that arrive at different times: the mod
    /// loader's list (identity), the load-phase patch (startup cost, before the index exists),
    /// and the periodic scan (patches, registered classes).</summary>
    public sealed class Entry
    {
        public string ModId;
        /// <summary>Printed in the report next to the id: a bug report that says "xskills 1.4.2"
        /// is worth more than one that says "xskills".</summary>
        public string Version;
        public Assembly Assembly;

        /// <summary>Ships with the game (survival, creative, essentials, the "game" domain).
        /// Kept and shown - vanilla content is often the most expensive thing on the list -
        /// but marked, because uninstalling it is not an option a player has.</summary>
        public bool GameContent;
        /// <summary>Not a mod at all: the engine, the runtime, a library. One shared entry.</summary>
        public bool IsEngine;

        // ---- per frame, fed by the two profilers' decorators ----
        public long RenderTicks;
        public long TickTicks;
        public double RenderMs;
        public double TickMs;
        public double Ms => RenderMs + TickMs;

        /// <summary>Wrapped renderer instances / tick listeners belonging to this mod, counted
        /// on the wrap pass. A mod with 4 000 renderers and 0,3 ms is a different story from
        /// one with a single renderer and the same 0,3 ms.</summary>
        public int Renderers;
        public int Listeners;

        // ---- what it does, from the periodic scan ----
        /// <summary>Distinct methods this mod has Harmony-patched.</summary>
        public int PatchedMethods;
        /// <summary>Of those, the ones that are NOT its own code - engine methods and other
        /// mods'. This is the number that decides whether an invisible mod is harmless.</summary>
        public int PatchedForeign;
        /// <summary>Transpilers among its patches: the kind that rewrites IL rather than
        /// bracketing it, and the kind two mods cannot both apply and stay predictable.</summary>
        public int Transpilers;
        public int Systems;
        public int Blocks, Items, Entities, BlockEntities, Behaviors;
        public int Classes => Blocks + Items + Entities + BlockEntities + Behaviors;

        // ---- what it cost to load, from the mod phase patch ----
        /// <summary>Milliseconds spent in this mod's ModSystem phases on the client - Start,
        /// AssetsLoaded, AssetsFinalize, StartClientSide. This is loading-screen time.</summary>
        public double LoadMs;
        /// <summary>The same on the integrated server, which a single player waits for too.</summary>
        public double LoadServerMs;
        /// <summary>The phase that cost the most, so "4,2 s" says where they went.</summary>
        public string SlowestPhase;
        public double SlowestPhaseMs;

        public double LoadTotalMs => LoadMs + LoadServerMs;
    }

    /// <summary>
    /// Everything that is not a mod: engine, runtime, libraries. Types resolve to this entry so
    /// the hot path never has to distinguish "unknown" from "not indexed yet" - a decorator
    /// always has somewhere to book, and it is never a mod's bucket by accident.
    ///
    /// Deliberately NOT in <see cref="All"/>: it would be the first row of every ranking
    /// forever, and "the engine costs most of the frame" is what the other HUD is for. Komet
    /// itself is not special-cased anywhere - its own renderers and listeners are wrapped like
    /// everyone's and show up as "komet", which is the only honest place for them.
    /// </summary>
    public static readonly Entry Engine = new()
    {
        ModId = "(engine)", Version = "", IsEngine = true, GameContent = true
    };

    private static readonly Dictionary<Assembly, Entry> ByAssembly = new();
    private static readonly Dictionary<Type, Entry> ByType = new();
    private static readonly Dictionary<string, Entry> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Entry> All = new();

    /// <summary>Load timings arrive before the index exists (the phase patch fires while the
    /// mods are still being started, and the client API only appears afterwards), and the
    /// integrated server runs its own loader on its own thread. So they are parked by mod id
    /// under a lock and merged into the entries when the index is built.</summary>
    private static readonly Dictionary<string, (double client, double server, string phase, double phaseMs)> PendingLoad = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadLock = new();

    private const double Alpha = 1.0 / 64.0;
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>Mods the loader reported, and how many of those carry code at all - a content
    /// mod cannot show up in any timing, and saying so keeps a list of thirty zeros honest.</summary>
    public static int ModCount { get; private set; }
    public static int CodeModCount { get; private set; }
    /// <summary>Scans of the Harmony registry and the class registry so far, and when the last
    /// one ran - an inventory nobody has refreshed is a stale inventory.</summary>
    public static int Scans { get; private set; }
    /// <summary>Mod phases measured. Zero means the load patch never fired - which is a
    /// different statement from "no mod took any time", and the report makes it.</summary>
    public static int LoadSamples { get; private set; }
    public static bool Indexed { get; private set; }

    // ---- index ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the assembly -&gt; mod map from the loader's own list. Cheap and idempotent;
    /// called once when the client is up, and again after a scan finds an assembly nobody
    /// knows (a mod that loads code lazily).
    ///
    /// A mod's assembly is not reachable through the public <see cref="Mod"/> surface, so the
    /// concrete <see cref="Vintagestory.Common.ModContainer"/> is asked for it, and its mod
    /// systems' assemblies are registered alongside: a mod may ship several DLLs, and the
    /// container names only the one the loader selected.
    ///
    /// Takes the mod list rather than the loader: the loader is an interface with thirty
    /// members that no test can stand up, and this only ever needed the list.
    /// </summary>
    public static void BuildIndex(IEnumerable<Mod> mods)
    {
        if (mods == null) return;

        // Under the load lock: the integrated server runs its own mod loader on its own thread,
        // and its phase calls read the id map through MergePendingLoad. Building the map while
        // something else walks it is the one race this class can actually have.
        lock (LoadLock)
        {
            ByAssembly.Clear();
            ByType.Clear();
            ById.Clear();
            All.Clear();
            ModCount = 0;
            CodeModCount = 0;

            foreach (var mod in mods)
            {
                if (mod?.Info == null) continue;
                ModCount++;

                var e = new Entry
                {
                    ModId = mod.Info.ModID ?? mod.FileName ?? "?",
                    Version = mod.Info.Version ?? "",
                    Systems = mod.Systems?.Count ?? 0,
                    GameContent = mod.Info.CoreMod,
                };
                    Register(e, (mod as Vintagestory.Common.ModContainer)?.Assembly);
                if (mod.Systems != null)
                    foreach (var system in mod.Systems) Register(e, system?.GetType().Assembly);

                // "with code" means it brought ModSystems, not that an assembly could be resolved:
                // a mod whose DLL the loader took a different route to is still a code mod, and the
                // count is what tells a player how many of thirty mods can appear in a timing at all.
                if (e.Assembly != null || e.Systems > 0) CodeModCount++;
                ById[e.ModId] = e;
                All.Add(e);
            }

            MergeLoadLocked();
            Indexed = true;
        }
    }

    /// <summary>Ties one assembly to a mod. The first assembly seen becomes the entry's own;
    /// the others are aliases, so a second DLL's renderer still books to the right mod.</summary>
    private static void Register(Entry e, Assembly asm)
    {
        if (asm == null || ByAssembly.ContainsKey(asm)) return;
        e.Assembly ??= asm;
        ByAssembly[asm] = e;
    }

    /// <summary>
    /// The mod a type came from - the one call the wrappers make, once per wrapped instance.
    /// Cached per type, because "which assembly, which mod" never changes for a type and the
    /// wrap pass runs several times a second over thousands of renderers.
    ///
    /// An assembly nobody claims becomes an entry of its own, named after the assembly rather
    /// than folded into the engine: that is how a mod that was loaded outside the loader (or
    /// a library one mod ships and another borrows) shows up as itself instead of as vanilla.
    /// </summary>
    public static Entry Of(Type type)
    {
        if (type == null) return Engine;
        if (ByType.TryGetValue(type, out var known)) return known;

        var e = Resolve(type.Assembly);
        ByType[type] = e;
        return e;
    }

    private static Entry Resolve(Assembly asm)
    {
        if (asm == null) return Engine;
        if (ByAssembly.TryGetValue(asm, out var e)) return e;

        var name = asm.GetName().Name ?? "?";
        if (IsEngineAssembly(name))
        {
            ByAssembly[asm] = Engine;
            return Engine;
        }

        // Not the engine, not a mod the loader knows: still name it. Anonymous cost is what
        // this whole class exists to stop.
        var stray = new Entry { ModId = name, Version = asm.GetName().Version?.ToString() ?? "" };
        ByAssembly[asm] = stray;
        All.Add(stray);
        return stray;
    }

    /// <summary>The game's own assemblies plus the runtime's. Everything else is somebody's.</summary>
    private static bool IsEngineAssembly(string name)
        => name.StartsWith("Vintagestory", StringComparison.Ordinal)
           || name.StartsWith("System", StringComparison.Ordinal)
           || name.StartsWith("Microsoft", StringComparison.Ordinal)
           || name == "mscorlib" || name == "netstandard" || name == "0Harmony"
           || name.StartsWith("OpenTK", StringComparison.Ordinal)
           || name == "cairo-sharp" || name == "SkiaSharp" || name == "Newtonsoft.Json"
           || name == "protobuf-net" || name == "Anonymously Hosted DynamicMethods Assembly";

    /// <summary>Whether the index knows this mod id - the report's way of asking without
    /// handing out the entry.</summary>
    public static Entry Find(string modId)
        => modId != null && ById.TryGetValue(modId, out var e) ? e : null;

    // ---- per frame -----------------------------------------------------------------------

    /// <summary>
    /// Folds the render ticks of the frame that just ended. Called from the renderer profiler's
    /// own EndFrame, on measured frames only and before it flips its sampling flag - the mod
    /// buckets are fed by the same decorators, so they have to follow the same cadence or an
    /// unmeasured frame's zero would drag every average down to a quarter of the truth.
    /// </summary>
    public static void FoldRender()
    {
        for (var i = 0; i < All.Count; i++)
        {
            var e = All[i];
            e.RenderMs += (e.RenderTicks * TicksToMs - e.RenderMs) * Alpha;
            e.RenderTicks = 0;
        }
        Engine.RenderMs += (Engine.RenderTicks * TicksToMs - Engine.RenderMs) * Alpha;
        Engine.RenderTicks = 0;
    }

    /// <summary>The same for the game tick's listeners, which are timed on every frame.</summary>
    public static void FoldTick()
    {
        for (var i = 0; i < All.Count; i++)
        {
            var e = All[i];
            e.TickMs += (e.TickTicks * TicksToMs - e.TickMs) * Alpha;
            e.TickTicks = 0;
        }
        Engine.TickMs += (Engine.TickTicks * TicksToMs - Engine.TickMs) * Alpha;
        Engine.TickTicks = 0;
    }

    /// <summary>
    /// Wrapped instances are counted per wrap pass, not incremented per wrap: the pass walks
    /// every renderer and listener that exists, wrapped or not, so counting up from zero each
    /// time is the only way the figure can also go DOWN when a mod unregisters one.
    ///
    /// One method per kind, because the two passes run one after the other (renderers, then
    /// listeners) and a single "clear everything" would wipe whatever the earlier pass counted.
    /// </summary>
    public static void BeginRendererCount()
    {
        for (var i = 0; i < All.Count; i++) All[i].Renderers = 0;
        Engine.Renderers = 0;
    }

    public static void BeginListenerCount()
    {
        for (var i = 0; i < All.Count; i++) All[i].Listeners = 0;
        Engine.Listeners = 0;
    }

    /// <summary>Everything the mods (not the engine) account for, per frame.</summary>
    public static double TotalMs
    {
        get
        {
            double sum = 0;
            for (var i = 0; i < All.Count; i++) sum += All[i].Ms;
            return sum;
        }
    }

    /// <summary>The heaviest mods per frame, most expensive first. The engine's own bucket is
    /// left out - it is not a mod, and it would be the first row of every list forever.</summary>
    public static List<Entry> Top(int count, double minMs = 0.005)
    {
        var all = new List<Entry>(All.Count);
        for (var i = 0; i < All.Count; i++)
            if (All[i].Ms > minMs) all.Add(All[i]);
        all.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        if (all.Count > count) all.RemoveRange(count, all.Count - count);
        return all;
    }

    /// <summary>The slowest mods at load, client and integrated server together.</summary>
    public static List<Entry> TopLoad(int count, double minMs = 20.0)
    {
        var all = new List<Entry>(All.Count);
        for (var i = 0; i < All.Count; i++)
            if (All[i].LoadTotalMs > minMs) all.Add(All[i]);
        all.Sort((a, b) => b.LoadTotalMs.CompareTo(a.LoadTotalMs));
        if (all.Count > count) all.RemoveRange(count, all.Count - count);
        return all;
    }

    /// <summary>The mods that reach furthest into code that is not theirs, most first. This is
    /// the ranking a zero in the timing table does not invalidate.</summary>
    public static List<Entry> TopReach(int count)
    {
        var all = new List<Entry>(All.Count);
        for (var i = 0; i < All.Count; i++)
            if (All[i].PatchedMethods > 0 || All[i].Classes > 0) all.Add(All[i]);
        all.Sort((a, b) =>
        {
            var c = (b.PatchedForeign * 4 + b.PatchedMethods).CompareTo(a.PatchedForeign * 4 + a.PatchedMethods);
            return c != 0 ? c : b.Classes.CompareTo(a.Classes);
        });
        if (all.Count > count) all.RemoveRange(count, all.Count - count);
        return all;
    }

    // ---- load phases ---------------------------------------------------------------------

    /// <summary>
    /// One ModSystem lifecycle phase of one mod, from the phase patch. Called long before the
    /// index exists and from both the client's and the integrated server's loader thread, so it
    /// parks the figure under a lock instead of touching an entry.
    /// </summary>
    public static void NoteLoad(string modId, string phase, double ms, bool serverSide)
    {
        if (string.IsNullOrEmpty(modId)) return;
        lock (LoadLock)
        {
            PendingLoad.TryGetValue(modId, out var cur);
            var client = cur.client + (serverSide ? 0 : ms);
            var server = cur.server + (serverSide ? ms : 0);
            var slowest = cur.phase;
            var slowestMs = cur.phaseMs;
            if (ms > slowestMs) { slowest = phase; slowestMs = ms; }
            PendingLoad[modId] = (client, server, slowest, slowestMs);
            LoadSamples++;
        }
        if (Indexed) MergePendingLoad();
    }

    /// <summary>Copies the parked load figures onto the entries. Idempotent - it assigns, it
    /// does not add, so running it after every phase costs nothing but a few writes.</summary>
    private static void MergePendingLoad()
    {
        lock (LoadLock) MergeLoadLocked();
    }

    /// <summary>The merge itself; the caller holds the lock. Split out because BuildIndex takes
    /// the same lock around the whole rebuild and Monitor reentrancy is not a design.</summary>
    private static void MergeLoadLocked()
    {
        foreach (var kv in PendingLoad)
        {
            if (!ById.TryGetValue(kv.Key, out var e)) continue;
            e.LoadMs = kv.Value.client;
            e.LoadServerMs = kv.Value.server;
            e.SlowestPhase = kv.Value.phase;
            e.SlowestPhaseMs = kv.Value.phaseMs;
        }
    }

    // ---- inventory -----------------------------------------------------------------------

    /// <summary>
    /// Refreshes "what they do": Harmony patches and registered classes, both keyed by the
    /// assembly the patch method or the class type came from.
    ///
    /// Periodic rather than once, for the same reason the patch guard rescans: mods patch
    /// lazily - on first use, on world join - and a mod that patches thirty methods the moment
    /// a player opens a specific block would otherwise read "0 patches" forever.
    ///
    /// The owner is taken from the patch METHOD's assembly, not from the Harmony id: the id is
    /// a free-form string a mod chooses, and several mods use the same conventional one.
    /// </summary>
    public static void ScanInventory()
    {
        if (!Enabled) return;

        for (var i = 0; i < All.Count; i++)
        {
            var e = All[i];
            e.PatchedMethods = 0;
            e.PatchedForeign = 0;
            e.Transpilers = 0;
        }
        Engine.PatchedMethods = Engine.PatchedForeign = Engine.Transpilers = 0;

        var seen = new HashSet<Entry>();
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            HarmonyLib.Patches info;
            try { info = Harmony.GetPatchInfo(method); }
            catch (Exception) { continue; }
            if (info == null) continue;

            var targetOwner = Of(method.DeclaringType);
            seen.Clear();
            CountPatches(info.Prefixes, targetOwner, seen, transpiler: false);
            CountPatches(info.Postfixes, targetOwner, seen, transpiler: false);
            CountPatches(info.Finalizers, targetOwner, seen, transpiler: false);
            CountPatches(info.Transpilers, targetOwner, seen, transpiler: true);
        }

        ScanClassRegistry();
        Scans++;
    }

    /// <summary>Books one kind of patch on one method. A mod that puts three prefixes on the
    /// same method has patched ONE method - the set makes that so.</summary>
    private static void CountPatches(IEnumerable<Patch> patches, Entry targetOwner, HashSet<Entry> seen, bool transpiler)
    {
        foreach (var p in patches)
        {
            var owner = Of(p.PatchMethod?.DeclaringType);
            if (owner.IsEngine) continue;
            if (transpiler) owner.Transpilers++;
            if (!seen.Add(owner)) continue;
            owner.PatchedMethods++;
            if (!ReferenceEquals(owner, targetOwner)) owner.PatchedForeign++;
        }
    }

    /// <summary>
    /// What each mod put into the class registry: blocks, items, entities, block entities and
    /// the various behaviour kinds. The registry is a public static on ClientMain and holds
    /// code -&gt; type maps, so the type's assembly is the mod.
    ///
    /// This is the half of "what they do" that has nothing to do with patching: a content-heavy
    /// mod with 400 block classes explains a slow load and a big heap without ever appearing in
    /// a frame-time table.
    /// </summary>
    private static void ScanClassRegistry()
    {
        var reg = Vintagestory.Client.NoObf.ClientMain.ClassRegistry;
        if (reg == null) return;

        for (var i = 0; i < All.Count; i++)
        {
            var e = All[i];
            e.Blocks = e.Items = e.Entities = e.BlockEntities = e.Behaviors = 0;
        }

        foreach (var kv in reg.BlockClassToTypeMapping) Of(kv.Value).Blocks++;
        foreach (var kv in reg.ItemClassToTypeMapping) Of(kv.Value).Items++;
        foreach (var kv in reg.entityClassNameToTypeMapping) Of(kv.Value).Entities++;
        foreach (var kv in reg.blockEntityClassnameToTypeMapping) Of(kv.Value).BlockEntities++;
        foreach (var kv in reg.blockbehaviorToTypeMapping) Of(kv.Value).Behaviors++;
        foreach (var kv in reg.blockentitybehaviorToTypeMapping) Of(kv.Value).Behaviors++;
        foreach (var kv in reg.entityBehaviorClassNameToTypeMapping) Of(kv.Value).Behaviors++;
        foreach (var kv in reg.collectibleBehaviorToTypeMapping) Of(kv.Value).Behaviors++;
    }

    // ---- report ---------------------------------------------------------------------------

    /// <summary>
    /// The mod block of the full report and of '.komet mods'. English, like every other
    /// diagnostic artefact here: it gets pasted into bug reports and read by people who do not
    /// run the client that produced it. The HUD is the localised surface, not this.
    ///
    /// It prints the caveats with the numbers, every time. A table of milliseconds per mod is
    /// exactly the kind of thing that gets quoted back without them.
    /// </summary>
    public static void Write(StringBuilder sb, CultureInfo ci, double frameMs, bool allRenderers, int count = 8)
    {
        sb.AppendFormat(ci, "mods: {0} loaded, {1} with code, inventory scanned {2}x\n",
            ModCount, CodeModCount, Scans);

        var top = Top(count);
        if (top.Count == 0)
        {
            sb.Append("  per frame: nothing measurable\n");
        }
        else
        {
            sb.Append("  per frame:");
            for (var i = 0; i < top.Count; i++)
            {
                var e = top[i];
                sb.AppendFormat(ci, "{0} {1} {2:F2} (r {3:F2} / t {4:F2}, {5} rend, {6} lst)",
                    i > 0 ? " |" : "", e.ModId, e.Ms, e.RenderMs, e.TickMs, e.Renderers, e.Listeners);
            }
            sb.AppendFormat(ci, "\n  = {0:F2} ms of {1:F2} ms/frame ({2:F0} %)\n",
                TotalMs, frameMs, frameMs > 0 ? 100.0 * TotalMs / frameMs : 0);
        }

        // Without this line the table above is unreadable: with the renderer profiler off,
        // only the Before stage is wrapped at all.
        sb.Append(allRenderers
            ? "  renderer attribution: all stages\n"
            : "  renderer attribution: before stage only ('.komet toggle profiler' for all stages)\n");

        var reach = TopReach(count);
        if (reach.Count > 0)
        {
            sb.Append("  what they do:");
            for (var i = 0; i < reach.Count; i++)
            {
                var e = reach[i];
                sb.AppendFormat(ci, "{0} {1}{2}:", i > 0 ? " |" : "", e.ModId,
                    string.IsNullOrEmpty(e.Version) ? "" : " " + e.Version);
                if (e.PatchedMethods > 0)
                {
                    sb.AppendFormat(ci, " {0} patches", e.PatchedMethods);
                    var detail = "";
                    if (e.PatchedForeign > 0) detail = e.PatchedForeign.ToString(ci) + " on foreign code";
                    if (e.Transpilers > 0)
                        detail += (detail.Length > 0 ? ", " : "") + e.Transpilers.ToString(ci) + " transpiler";
                    if (detail.Length > 0) sb.Append(" (").Append(detail).Append(')');
                }
                // Only the kinds a mod actually registers - a row of zeros says nothing and
                // makes the one number that matters harder to find.
                if (e.Classes > 0)
                {
                    sb.AppendFormat(ci, "{0} {1} classes (", e.PatchedMethods > 0 ? "," : "", e.Classes);
                    var first = true;
                    void Part(int n, string what)
                    {
                        if (n <= 0) return;
                        if (!first) sb.Append(", ");
                        sb.AppendFormat(ci, "{0} {1}", n, what);
                        first = false;
                    }
                    Part(e.Blocks, "block");
                    Part(e.Items, "item");
                    Part(e.Entities, "entity");
                    Part(e.BlockEntities, "block entity");
                    Part(e.Behaviors, "behavior");
                    sb.Append(')');
                }
            }
            sb.Append('\n');
        }

        var load = TopLoad(count);
        if (load.Count > 0)
        {
            sb.Append("  at load:");
            for (var i = 0; i < load.Count; i++)
            {
                var e = load[i];
                sb.AppendFormat(ci, "{0} {1} {2:F2} s", i > 0 ? " |" : "", e.ModId, e.LoadTotalMs / 1000.0);
                if (e.LoadServerMs > 50) sb.AppendFormat(ci, " (server {0:F2} s)", e.LoadServerMs / 1000.0);
                if (e.SlowestPhase != null) sb.AppendFormat(ci, " mostly {0}", e.SlowestPhase);
            }
            sb.AppendFormat(ci, "\n  ({0} phases measured; komet loads at ExecuteOrder 0.05, "
                                + "so phases that ran before it are not in this list)\n", LoadSamples);
        }

        sb.Append("  not attributed: block entity ticks, mod worker threads, gui dialogs, "
                  + "and every harmony patch (a patch's time is booked to the method it patches)\n");
    }

    public static void Reset()
    {
        for (var i = 0; i < All.Count; i++)
        {
            var e = All[i];
            e.RenderTicks = e.TickTicks = 0;
            e.RenderMs = e.TickMs = 0;
        }
        Engine.RenderTicks = Engine.TickTicks = 0;
        Engine.RenderMs = Engine.TickMs = 0;
    }

    /// <summary>Forgets everything, index included - world leave and the verify harness.</summary>
    public static void Clear()
    {
        ByAssembly.Clear();
        ByType.Clear();
        ById.Clear();
        All.Clear();
        lock (LoadLock) PendingLoad.Clear();
        Engine.RenderTicks = Engine.TickTicks = 0;
        Engine.RenderMs = Engine.TickMs = 0;
        ModCount = CodeModCount = Scans = LoadSamples = 0;
        Indexed = false;
    }

    /// <summary>Everything the index holds - the report walks this, the HUD does not.</summary>
    public static IReadOnlyList<Entry> Entries => All;
}
