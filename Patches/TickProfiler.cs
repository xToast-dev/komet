using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Vintagestory.Common;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Attributes the game tick to its listeners, the way the renderer profiler attributes stages.
///
/// The client's game tick is one bucket ("tick 12,7") owned by a few dozen system listeners
/// (entities, weather, HUD elements, sounds, mods) plus every block entity's tick. When it
/// hitches - and it does, 54 of 155 hitches in one field report, every one next to a GC
/// pause of about the same size - nothing says which listener allocated its way into that
/// pause. This wraps each entry of EventManager.GameTickListenersEntity in a timing
/// delegate: the frame keeps every listener's ticks, the hitch log asks for the top one at
/// detection time, and a smoothed per-listener table feeds the report.
///
/// Same lesson as the renderer profiler: substituting the delegate cannot be optimised away,
/// and UnregisterGameTickListener works by listener id, so identity is not an issue. Block
/// listeners (GameTickListenersBlock, one per ticking block entity, thousands at view
/// distance 1536) are deliberately NOT wrapped; their share is what the tick total does not
/// explain. The engine's own tick profiler (extendedDebugInfo) names listeners by the
/// handler's target type, which a wrapper would hide - so while that profiler is on, this
/// one unwraps and stands down.
/// </summary>
public static class TickProfiler
{
    public static bool Enabled = true;

    /// <summary>The timing decorator; one instance per wrapped listener.</summary>
    internal sealed class Timed
    {
        internal readonly Action<float> Inner;
        private readonly Measure.MsLedger.Entry entry;

        /// <summary>The mod whose code this listener is, resolved once at wrap time - see
        /// RendererProfiler.Timed.Mod for why it is not a per-call lookup.</summary>
        internal readonly Measure.ModProfiler.Entry Mod;

        public Timed(Action<float> inner, Measure.MsLedger.Entry entry, Measure.ModProfiler.Entry mod)
        {
            Inner = inner;
            this.entry = entry;
            Mod = mod;
        }

        public void Invoke(float dt)
        {
            var t0 = Stopwatch.GetTimestamp();
            Inner(dt);
            var spent = Stopwatch.GetTimestamp() - t0;
            entry.Ticks += spent;
            entry.Calls++;
            Mod.TickTicks += spent;
        }
    }

    private static readonly Measure.MsLedger Ledger = new();

    private static readonly AccessTools.FieldRef<EventManager, List<GameTickListener>> Listeners =
        AccessTools.FieldRefAccess<EventManager, List<GameTickListener>>("GameTickListenersEntity");

    /// <summary>Listeners currently wrapped / present.</summary>
    public static int StatWrapped, StatTotal;

    /// <summary>
    /// Wraps every listener that is not wrapped yet. Re-run periodically: listeners register
    /// throughout a session (mods, HUD elements opening), and the engine nulls a slot on
    /// unregister rather than removing it.
    /// </summary>
    public static void Wrap(EventManager manager)
    {
        if (manager == null) return;
        var list = Listeners(manager);
        if (list == null) return;
        int total = 0, wrapped = 0;
        // recounted from zero every pass, like the renderer side
        var countMods = Measure.ModProfiler.Enabled;
        if (countMods) Measure.ModProfiler.BeginListenerCount();
        for (var i = 0; i < list.Count; i++)
        {
            var l = list[i];
            var h = l?.Handler;
            if (h == null) continue;
            total++;
            if (h.Target is Timed already)
            {
                if (countMods) already.Mod.Listeners++;
                wrapped++;
                continue;
            }
            var mod = Measure.ModProfiler.Of(OwnerOf(h));
            if (countMods) mod.Listeners++;
            l.Handler = new Timed(h, Ledger.Bucket(NameOf(h)), mod).Invoke;
            wrapped++;
        }
        StatTotal = total;
        StatWrapped = wrapped;
    }

    /// <summary>Puts the original handlers back. Leaves no trace.</summary>
    public static void Unwrap(EventManager manager)
    {
        if (manager == null) return;
        var list = Listeners(manager);
        if (list == null) return;
        for (var i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (l?.Handler?.Target is Timed t) l.Handler = t.Inner;
        }
        StatWrapped = 0;
    }

    /// <summary>"ClientSystemEntities.OnGameTick" - the target's type and the method, with the
    /// compiler's lambda mangling reduced to something a human can read.</summary>
    internal static string NameOf(Action<float> h)
    {
        var type = h.Target?.GetType() ?? h.Method.DeclaringType;
        var typeName = type?.Name ?? "?";
        // closures live in nested "<>c__DisplayClass" types; name them after the outer type
        if (typeName.StartsWith("<>") && type?.DeclaringType != null) typeName = type.DeclaringType.Name;
        var method = h.Method.Name;
        if (method.StartsWith('<'))
        {
            var end = method.IndexOf('>');
            method = end > 1 ? method.Substring(1, end - 1) + "()" : "lambda";
        }
        return typeName + "." + method;
    }

    /// <summary>The type a listener's code lives in: the closure's or the object's, and for a
    /// lambda in a nested display class the type that declares it. Same resolution the name
    /// uses, so the table and the mod attribution can never disagree about who this is.</summary>
    internal static Type OwnerOf(Action<float> h)
    {
        var type = h.Target?.GetType() ?? h.Method.DeclaringType;
        if (type != null && type.Name.StartsWith("<>", StringComparison.Ordinal) && type.DeclaringType != null)
            type = type.DeclaringType;
        return type;
    }

    /// <summary>Folds the finished frame into the averages, and the mod profiler's with it.</summary>
    public static void EndFrame()
    {
        Ledger.EndFrame();
        // the same every-frame fold, for the same reason: a mod that ticked nothing this frame
        // is a real zero, not a stale average
        Measure.ModProfiler.FoldTick();
    }

    /// <summary>The frame's most expensive listener - valid only between the frame boundary's
    /// hitch detection and EndFrame, like the renderer profiler's.</summary>
    public static (string name, double ms)? TopOfCurrentFrame() => Ledger.TopOfCurrentFrame();

    public static List<(string name, double ms, long calls)> Top(int count) => Ledger.Top(count);

    /// <summary>Everything the wrapped listeners account for per frame; against the tick
    /// total, the remainder is block entity ticks and delayed callbacks.</summary>
    public static double TotalMs => Ledger.TotalMs;

    public static int Count => Ledger.Count;

    public static void Write(StringBuilder sb, int count, CultureInfo ci, double tickMs)
    {
        var top = Top(count);
        sb.AppendFormat(ci, "tick-listener: {0} gewickelt, {1:F2} von {2:F2} ms/frame erklaert (rest = block-entities, delayed callbacks)",
            StatWrapped, TotalMs, tickMs);
        if (top.Count > 0)
        {
            sb.Append(" | teuerste: ");
            for (var i = 0; i < top.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(ci, "{0} {1:F2} ms", top[i].name, top[i].ms);
            }
        }
        sb.Append('\n');
    }

    public static void Reset() => Ledger.Reset();
}
