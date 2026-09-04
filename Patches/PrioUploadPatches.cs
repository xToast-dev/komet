using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Puts a per-frame vertex budget on the PRIORITY chunk upload queue.
///
/// ChunkTesselatorManager.OnBeforeFrame drains tessChunksQueuePriority with
/// <c>while (Count &gt; 0)</c> - no budget of any kind. That is the right call for its
/// designed load, a player block edit (one or two chunks that must appear instantly). But
/// everything else routes through the same queue: relight storms (a time or season change,
/// SheyderMod's light baking), ClientSystemRelight's priority marks, and this mod's own
/// edge-retess promotion. The 31.08. hitch log has the receipt - "davon upload 10-27 ms"
/// on frame after frame while standing still in a relight storm, every one of them this
/// loop uploading dozens of chunk meshes in a single frame. The adaptive upload budget
/// never sees them: its transpiler scales the NORMAL queue's vertex budget, and vanilla
/// checks that budget only after the priority drain is already complete.
///
/// The fix is the same shape as the entity tesselation budget: a cap with a liveness
/// floor. Per frame the drain uploads at least one chunk (a backlog can never starve) and
/// stops once the uploaded vertices reach the cap; the remainder stays in the queue and
/// continues NEXT frame - deferred, never lost. The cap is derived from the same
/// gain-scaled base the normal budget uses, so when a storm drives frame time up, the
/// controller squeezes both queues; the floor keeps it high enough that a typical player
/// edit (one full chunk mesh) still goes out in the frame it was meshed in.
///
/// Mechanically this is a prefix that performs the budgeted drain itself (a 1:1
/// transcription of vanilla's loop, per the firepit precedent) and then clears
/// processPrioQueue so the original's own unbudgeted loop finds nothing to do; the
/// original then proceeds with the normal queue exactly as vanilla. The prefix runs at
/// Harmony priority Low so the shared upload clock (MeasurementPatches, priority normal)
/// is already running around it - the drain stays inside the measured "upload" bucket.
/// </summary>
public static class PrioUploadPatches
{
    public static bool Enabled = true;

    /// <summary>
    /// Floor of the per-frame cap, in vertices. One full chunk mesh is some tens of
    /// thousands of vertices, so even with the upload controller at its floor a player
    /// edit is on screen the next frame; only genuine storms are spread out.
    /// </summary>
    public static int MinVertices = 65536;

    /// <summary>Priority chunks uploaded / frames that stopped early leaving a remainder.</summary>
    public static long StatUploadedChunks, StatDeferrals;

    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, Queue<TesselatedChunk>> PrioQueue =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, Queue<TesselatedChunk>>("tessChunksQueuePriority");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, object> PrioLock =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, object>("tessChunksQueuePriorityLock");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, bool> ProcessPrio =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, bool>("processPrioQueue");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, int> SingleUploadDelay =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, int>("singleUploadDelayCounter");

    private static readonly AccessTools.FieldRef<TesselatedChunk, ClientChunk> TcChunk =
        AccessTools.FieldRefAccess<TesselatedChunk, ClientChunk>("chunk");
    internal static readonly AccessTools.FieldRef<TesselatedChunk, int> TcVerts =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("VerticesCount");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> TcX =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> TcY =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> TcZ =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");
    private static readonly AccessTools.FieldRef<ClientChunk, bool> QueuedForUpload =
        AccessTools.FieldRefAccess<ClientChunk, bool>("queuedForUpload");
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> ChunkRendererRef =
        AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
    private static readonly Action<TesselatedChunk> UnusedDispose =
        AccessTools.MethodDelegate<Action<TesselatedChunk>>(
            AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose"));

    // main thread only, like vanilla's own tmpPos
    private static readonly Vec3i tmpPos = new();

    public static void Apply(Harmony harmony)
    {
        var target = Measure.MeasurementPatches.UploadMethod
                     ?? throw new InvalidOperationException("measurement patches must be applied first");
        harmony.Patch(target, prefix: new HarmonyMethod(
            typeof(PrioUploadPatches), nameof(BudgetedPrioDrain)) { priority = Priority.Low });
    }

    /// <summary>The continue rule, pure: the first entry of a frame always goes (liveness -
    /// a backlog must drain even against a tiny cap), further ones only under the cap.</summary>
    internal static bool ShouldContinue(int uploadedVerts, int processed, int capVerts)
        => processed == 0 || uploadedVerts < capVerts;

    /// <summary>Per-frame cap: three times the gain-scaled base (vanilla's own zero-backlog
    /// allowance for the normal queue), never below the one-full-chunk floor.</summary>
    internal static int CapVertices(int scaledBase, int minVerts)
        => Math.Max(minVerts, 3 * scaledBase);

    /// <summary>
    /// The queue/budget mechanics, separated from the engine side effects so verify can
    /// drive them with fake entries: dequeues while the rule allows, hands each entry to
    /// <paramref name="uploadOne"/> (which returns the vertices it uploaded, 0 for an entry
    /// that had no live chunk), and leaves the remainder in the queue.
    /// </summary>
    internal static int DrainBudgeted(Queue<TesselatedChunk> q, int capVerts, Func<TesselatedChunk, int> uploadOne)
    {
        int verts = 0, processed = 0;
        while (q.Count > 0 && ShouldContinue(verts, processed, capVerts))
        {
            var tc = q.Dequeue();
            processed++;
            verts += uploadOne(tc);
        }
        return verts;
    }

    public static void BudgetedPrioDrain(ChunkTesselatorManager __instance)
    {
        if (!Enabled) return;
        var game = ClientQueues.GameOf(__instance);
        if (game == null) return;
        var q = PrioQueue(__instance);
        if (q == null) return;
        // Pending work is either vanilla's flag or a remainder this budget left behind last
        // frame (the flag is already false then - the Count is what carries the liveness).
        if (!ProcessPrio(__instance) && q.Count == 0) return;

        var renderer = ChunkRendererRef(game);
        if (renderer == null) return; // pre-world startup; nothing can be queued yet either

        var viewDistSq = game.frustumCuller?.ViewDistanceSq ?? 0;
        var cap = CapVertices(UploadBudget.Scale(viewDistSq / 48 + 350), MinVertices);
        lock (PrioLock(__instance))
        {
            DrainBudgeted(q, cap, tc =>
            {
                // vanilla's loop body, 1:1 - including the unconditional chunk deref
                QueuedForUpload(TcChunk(tc)) = false;
                var at = game.WorldMap.GetChunkAtPos(TcX(tc), TcY(tc), TcZ(tc)) as ClientChunk;
                if (at == null)
                {
                    UnusedDispose(tc);
                    return 0;
                }
                renderer.AddTesselatedChunk(tc, at);
                SingleUploadDelay(__instance) = 10;
                tmpPos.Set(TcX(tc) / 32, TcY(tc) / 32, TcZ(tc) / 32);
                game.eventManager?.TriggerChunkRetesselated(tmpPos, at);
                StatUploadedChunks++;
                return TcVerts(tc);
            });
            if (q.Count > 0) StatDeferrals++;
        }

        // The original's own priority loop must not run - it has no budget and would drain
        // the remainder in this same frame. With the flag cleared it finds nothing to do and
        // proceeds straight to the normal queue; the remainder re-enters through the
        // q.Count > 0 check above on the next frame. The same enqueue-between-drain-and-clear
        // race vanilla has exists here too, and heals the same way: the next priority
        // tesselation sets the flag again.
        ProcessPrio(__instance) = false;
    }

    public static void ResetStats()
    {
        StatUploadedChunks = 0;
        StatDeferrals = 0;
    }
}
