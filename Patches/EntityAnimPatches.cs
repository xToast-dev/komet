using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Attributes the entity renderer's Before stage - and animates far entities less often.
///
/// SystemRenderEntities.OnBeforeRender ("Before-ree") walks every loaded entity each frame:
/// the frustum test, EntityRenderer.BeforeRender for the visible ones (shape tesselation,
/// light lookups), then AnimManager.OnClientFrame for ALL of them - which runs the animator
/// (per-joint matrix math, the heaviest per-entity CPU cost there is) for every entity that
/// is rendered, shadow-rendered, or dead. The 02.09. field report had three hitches at
/// "before 17-20 ms | renderer Before-ree 17-19 ms" with only 1,2-1,5 ms of it entity
/// tesselation and no name for the rest.
///
/// This replaces the loop with a 1:1 transcription that clocks the two halves separately
/// (vor-render / anim), counts the entities in each, and keeps the frame's most expensive
/// single entity - the hitch line then says "entities vor-render 2,1 ms, anim 9,3 ms/188
/// (top wolf-eurasian-adult-male 1,2)" and the report carries the smoothed split.
///
/// The optimisation on top: an entity whose shadow is the only thing rendered of it (it is
/// outside the view frustum but inside the shadow frustum, which at 255 blocks of shadow
/// range is most loaded entities) has its animation advanced every third frame, a rendered
/// entity beyond <see cref="FarBlocks"/> every second frame - each time with the skipped
/// frames' dt folded in, so animation TIME runs exactly as before, only sampled less often.
/// The own player, near entities and dead ones (their death animation must finish) are
/// always at full rate. Turns are spread by entity id, so the skipped work is even across
/// frames instead of every third frame being cheap.
/// </summary>
public static class EntityAnimPatches
{
    /// <summary>The transcription (attribution) - off = vanilla's loop, untouched.</summary>
    public static bool Enabled = true;

    /// <summary>The reduced animation rate for far / shadow-only entities.</summary>
    public static bool LodEnabled = true;

    /// <summary>Rendered entities farther than this (blocks, horizontal) animate every
    /// second frame.</summary>
    public static double FarBlocks = 48;

    public const int FarDivisor = 2;
    public const int ShadowOnlyDivisor = 3;

    /// <summary>Animation frames run / skipped (folded into a later frame), since start.</summary>
    public static long StatAnimated, StatSkipped;

    /// <summary>Smoothed per-frame figures for the report.</summary>
    public static double AvgBeforeMs { get; private set; }
    public static double AvgAnimMs { get; private set; }
    public static double AvgRendered { get; private set; }
    public static double AvgAnimated { get; private set; }

    /// <summary>The single most expensive entity-frame seen (BeforeRender + animation).</summary>
    public static double StatWorstMs;
    public static string StatWorstName;

    private const double Alpha = 1.0 / 64.0;
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private static long frame;
    private static long beforeTicks, animTicks, topTicks;
    private static int nRendered, nAnimated;
    private static Entity topEntity;
    private static readonly Dictionary<long, float> pendingDt = new(256);

    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> GameRef =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> RenderersRef =
        AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");

    public static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)])
                     ?? throw new InvalidOperationException("SystemRenderEntities.OnBeforeRender not found");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(EntityAnimPatches), nameof(BeforePrefix)));
    }

    public static bool BeforePrefix(SystemRenderEntities __instance, float dt)
    {
        if (!Enabled) return true;
        Run(GameRef(__instance), dt);
        return false;
    }

    /// <summary>SystemRenderEntities.OnBeforeRender, transcribed, with clocks and the rate rule.</summary>
    private static void Run(ClientMain game, float dt)
    {
        frame++;
        double viewSq = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
        var self = game.EntityPlayer;
        var xyz = self.Pos.XYZ;
        var dimension = self.Pos.Dimension;
        var profiler = game.api.World.FrameProfiler;
        var lod = LodEnabled;
        var farSq = FarBlocks * FarBlocks;
        var culler = game.frustumCuller;

        foreach (var kv in RenderersRef(game))
        {
            var renderer = kv.Value;
            var entity = renderer.entity;
            var pos = entity.Pos;
            var t0 = Stopwatch.GetTimestamp();

            var distSq = -1.0;
            var visible = culler.SphereInFrustum((float)pos.X, (float)pos.InternalY, (float)pos.Z, entity.FrustumSphereRadius)
                          && pos.Dimension == dimension;
            if (visible && !entity.AllowOutsideLoadedRange)
            {
                distSq = xyz.HorizontalSquareDistanceTo(pos.X, pos.Z);
                visible = distSq < viewSq && (entity == self || game.WorldMap.IsChunkRendered(pos));
            }
            if (visible)
            {
                entity.IsRendered = true;
                renderer.BeforeRender(dt);
                nRendered++;
            }
            else entity.IsRendered = false;
            var t1 = Stopwatch.GetTimestamp();
            beforeTicks += t1 - t0;

            profiler.Mark("esr-beforeanim");
            var anim = entity.AnimManager;
            if (anim != null)
            {
                var divisor = 1;
                if (lod && entity != self && (entity.IsRendered || entity.IsShadowRendered))
                {
                    if (distSq < 0) distSq = xyz.HorizontalSquareDistanceTo(pos.X, pos.Z);
                    divisor = Divisor(entity.IsRendered, entity.IsShadowRendered, distSq, farSq);
                }
                if (IsTurn(frame, entity.EntityId, divisor))
                {
                    var useDt = dt;
                    if (pendingDt.Count > 0 && pendingDt.Remove(entity.EntityId, out var held)) useDt += held;
                    try
                    {
                        anim.OnClientFrame(useDt);
                    }
                    catch (Exception)
                    {
                        game.Logger.Error("Animations error for entity " + entity.Code.ToShortString() + " at " + entity.Pos.AsBlockPos);
                        throw;
                    }
                    nAnimated++;
                }
                else
                {
                    pendingDt[entity.EntityId] = (pendingDt.TryGetValue(entity.EntityId, out var held) ? held : 0f) + dt;
                    StatSkipped++;
                }
            }
            profiler.Mark("esr-afteranim");
            var t2 = Stopwatch.GetTimestamp();
            animTicks += t2 - t1;
            var total = t2 - t0;
            if (total > topTicks) { topTicks = total; topEntity = entity; }
        }
        // ids of entities long gone - bounded, and a lost fraction of a frame is harmless
        if (pendingDt.Count > 8192) pendingDt.Clear();
    }

    /// <summary>The rate rule, pure: shadow-only entities every third frame, rendered ones
    /// beyond the far distance every second, everything else every frame.</summary>
    internal static int Divisor(bool rendered, bool shadowRendered, double distSq, double farSq)
    {
        if (!rendered) return shadowRendered ? ShadowOnlyDivisor : 1;
        return distSq > farSq ? FarDivisor : 1;
    }

    /// <summary>Whose turn it is this frame; ids spread the turns across frames.</summary>
    internal static bool IsTurn(long frame, long entityId, int divisor)
        => divisor <= 1 || (frame + entityId) % divisor == 0;

    /// <summary>The finished frame's figures, raw - valid between the frame boundary's hitch
    /// detection and <see cref="EndFrame"/>, like the tick profiler's.</summary>
    public static (double beforeMs, double animMs, int animated, string topName, double topMs)? TopOfCurrentFrame()
    {
        if (beforeTicks + animTicks == 0) return null;
        return (beforeTicks * TicksToMs, animTicks * TicksToMs, nAnimated,
                topEntity?.Code?.ToShortString(), topTicks * TicksToMs);
    }

    public static void EndFrame()
    {
        var first = AvgRendered == 0 && AvgAnimated == 0 && AvgBeforeMs == 0;
        AvgBeforeMs = Blend(AvgBeforeMs, beforeTicks * TicksToMs, first);
        AvgAnimMs = Blend(AvgAnimMs, animTicks * TicksToMs, first);
        AvgRendered = Blend(AvgRendered, nRendered, first);
        AvgAnimated = Blend(AvgAnimated, nAnimated, first);
        StatAnimated += nAnimated;
        var topMs = topTicks * TicksToMs;
        if (topMs > StatWorstMs)
        {
            StatWorstMs = topMs;
            StatWorstName = topEntity?.Code?.ToShortString();
        }
        beforeTicks = animTicks = topTicks = 0;
        nRendered = nAnimated = 0;
        topEntity = null;
    }

    private static double Blend(double cur, double sample, bool first) => first ? sample : cur + (sample - cur) * Alpha;

    public static void Write(StringBuilder sb, System.Globalization.CultureInfo ci)
    {
        sb.AppendFormat(ci, "entity before: pre-render {0:F2} ms ({1:F0} visible), anim {2:F2} ms ({3:F0} per frame, {4:N0} skipped)",
            AvgBeforeMs, AvgRendered, AvgAnimMs, AvgAnimated, StatSkipped);
        if (StatWorstMs >= 1.0)
            sb.AppendFormat(ci, ", most expensive {0:F1} ms ({1})", StatWorstMs, StatWorstName ?? "?");
        if (!LodEnabled) sb.Append(" (anim lod OFF)");
        if (!Enabled) sb.Append(" (OFF)");
        sb.Append('\n');
    }

    public static void ResetStats()
    {
        StatAnimated = StatSkipped = 0;
        StatWorstMs = 0;
        StatWorstName = null;
    }

    /// <summary>World leave: held entity references and per-entity dt belong to that world.</summary>
    public static void Reset()
    {
        ResetStats();
        pendingDt.Clear();
        beforeTicks = animTicks = topTicks = 0;
        nRendered = nAnimated = 0;
        topEntity = null;
        AvgBeforeMs = AvgAnimMs = AvgRendered = AvgAnimated = 0;
    }
}
