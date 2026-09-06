using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Komet.Runtime;

/// <summary>
/// Generates an entity shape's animation frames on a worker thread before the first entity
/// of that shape is handed to the world.
///
/// The engine builds the per-frame transforms of an animation lazily: ClientAnimator.
/// AnimNowActive calls Animation.GenerateAllFrames the first time an animation starts, on the
/// main thread, inside the entity Before stage. For a shape with a few dozen elements that
/// is milliseconds per animation - measured against the game's own shape files on the
/// author's machine: chicken-rooster 11,9 ms for all 13 animations, 4,7 ms for the single
/// "attack"; the report's "anim ... most expensive 53,1 ms (pig-eurasian-adult-female)" is the
/// same cost on a bigger shape. The frames live on the SHAPE's Animation objects, shared by
/// every entity of that type, so the price is paid once per type per session - and always
/// in the frame in which a new kind of creature first moves into view.
///
/// Every entity reaches the client through the load hold (see EntityLoadPatches): it is held
/// in a distance bin for a few frames before Initialize. That is the window: on the first held
/// entity of a shape a worker runs exactly what the engine's cache-miss path runs -
/// InitForAnimations with the same arguments, then GenerateAllFrames for every animation - and
/// the entity, along with every later one of the same shape, stays held until the worker is
/// done. The main thread then finds PrevNextKeyFrameByFrame already set and skips the
/// generation; the joint ids it re-derives are the same, because ResolveAndFindJoints is
/// deterministic and idempotent on an initialised shape.
///
/// Thread safety rests on one rule: nobody else touches a shape while its worker runs. The
/// hold guarantees it for entities that arrive later; for entities that are already in the
/// world the warm-up is not started at all - a shape that has any animation generated, or any
/// loaded entity using it, is taken as in use and left alone (the engine's lazy path then does
/// what it always did). Inside GenerateAllFrames the only shared state is the static identity
/// matrix, which is cloned and never written, and per-Animation scratch sets, which belong to
/// the shape being warmed. A promote (a packet naming a held entity) or the disable-flush wait
/// for a running worker - a few milliseconds at most, and rare (17 of 712 in the field).
/// </summary>
public static class AnimationWarmup
{
    public static bool Enabled;

    /// <summary>Warnings go here (the mod's logger); null drops them.</summary>
    public static Action<string> Log;

    /// <summary>Shapes warmed, animations generated, worker milliseconds, since start.</summary>
    public static long StatShapes, StatAnimations, StatSkippedInUse, StatWaits;

    /// <summary>Animations whose frames could not be generated - malformed shape data the
    /// engine's lazy path would only ever have found if that animation was played. Reported
    /// rather than swallowed: a warm-up that quietly skips half a creature is a warm-up nobody
    /// can tell from a working one.</summary>
    public static long StatBroken;
    public static double StatWorkerMs, StatWorstMs, StatWaitMs;
    public static string StatWorstShape;

    private static readonly object gate = new();
    private static readonly Dictionary<Shape, ManualResetEventSlim> running = new();
    private static readonly HashSet<Shape> done = new();

    /// <summary>
    /// The shape an entity with this id will animate with - the engine's own choice
    /// (EntityClientProperties.DetermineLoadedShape: alternates are picked by a hash of the
    /// entity id), computed without touching the class-level properties.
    /// </summary>
    public static Shape ShapeFor(EntityProperties type, long entityId)
    {
        var client = type?.Client;
        if (client == null) return null;
        var alternates = client.LoadedAlternateShapes;
        if (alternates == null || alternates.Length == 0) return client.LoadedShape;
        var num = GameMath.MurmurHash3Mod(0, 0, (int)entityId, 1 + alternates.Length);
        return num == 0 ? client.LoadedShape : alternates[num - 1];
    }

    /// <summary>
    /// Starts the warm-up for the shape this entity will use, unless it is done, running, or
    /// in use by the world already. Main thread. Returns true when a worker is now running
    /// for it (the caller holds the entity until <see cref="Ready"/>).
    /// </summary>
    public static bool Start(EntityProperties type, long entityId, ILogger logger, System.Func<Shape, bool> shapeInUse)
    {
        if (!Enabled) return false;
        var shape = ShapeFor(type, entityId);
        if (shape?.Animations == null || shape.Animations.Length == 0 || shape.Elements == null) return false;

        ManualResetEventSlim ev;
        lock (gate)
        {
            if (done.Contains(shape)) return false;
            if (running.ContainsKey(shape)) return true;

            // In use already: an animation has frames, or a loaded entity animates with it.
            // Then the main thread may be reading the shape right now, and the engine's lazy
            // path is the only safe one.
            if (AnyGenerated(shape) || (shapeInUse != null && shapeInUse(shape)))
            {
                done.Add(shape);
                StatSkippedInUse++;
                return false;
            }
            ev = new ManualResetEventSlim(false);
            running[shape] = ev;
        }

        var name = type.Client.ShapeForEntity?.Base?.ToString() ?? type.Code?.ToString() ?? "?";
        var disable = DisableElements(type);
        var require = RequireJoints(type);
        // Komet's own pool rather than the shared ThreadPool: the game queues chunk work on
        // that one, and a prewarm holds a thread for hundreds of milliseconds per shape. Here
        // it sits in the Idle queue, so it only ever runs on a worker with nothing else to do.
        JobScheduler.Submit(JobKind.Warmup, long.MinValue,
            () => Run(shape, name, disable, require, logger, ev));
        return true;
    }

    /// <summary>The worker body, also the harness's entry: the engine's cache-miss sequence.</summary>
    internal static int Warm(Shape shape, string name, string[] disableElements, string[] requireJoints, ILogger logger)
        => Warm(shape, name, disableElements, requireJoints, logger, out _, out _);

    /// <summary>
    /// The engine's cache-miss sequence, with the count of animations it could not generate.
    ///
    /// One animation failing must not cost the shape its warm-up, and that is not a hypothetical:
    /// a field log from 1.22.5 shows game:locust-corrupt-sawblade throwing on 'idlesaw'
    /// ("QuantityFrames set to 7 but a key frame at frame 7"), which used to abandon the loop and
    /// leave every LATER animation of that creature to the lazy main-thread path - exactly the
    /// hitch this feature exists to remove, now paid in full because of one bad entry.
    ///
    /// It matters that this warm-up does MORE than the engine does: the engine generates an
    /// animation's frames when that animation first plays, so malformed data on an animation
    /// nothing ever starts is data the engine never touches. Generating everything up front
    /// finds it. That is a reason to skip the entry and carry on, not to stop.
    /// </summary>
    internal static int Warm(Shape shape, string name, string[] disableElements, string[] requireJoints,
                             ILogger logger, out int broken, out string firstFailure)
    {
        // AnimationCache.InitManager runs the three-argument InitForAnimations, then a cache
        // miss runs AnimationManager.LoadAnimator's four-argument one - in that order, and
        // both are idempotent, so the state after is what the engine leaves behind.
        shape.InitForAnimations(logger, name, requireJoints);
        shape.InitForAnimations(logger, name, disableElements, requireJoints);
        var generated = 0;
        broken = 0;
        firstFailure = null;
        foreach (var animation in shape.Animations)
        {
            if (animation == null || animation.PrevNextKeyFrameByFrame != null) continue;
            if (animation.KeyFrames == null || animation.KeyFrames.Length == 0) continue;
            try
            {
                animation.GenerateAllFrames(shape.Elements, shape.JointsById);
                generated++;
            }
            catch (Exception e)
            {
                // Left exactly as the engine would find it, so its own lazy path still throws
                // its own exception in its own place if this animation is ever played.
                animation.PrevNextKeyFrameByFrame = null;
                broken++;
                firstFailure ??= (animation.Code ?? "?") + ": " + e.GetType().Name + ": " + e.Message;
            }
        }
        return generated;
    }

    private static void Run(Shape shape, string name, string[] disable, string[] require, ILogger logger, ManualResetEventSlim ev)
    {
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var generated = Warm(shape, name, disable, require, logger, out var broken, out var firstFailure);
            var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            lock (gate)
            {
                StatShapes++;
                StatAnimations += generated;
                StatBroken += broken;
                StatWorkerMs += ms;
                if (ms > StatWorstMs) { StatWorstMs = ms; StatWorstShape = name; }
            }

            // Once per shape, not once per animation: a creature with three malformed entries
            // is one line, and the count says how much of the shape did warm up.
            if (broken > 0)
                Log?.Invoke("animation warm-up for " + name + ": " + generated + " animations generated, "
                          + broken + " could not be (first " + firstFailure
                          + ") - the engine hits the same data lazily if one of them ever plays");
        }
        catch (Exception e)
        {
            // InitForAnimations itself failed, so there is no per-animation loop to salvage:
            // the engine's lazy path will do the whole shape on the main thread, as before.
            Log?.Invoke("animation warm-up for " + name + " failed (" + e.GetType().Name + ": " + e.Message + ") - the engine generates the frames lazily");
        }
        finally
        {
            lock (gate)
            {
                running.Remove(shape);
                done.Add(shape);
            }
            ev.Set();
        }
    }

    /// <summary>Whether an entity of this shape may be finished now (no worker on it).</summary>
    public static bool Ready(Shape shape)
    {
        if (shape == null) return true;
        lock (gate) return !running.ContainsKey(shape);
    }

    /// <summary>Blocks until the shape's worker is done. Only for the out-of-turn paths.</summary>
    public static void Wait(Shape shape)
    {
        if (shape == null) return;
        ManualResetEventSlim ev;
        lock (gate)
        {
            if (!running.TryGetValue(shape, out ev)) return;
        }
        var t0 = Stopwatch.GetTimestamp();
        ev.Wait();
        lock (gate)
        {
            StatWaits++;
            StatWaitMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private static bool AnyGenerated(Shape shape)
    {
        foreach (var a in shape.Animations)
            if (a?.PrevNextKeyFrameByFrame != null) return true;
        return false;
    }

    /// <summary>Entity.OnTesselation's disableElements, from the same attribute.</summary>
    internal static string[] DisableElements(EntityProperties type)
    {
        try
        {
            var attributes = type?.Attributes;
            if (attributes != null && attributes["disableElements"].Exists)
                return attributes["disableElements"].AsArray<string>();
        }
        catch (Exception) { /* a malformed attribute is the engine's to complain about */ }
        return null;
    }

    /// <summary>AnimationManager.LoadAnimator's requireJointsForElements: "head" plus the attribute.</summary>
    internal static string[] RequireJoints(EntityProperties type)
    {
        var joints = new List<string> { "head" };
        try
        {
            var attributes = type?.Attributes;
            if (attributes != null && attributes["requireJointsForElements"].Exists)
            {
                var extra = attributes["requireJointsForElements"].AsArray<string>();
                if (extra != null) joints.AddRange(extra);
            }
        }
        catch (Exception) { /* as above */ }
        return joints.ToArray();
    }

    /// <summary>World leave: the shapes die with the world; running workers finish on their
    /// own and find nobody waiting.</summary>
    public static void Reset()
    {
        lock (gate) done.Clear();
    }

    public static void ResetStats()
    {
        lock (gate)
        {
            StatShapes = StatAnimations = StatSkippedInUse = StatWaits = StatBroken = 0;
            StatWorkerMs = StatWorstMs = StatWaitMs = 0;
            StatWorstShape = null;
        }
    }

    /// <summary>Harness: whether a shape is recorded as done.</summary>
    internal static bool IsDone(Shape shape) { lock (gate) return done.Contains(shape); }

    /// <summary>Harness: marks a shape as being warmed, so the drain gating can be checked
    /// without a worker; <see cref="Release"/> ends it.</summary>
    internal static ManualResetEventSlim BlockForTest(Shape shape)
    {
        var ev = new ManualResetEventSlim(false);
        lock (gate) running[shape] = ev;
        return ev;
    }

    internal static void Release(Shape shape)
    {
        ManualResetEventSlim ev;
        lock (gate)
        {
            running.Remove(shape, out ev);
            done.Add(shape);
        }
        ev?.Set();
    }
}
