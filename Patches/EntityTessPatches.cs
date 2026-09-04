using System;
using System.Diagnostics;
using HarmonyLib;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Caps how much main-thread time per frame goes into re-tesselating entity shapes - the
/// measured cause of the look-around stutter.
///
/// SystemRenderEntities.OnBeforeRender calls EntityShapeRenderer.BeforeRender only for
/// entities inside the frustum, and BeforeRender starts with "if (!entity.ShapeFresh)
/// TesselateShape()". So an entity whose shape went stale (or was never built) while
/// off-screen does nothing at all - until the camera turns onto it. Panning across a freshly
/// streamed area then runs the main-thread half of TesselateShape for a whole swath of
/// entities in a single frame: shape clone, StepParentShape for every piece of clothing and
/// armor, texture baking with entity-atlas inserts (a GL upload), behavior handlers. The
/// hitch log measured exactly that: "before 12-39 ms" at 600-1000 grad/s with no GC pause,
/// renderer "Before-ree".
///
/// The fix is a per-frame millisecond budget on EntityShapeRenderer.TesselateShape(). The
/// retry comes for free: a skipped call leaves entity.ShapeFresh false, and the next
/// BeforeRender of a visible entity calls TesselateShape() again - the engine's own lazy
/// path for off-screen entities works the same way. At least one call per frame always goes
/// through, so a backlog drains at worst one entity per frame and cannot starve. The player
/// is never affected: EntityPlayerShapeRenderer overrides TesselateShape() without calling
/// base, so the patch on the base method never sees it.
/// </summary>
public static class EntityTessPatches
{
    public static bool Enabled = true;

    /// <summary>Main-thread milliseconds per frame the shape tesselations may take before
    /// the rest is deferred to the following frames. 0 = vanilla.</summary>
    public static double BudgetMs = 2.0;

    /// <summary>Tesselations that ran / were pushed to a later frame, since start.</summary>
    public static long StatAllowed, StatDeferred;

    /// <summary>Windows that had to reopen themselves because no frame boundary arrived
    /// (see <see cref="StaleAfterMs"/>). Anything but 0 while the client renders means the
    /// boundary is not firing - the report says so instead of hiding it.</summary>
    public static long StatStaleResets;

    /// <summary>
    /// The single most expensive TesselateShape call seen, and whose entity it was.
    ///
    /// The budget's known gap is that the FIRST call of a frame is uncapped (liveness), so
    /// one fat entity - many clothing pieces, cold texture atlas - can still spike a frame
    /// alone. When a join burst shows up as "enttess 60" in the hitch line, this pair names
    /// the entity instead of leaving it at "one of the twenty".
    /// </summary>
    public static double StatWorstMs;
    public static string StatWorstName;

    private static double spentThisFrameMs;
    private static int allowedThisFrame;
    private static long lastBoundaryTs;

    /// <summary>
    /// How long the window may go without a frame boundary before it reopens itself.
    ///
    /// The budget is only ever reset by <see cref="OnFrameBoundary"/>. If that event stops
    /// coming while the patch stays applied, the spent milliseconds never fall back below the
    /// budget and EVERY further TesselateShape is skipped - permanently, for every animated
    /// entity in the world, which reads as "all animals are invisible". A budget that loses
    /// its reset has to degrade to vanilla, not to "never", so a window this old counts as no
    /// window at all. Frames are milliseconds apart; a quarter second is far outside anything
    /// a running client produces, and while none are being drawn an unbudgeted tesselation
    /// costs nothing anyway.
    /// </summary>
    public const double StaleAfterMs = 250.0;

    public static void Apply(Harmony harmony, double budgetMs)
    {
        BudgetMs = budgetMs;

        // VSEssentials is resolved at runtime, like the firepit gate - no compile-time
        // reference to the content mods.
        var esr = AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer")
                  ?? throw new InvalidOperationException("EntityShapeRenderer not found - VSEssentials not loaded?");
        var tess = AccessTools.Method(esr, "TesselateShape", Type.EmptyTypes)
                   ?? throw new InvalidOperationException("EntityShapeRenderer.TesselateShape() not found");

        harmony.Patch(tess,
            prefix: new HarmonyMethod(typeof(EntityTessPatches), nameof(TessPrefix)),
            postfix: new HarmonyMethod(typeof(EntityTessPatches), nameof(TessPostfix)));
    }

    /// <summary>Called on the frame boundary; a fresh frame gets a fresh budget.</summary>
    public static void OnFrameBoundary()
    {
        spentThisFrameMs = 0;
        allowedThisFrame = 0;
        lastBoundaryTs = Stopwatch.GetTimestamp();
    }

    /// <summary>The staleness rule, pure: no boundary within <see cref="StaleAfterMs"/> means
    /// the window is not being reset at all. Zero (none seen yet) counts as stale.</summary>
    internal static bool WindowStale(long nowTs, long lastTs)
        => lastTs == 0 || (nowTs - lastTs) * 1000.0 / Stopwatch.Frequency > StaleAfterMs;

    /// <summary>
    /// The rule, pure so it can be checked directly: the first tesselation of a frame always
    /// runs (liveness - a backlog must drain even with a tiny budget), further ones only
    /// while the budget is not yet spent.
    /// </summary>
    internal static bool ShouldTesselate(double spentMs, int allowedCount, double budgetMs)
        => allowedCount == 0 || spentMs < budgetMs;

    public static bool TessPrefix(out long __state)
    {
        __state = 0;
        if (!Enabled || BudgetMs <= 0) return true;

        // no boundary in a quarter second: the reset is gone, so the window reopens here
        // rather than blocking every tesselation from now on
        var now = Stopwatch.GetTimestamp();
        if (WindowStale(now, lastBoundaryTs))
        {
            spentThisFrameMs = 0;
            allowedThisFrame = 0;
            lastBoundaryTs = now;
            StatStaleResets++;
        }

        if (!ShouldTesselate(spentThisFrameMs, allowedThisFrame, BudgetMs))
        {
            // ShapeFresh stays false, so the next BeforeRender of this entity retries -
            // deferring never loses a tesselation, it only spreads the burst.
            StatDeferred++;
            return false;
        }

        __state = Stopwatch.GetTimestamp();
        return true;
    }

    public static void TessPostfix(long __state, object __instance)
    {
        if (__state == 0) return; // skipped, or the budget is off
        var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        spentThisFrameMs += ms;
        allowedThisFrame++;
        StatAllowed++;

        // feeds the hitch line's "enttess" share - the sub-attribution of the before bucket
        Measure.FrameStats.AddEntityTessMs(ms);

        if (ms > StatWorstMs)
        {
            StatWorstMs = ms;
            // EntityShapeRenderer derives from the public API EntityRenderer, whose entity
            // field is public - no reflection needed, and a null anywhere just loses the name
            StatWorstName = (__instance as Vintagestory.API.Common.Entities.EntityRenderer)
                ?.entity?.Code?.ToShortString();
        }
    }

    public static void ResetStats()
    {
        StatAllowed = StatDeferred = StatStaleResets = 0;
        StatWorstMs = 0;
        StatWorstName = null;
    }
}
