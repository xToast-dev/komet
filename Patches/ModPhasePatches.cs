using System;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Common;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// What each mod costs at load: the loading screen, attributed.
///
/// "It takes two minutes to get into a world" is the second thing people say about a heavily
/// modded install, right after "it stutters", and nothing in the client answers it. The log
/// prints the phases the loader runs, not what each mod spent in them, and the per-frame
/// attribution this mod does elsewhere cannot see load time at all - by the time there is a
/// frame, loading is over.
///
/// Every ModSystem lifecycle call goes through one private method,
/// <c>ModLoader.TryRunModPhase(mod, system, api, phase)</c>: Start, AssetsLoaded,
/// AssetsFinalize, StartClientSide (the "Normal" phase) and Dispose, once per system per mod.
/// A prefix/postfix pair around it is therefore the whole measurement - a few hundred calls
/// per session, two Stopwatch reads each.
///
/// It is patched from Komet's own Start, which itself runs inside one of these calls: Komet
/// loads at ExecuteOrder 0.05, so everything after it in the Start phase and every later phase
/// of every mod is measured. Komet's own Start is not, and neither is anything that ran before
/// it - the report says so rather than pretending the table is complete.
///
/// The integrated server runs its own loader on its own thread, and a single player waits for
/// that just as much as for the client's, so both sides are booked - separately, because
/// "which of these two waits is it" is the next question.
/// </summary>
public static class ModPhasePatches
{
    public static bool Enabled = true;

    public static void Apply(Harmony harmony)
    {
        var run = AccessTools.Method(typeof(ModLoader), "TryRunModPhase",
                      [typeof(Mod), typeof(ModSystem), typeof(ICoreAPI), typeof(ModRunPhase)])
                  ?? throw new InvalidOperationException("ModLoader.TryRunModPhase not found");

        harmony.Patch(run,
            prefix: new HarmonyMethod(typeof(ModPhasePatches), nameof(PhasePrefix)),
            postfix: new HarmonyMethod(typeof(ModPhasePatches), nameof(PhasePostfix)));
    }

    public static void PhasePrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void PhasePostfix(Mod mod, ICoreAPI api, ModRunPhase phase, long __state)
    {
        if (!Enabled || mod?.Info == null) return;
        var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        // The count of measured phases lives with the figures, in ModProfiler.LoadSamples.
        Measure.ModProfiler.NoteLoad(mod.Info.ModID, NameOf(phase), ms,
            serverSide: api != null && api.Side == EnumAppSide.Server);
    }

    /// <summary>The phase as a player-sized word. "Normal" is the engine's name for the one
    /// everybody knows as StartClientSide, and printing "normal" in a table of load times would
    /// be the least informative row in it.</summary>
    private static string NameOf(ModRunPhase phase) => phase switch
    {
        ModRunPhase.Pre => "startpre",
        ModRunPhase.Start => "start",
        ModRunPhase.AssetsLoaded => "assets",
        ModRunPhase.AssetsFinalize => "assetsfinal",
        ModRunPhase.Normal => "startside",
        ModRunPhase.Dispose => "dispose",
        _ => phase.ToString().ToLowerInvariant()
    };
}
