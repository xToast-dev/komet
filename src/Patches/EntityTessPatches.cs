using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

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

    private static double spentThisFrameMs;
    private static int allowedThisFrame;

    public static void Apply(Harmony harmony, double budgetMs)
    {
        BudgetMs = budgetMs;

        // VSEssentials is resolved at runtime, like the firepit gate - no compile-time
        // reference to the content mods.
        Type esr = AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer")
                   ?? throw new InvalidOperationException("EntityShapeRenderer not found - VSEssentials not loaded?");
        MethodInfo tess = AccessTools.Method(esr, "TesselateShape", Type.EmptyTypes)
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
    }

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

    public static void TessPostfix(long __state)
    {
        if (__state == 0) return; // skipped, or the budget is off
        spentThisFrameMs += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        allowedThisFrame++;
        StatAllowed++;
    }
}
