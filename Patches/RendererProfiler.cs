using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Attributes each render stage's time to the individual renderers inside it.
///
/// The stage breakdown says "opaque costs 3.4 ms" but not who spends it, and a stage holds
/// everything from the terrain renderer to particles, entities, weather and any other mod's
/// renderer. Every conclusion drawn from stage totals alone in this project needed a
/// correction afterwards; the first working per-renderer measurement found the frame's
/// biggest item immediately (a firepit renderer), and it was never in the terrain.
///
/// Each renderer is wrapped in a timing decorator. Wrapping rather than patching the dispatch
/// loop is deliberate: the first attempt patched ClientEventManager.TriggerRenderStage, which
/// applied cleanly, logged "enabled" - and recorded nothing, because the JIT had already
/// inlined the dispatch call into the (itself patched) caller. Harmony cannot reach an
/// inlined copy, and a patch that applies but is never called looks exactly like one that
/// works. Substituting the objects in the list cannot be optimised away.
///
/// History note: this class once also carried a distance gate that skipped far-away block
/// entity renderers. It was removed after runtime bisection proved it caused world glitches
/// (skipped renderers leave different GL state for their successors, and which ones are
/// skipped changes with every camera move) - and after the ghost-renderer fix revealed that
/// most of the cost it fought had been ghosts, not real renderers: the firepit renderer
/// measured 8.14 ms with ghosts accumulating and 0.61 ms once they were fixed.
/// </summary>
public static class RendererProfiler
{
    public static bool Enabled;

    /// <summary>
    /// Keep the Before stage's renderers wrapped and timed EVERY frame even while the full
    /// profiler is off.
    ///
    /// The full profiler is off by default because it wraps ~10 000 block entity renderers;
    /// the Before stage holds about nine system renderers (entities, chunk uploads, the
    /// liquid depth pass, camera, ambient - plus whatever other mods register), so always-on
    /// costs a few microseconds and answers the one attribution gap the hitch log still had:
    /// repeated 60-87 ms "before" bursts at world join with no renderer name attached,
    /// suspected for weeks and never nameable because naming them required remembering to
    /// arm the profiler before joining a world.
    /// </summary>
    public static bool AttributeBeforeStage = true;

    private sealed class Entry
    {
        public long Ticks;      // accumulated in the current frame
        public double Ms;       // smoothed, published per frame
        public EnumRenderStage Stage;
        /// <summary>Before-stage buckets are timed and folded every frame, not sampled -
        /// the hitch log reads per-frame ticks, and a hitch does not wait to be sampled.</summary>
        public bool EveryFrame;
    }

    /// <summary>
    /// Wrapped-instance count per stage. The unregister fix scans a stage's renderer list
    /// linearly to find a wrapper; with only the Before stage wrapped, an unregistering
    /// block entity (thousands of entries in the Opaque list, one per chunk unload) must
    /// not pay that scan for a wrapper that cannot exist there.
    /// </summary>
    private static readonly int[] wrappedByStage = new int[32];

    private static readonly Dictionary<string, Entry> Entries = new(64);
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>Smoothing to match the rest of the HUD - roughly half a second of history.</summary>
    private const double Alpha = 1.0 / 64.0;

    /// <summary>
    /// Measure one frame in this many, rather than every frame.
    ///
    /// Two Stopwatch reads cost around 40 ns, which is nothing until you notice there are
    /// ~10 000 renderer instances registered at view distance 1536 - then it is 0.4 ms of
    /// every frame spent measuring, more than several of the things being measured. The
    /// figures are smoothed over dozens of frames anyway, so sampling every fourth one
    /// reports the same averages at a quarter of the cost.
    /// </summary>
    private const int SampleEveryNthFrame = 4;

    private static int frameIndex;
    private static bool measuringThisFrame = true;

    /// <summary>Renderer instances currently wrapped, and how many exist in total.</summary>
    public static int StatWrapped;
    public static int StatTotal;

    /// <summary>
    /// Wraps every registered renderer in a timing decorator.
    ///
    /// Re-run periodically: the engine and other mods register renderers throughout a session,
    /// and anything registered after the last pass simply goes unmeasured until the next one.
    /// </summary>
    public static void Wrap(ClientEventManager manager)
    {
        if (manager?.renderersByStage == null) return;

        // With the full profiler off, only the Before stage is touched - both for the wrap
        // itself and for the periodic re-wrap pass, which would otherwise walk thousands of
        // block entity entries four times a second for nothing.
        int first = 0, last = manager.renderersByStage.Length - 1;
        if (!Enabled)
        {
            if (!AttributeBeforeStage) return;
            first = last = (int)EnumRenderStage.Before;
        }

        int total = 0, wrapped = 0;
        for (var stage = first; stage <= last; stage++)
        {
            var list = manager.renderersByStage[stage];
            if (list == null) continue;

            var wrappedHere = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var handler = list[i];
                if (handler?.Renderer == null) continue;
                total++;
                if (handler.Renderer is Timed) { wrapped++; wrappedHere++; continue; }

                handler.Renderer = new Timed(handler.Renderer, handler.ProfilingName ?? "?", (EnumRenderStage)stage);
                wrapped++;
                wrappedHere++;
            }
            if (stage < wrappedByStage.Length) wrappedByStage[stage] = wrappedHere;
        }

        StatTotal = total;
        StatWrapped = wrapped;
    }

    /// <summary>
    /// Lets a wrapped renderer still unregister itself.
    ///
    /// ClientEventManager.UnregisterRenderer finds the entry to remove by reference:
    /// <c>x.Renderer == handler</c>. Once a renderer is wrapped, the list holds the Timed
    /// decorator instead of the original, so a block entity calling UnregisterRenderer(this)
    /// silently removes nothing. The ghost stays registered while its owner disposes its
    /// meshes - and the next frame crashes with "Trying to render a disposed mesh"
    /// (PotInFirepitRenderer, in the wild). Chunks unload constantly while flying, so these
    /// ghosts accumulated for as long as the wrapping existed - and inflated every renderer
    /// cost measured before the fix.
    ///
    /// The prefix rewrites the argument to the wrapper that actually sits in the list, and
    /// vanilla's own removal then works unchanged.
    /// </summary>
    public static void ApplyUnregisterFix(HarmonyLib.Harmony harmony)
    {
        var unreg = HarmonyLib.AccessTools.Method(typeof(ClientEventManager),
                        nameof(ClientEventManager.UnregisterRenderer),
                        [typeof(IRenderer), typeof(EnumRenderStage)])
                    ?? throw new InvalidOperationException("ClientEventManager.UnregisterRenderer not found");

        harmony.Patch(unreg, prefix: new HarmonyLib.HarmonyMethod(
            typeof(RendererProfiler).GetMethod(nameof(ResolveWrapper))));
    }

    /// <summary>Swaps the argument for the decorator holding it, if there is one.</summary>
    public static void ResolveWrapper(ClientEventManager __instance, ref IRenderer handler, EnumRenderStage stage)
    {
        // The scan below is linear in the stage's renderer list, which at view distance 1536
        // holds thousands of entries, and every unloading block entity unregisters itself - so
        // with nothing wrapped this would be a per-chunk-unload cost for no purpose at all.
        // Keyed on wrappers existing rather than on Enabled: switching the profiler off and
        // unwrapping are two steps, and a renderer unregistering between them still has to
        // find its decorator or it becomes a ghost.
        if (StatWrapped == 0) return;
        if (handler == null || handler is Timed) return;
        // A stage with nothing wrapped cannot contain the decorator - and with only the
        // Before stage attributed, this is what spares every unloading block entity the
        // linear scan of its thousands-long Opaque list.
        if ((int)stage < wrappedByStage.Length && wrappedByStage[(int)stage] == 0) return;

        var list = __instance.renderersByStage?[(int)stage];
        if (list == null) return;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i]?.Renderer is Timed timed && ReferenceEquals(timed.Inner, handler))
            {
                handler = timed;
                return;
            }
        }
    }

    /// <summary>
    /// Puts the original renderers back, so unloading the mod - or switching the profiler off -
    /// leaves no trace. StatWrapped only drops to zero once every decorator is really gone,
    /// because that flag is what keeps the unregister fix alive in the meantime.
    /// </summary>
    public static void Unwrap(ClientEventManager manager, bool keepBeforeAttribution = false)
    {
        Enabled = false;
        if (manager?.renderersByStage == null) return;

        var kept = 0;
        for (var stage = 0; stage < manager.renderersByStage.Length; stage++)
        {
            var list = manager.renderersByStage[stage];
            if (list == null) continue;
            if (keepBeforeAttribution && stage == (int)EnumRenderStage.Before)
            {
                // the always-on attribution survives a profiler toggle-off; only the full
                // teardown (world leave) takes these out too
                for (var i = 0; i < list.Count; i++)
                    if (list[i]?.Renderer is Timed) kept++;
                continue;
            }
            for (var i = 0; i < list.Count; i++)
                if (list[i]?.Renderer is Timed timed) list[i].Renderer = timed.Inner;
            if (stage < wrappedByStage.Length) wrappedByStage[stage] = 0;
        }

        // StatWrapped keeps the unregister fix armed exactly while any decorator exists.
        StatWrapped = kept;
        StatTotal = kept;
    }

    /// <summary>Forwards everything, and times OnRenderFrame on sampled frames.</summary>
    internal sealed class Timed : IRenderer
    {
        internal readonly IRenderer Inner;

        /// <summary>
        /// The bucket this renderer books into, resolved once at wrap time.
        ///
        /// It used to be a dictionary lookup on the profiling name per renderer per measured
        /// frame. Hundreds of firepits share one name, and at view distance 1536 there are
        /// thousands of wrapped instances, so that was thousands of string hashes and probes
        /// on every sampled frame - for a figure that is then smoothed over dozens of frames.
        /// The name never changes, so neither does the bucket.
        /// </summary>
        private readonly Entry entry;

        public Timed(IRenderer inner, string name, EnumRenderStage stage)
        {
            Inner = inner;
            entry = Bucket(name, stage);
        }

        public double RenderOrder => Inner.RenderOrder;
        public int RenderRange => Inner.RenderRange;

        public void OnRenderFrame(float dt, EnumRenderStage renderStage)
        {
            // Before-stage buckets are timed every frame: the hitch log reads the raw
            // per-frame ticks at detection time, and a hitch on an unsampled frame would
            // otherwise go unnamed - which for the world-join "before" bursts was three
            // out of four of them.
            if (!measuringThisFrame && !entry.EveryFrame)
            {
                Inner.OnRenderFrame(dt, renderStage);
                return;
            }

            var t0 = Stopwatch.GetTimestamp();
            Inner.OnRenderFrame(dt, renderStage);
            entry.Ticks += Stopwatch.GetTimestamp() - t0;
        }

        public void Dispose() => Inner.Dispose();
    }

    /// <summary>
    /// The shared bucket for a profiling name. Called from Wrap on the main thread only, which
    /// is what makes the plain dictionary safe.
    /// </summary>
    private static Entry Bucket(string name, EnumRenderStage stage)
    {
        name ??= "?";
        if (!Entries.TryGetValue(name, out var e))
            Entries[name] = e = new Entry { Stage = stage, EveryFrame = stage == EnumRenderStage.Before };
        return e;
    }

    /// <summary>
    /// Folds the frame that just ended into the smoothed values, and decides whether the next
    /// one is measured. Only measured frames are blended - an unmeasured frame has no ticks,
    /// and folding its zero in would drag every average towards nothing.
    /// </summary>
    public static void EndFrame()
    {
        foreach (var kv in Entries)
        {
            var e = kv.Value;
            // sampled buckets fold only on measured frames (an unmeasured frame has no
            // ticks, and folding its zero in would drag the average towards nothing);
            // every-frame buckets have real ticks every frame and fold every frame
            if (!measuringThisFrame && !e.EveryFrame) continue;
            var ms = e.Ticks * TicksToMs;
            e.Ms += (ms - e.Ms) * Alpha;
            e.Ticks = 0;
        }

        measuringThisFrame = ++frameIndex % SampleEveryNthFrame == 0;
    }

    /// <summary>
    /// The most expensive renderer of the frame that is currently being closed out - raw
    /// per-frame ticks, not the smoothed averages. Only meaningful between the frame boundary
    /// (where the hitch log asks) and EndFrame (which folds and clears the ticks); on the
    /// three of four frames the profiler skips, everything is zero and this returns null.
    /// </summary>
    public static (string name, double ms)? TopOfCurrentFrame()
    {
        string bestName = null;
        long bestTicks = 0;
        foreach (var kv in Entries)
        {
            if (kv.Value.Ticks > bestTicks)
            {
                bestTicks = kv.Value.Ticks;
                bestName = kv.Key;
            }
        }
        if (bestName == null) return null;
        return (bestName, bestTicks * TicksToMs);
    }

    /// <summary>
    /// Distinct profiling names, not renderer instances - hundreds of firepits all report as
    /// one "Opaque-firepit". StatWrapped is the instance count.
    /// </summary>
    public static int Count => Entries.Count;

    /// <summary>
    /// Everything the renderers together account for. Against the stage totals this says
    /// whether the list explains the frame or whether something is still hiding.
    /// </summary>
    public static double TotalMs
    {
        get
        {
            double sum = 0;
            foreach (var kv in Entries) sum += kv.Value.Ms;
            return sum;
        }
    }

    /// <summary>The heaviest renderers, most expensive first.</summary>
    public static List<(string name, EnumRenderStage stage, double ms)> Top(int count)
    {
        var all = new List<(string, EnumRenderStage, double)>(Entries.Count);
        foreach (var kv in Entries)
            if (kv.Value.Ms > 0.005) all.Add((kv.Key, kv.Value.Stage, kv.Value.Ms));

        all.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        if (all.Count > count) all.RemoveRange(count, all.Count - count);
        return all;
    }

    public static void Write(StringBuilder sb, int count)
    {
        var top = Top(count);
        if (top.Count == 0)
        {
            // An empty section is indistinguishable from a broken one - which is exactly how
            // the first version of this failed, silently.
            Measure.DebugHud.Row(sb, "(sammelt)", Entries.Count + " renderer");
            return;
        }

        foreach ((var name, var stage, var ms) in top)
        {
            var label = name.Length > 13 ? name.Substring(0, 13) : name;
            Measure.DebugHud.Row(sb, label, null, Measure.DebugHud.Ms(ms), stage.ToString().ToLowerInvariant());
        }
    }

    public static void Reset()
    {
        foreach (var kv in Entries) { kv.Value.Ticks = 0; kv.Value.Ms = 0; }
    }
}
