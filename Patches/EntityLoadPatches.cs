using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Komet.Runtime;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Budgets the main-thread half of entity loading and orders it nearest-first.
///
/// How entities reach the client in 1.22: chunk packets carry none (the Entities field of
/// Packet_ServerChunk is never filled), and the EntityLoadQueue that ClientSystemEntities
/// drains every tick has no producer left - both dead paths. What actually happens is that
/// the server starts TRACKING an entity once its chunk was sent (PhysicsManager, every
/// 200 ms) and sends one full entity packet (id 33) per entity; spawns come as packet 34.
/// Each arrives as its own main-thread task, and the task does everything at once: create
/// the entity, deserialise it, Initialize (behaviours, animator, attributes), register it
/// in its chunk, add it to LoadedEntities and fire OnEntityLoaded - which creates the
/// renderer. A world join or a flood of freshly streamed chunks therefore drops hundreds of
/// these into ONE task drain, in the order the server happened to iterate them.
///
/// This splits the work at the point vanilla already splits it: creation and FromBytes
/// (cheap, and it yields the position) run immediately when the packet arrives; the
/// expensive remainder - Initialize, chunk registration, renderer creation - is held in
/// distance bins and finished at the frame boundary under a per-frame millisecond budget,
/// nearest entity first. The budget has the same liveness floor as the other budgets in
/// this mod: at least MinPerFrame loads per frame, so a backlog can never starve, and
/// disabling the feature flushes everything at once (nothing is ever stranded).
///
/// Coherence with every other packet that names an entity is exact. Attribute syncs (37,
/// 38, 60) are applied to the held entity's tree directly - equivalent to the server having
/// sent its full packet a moment later, and vanilla's own handler then ignores the id it
/// cannot find. This matters: the first field report showed 74 % of held entities being
/// finished early, because nearly every freshly tracked entity gets a partial attribute
/// update within 200 ms - which used to promote it and hand the whole burst back to one
/// frame. A custom entity packet (67), the spawn position (80) and the bulk packet (40)
/// need an initialised entity and finish it on the spot; a despawn (36) drops it (it was
/// never registered anywhere). The only observable difference is WHEN a distant entity
/// appears after its packet - a few frames later in a burst - which is exactly the
/// difference the prio upload budget makes for chunk meshes.
/// </summary>
public static class EntityLoadPatches
{
    public static bool Enabled = true;

    /// <summary>Main-thread milliseconds per frame for finishing entity loads. 0 = flush
    /// everything every frame (vanilla timing, still nearest-first).</summary>
    public static double BudgetMs = 1.5;

    /// <summary>Liveness floor: this many loads always run per frame, budget or not.</summary>
    public const int MinPerFrame = 2;

    /// <summary>Distance bins of BinBlocks each; the last bin holds everything beyond.</summary>
    public const int BinCount = 32;
    public const double BinBlocks = 32.0;

    /// <summary>Warning sink (unknown entity types, like vanilla's own error line).</summary>
    public static Action<string> Log;

    public static long StatLoaded, StatDeferredFrames, StatPromoted, StatDropped, StatUpdatedPending, StatStaleFlushes;

    /// <summary>
    /// How long the bins may go without a frame boundary before intake stops holding anything.
    ///
    /// Only <see cref="OnFrameBoundary"/> finishes a held entity. If that event stops coming
    /// while the patch stays applied, every entity the server sends is held forever and never
    /// reaches LoadedEntities - the world simply has no creatures in it. Held work whose drain
    /// is gone has to fall back to vanilla (finish on the spot), so a boundary this old counts
    /// as none; see EntityTessPatches.StaleAfterMs for the same rule and the same reason.
    /// </summary>
    public const double StaleAfterMs = 250.0;

    private static long lastBoundaryTs;
    public static double StatWorstMs;
    public static string StatWorstCode;
    public static int PendingCount => pendingById.Count;

    internal sealed class Pending
    {
        public long Id;
        public Entity Entity;
        public EntityProperties Type;
        public bool Spawn;
        public int Bin;
    }

    private static readonly List<Pending>[] bins = new List<Pending>[BinCount];
    private static readonly Dictionary<long, Pending> pendingById = new(256);
    private static ClientMain game;

    private static readonly System.Func<ClientWorldMap, long, ClientChunk> GetClientChunk =
        AccessTools.MethodDelegate<System.Func<ClientWorldMap, long, ClientChunk>>(
            AccessTools.Method(typeof(ClientWorldMap), "GetClientChunk", [typeof(long)]));

    static EntityLoadPatches()
    {
        for (var i = 0; i < BinCount; i++) bins[i] = new List<Pending>();
    }

    public static void Apply(Harmony harmony)
    {
        var t = typeof(ClientSystemEntities);
        MethodBase Handler(string name) =>
            AccessTools.Method(t, name, [typeof(Packet_Server)])
            ?? throw new InvalidOperationException("ClientSystemEntities." + name + "(Packet_Server) not found");

        harmony.Patch(Handler("HandleEntityLoadedPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(LoadedPrefix)));
        harmony.Patch(Handler("HandleEntitySpawnPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(SpawnPrefix)));
        harmony.Patch(Handler("HandleEntityDespawnPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(DespawnPrefix)));
        // every other packet that names an entity finishes a held one first, so vanilla's
        // LoadedEntities lookup succeeds exactly as it would have without the budget
        harmony.Patch(Handler("HandleEntitiesPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(PromoteEntities)));
        // attribute syncs are folded into a held entity's tree instead - promoting on them
        // defeated the budget for three entities out of four in the first field report
        harmony.Patch(Handler("HandleEntityAttributesPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(ApplyAttributes)));
        harmony.Patch(Handler("HandleEntityAttributeUpdatePacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(ApplyAttributeUpdate)));
        harmony.Patch(Handler("HandleEntityBulkAttributesPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(ApplyBulkAttributes)));
        harmony.Patch(Handler("HandleEntityPacket"), prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(PromoteEntityPacket)));

        var spawnPos = AccessTools.Method(typeof(SystemNetworkProcess), "HandleEntitySpawnPosition", [typeof(Packet_Server)])
                       ?? throw new InvalidOperationException("SystemNetworkProcess.HandleEntitySpawnPosition not found");
        harmony.Patch(spawnPos, prefix: new HarmonyMethod(typeof(EntityLoadPatches), nameof(PromoteSpawnPosition)));
    }

    // ---- the rules, pure ----------------------------------------------------------------

    /// <summary>Liveness first, then the budget - the same shape as every budget in this mod.</summary>
    internal static bool ShouldLoad(double spentMs, int loadedThisFrame, double budgetMs, int minPerFrame)
        => loadedThisFrame < minPerFrame || spentMs < budgetMs;

    /// <summary>Distance bin of a squared distance to the player; unknown (NaN) goes last.</summary>
    internal static int BinOf(double distSq)
    {
        var d = Math.Sqrt(distSq);
        if (!(d >= 0)) return BinCount - 1;
        var bin = (int)(d / BinBlocks);
        return bin >= BinCount ? BinCount - 1 : bin;
    }

    // ---- intake ---------------------------------------------------------------------------

    public static bool LoadedPrefix(ClientSystemEntities __instance, Packet_Server serverpacket)
    {
        if (!Enabled) return true;
        var g = ClientQueues.GameOf(__instance);
        if (g == null) return true;
        var packet = serverpacket.Entity;
        if (packet == null) return false; // vanilla: nothing to do either
        Intake(g, packet, spawn: false);
        return false;
    }

    public static bool SpawnPrefix(ClientSystemEntities __instance, Packet_Server serverpacket)
    {
        if (!Enabled) return true;
        var g = ClientQueues.GameOf(__instance);
        if (g == null) return true;
        var spawn = serverpacket.EntitySpawn;
        if (spawn?.Entity == null) return false;
        for (var i = 0; i < spawn.EntityCount; i++)
        {
            var p = spawn.Entity[i];
            if (p != null) Intake(g, p, spawn: true);
        }
        return false;
    }

    /// <summary>A despawn for an entity that was never finished: drop it, it exists nowhere.</summary>
    public static void DespawnPrefix(Packet_Server serverpacket)
    {
        var d = serverpacket.EntityDespawn;
        if (d?.EntityId == null || pendingById.Count == 0) return;
        for (var i = 0; i < d.EntityIdCount; i++)
        {
            if (pendingById.Remove(d.EntityId[i], out var pend))
            {
                bins[pend.Bin].Remove(pend);
                StatDropped++;
            }
        }
    }

    public static void PromoteEntities(Packet_Server serverpacket)
    {
        var e = serverpacket.Entities?.Entities;
        if (e == null || pendingById.Count == 0) return;
        for (var i = 0; i < e.Length && e[i] != null; i++) Promote(e[i].EntityId);
    }

    public static void ApplyAttributes(Packet_Server serverpacket)
    {
        if (pendingById.Count == 0) return;
        var a = serverpacket.EntityAttributes;
        if (a != null) ApplyFull(a);
    }

    public static void ApplyAttributeUpdate(Packet_Server serverpacket)
    {
        if (pendingById.Count == 0) return;
        var a = serverpacket.EntityAttributeUpdate;
        if (a != null) ApplyPartial(a);
    }

    public static void ApplyBulkAttributes(Packet_Server packet)
    {
        if (pendingById.Count == 0) return;
        var b = packet.BulkEntityAttributes;
        if (b == null) return;
        if (b.FullUpdates != null)
            for (var i = 0; i < b.FullUpdatesCount; i++)
                if (b.FullUpdates[i] != null) ApplyFull(b.FullUpdates[i]);
        if (b.PartialUpdates != null)
            for (var i = 0; i < b.PartialUpdatesCount; i++)
                if (b.PartialUpdates[i] != null) ApplyPartial(b.PartialUpdates[i]);
    }

    /// <summary>A full attribute packet for a held entity: the same FromBytes vanilla runs on
    /// a loaded one. Vanilla's handler then finds no such loaded entity and does nothing.</summary>
    private static void ApplyFull(Packet_EntityAttributes a)
    {
        if (!pendingById.TryGetValue(a.EntityId, out var held) || a.Data == null) return;
        held.Entity.FromBytes(new BinaryReader(new MemoryStream(a.Data)), isSync: true);
        StatUpdatedPending++;
    }

    /// <summary>A partial update for a held entity, path by path, as vanilla applies it.</summary>
    private static void ApplyPartial(Packet_EntityAttributeUpdate p)
    {
        if (!pendingById.TryGetValue(p.EntityId, out var held) || p.Attributes == null) return;
        var tree = held.Entity.WatchedAttributes;
        for (var i = 0; i < p.AttributesCount; i++)
        {
            var a = p.Attributes[i];
            if (a != null) tree.PartialUpdate(a.Path, a.Data);
        }
        StatUpdatedPending++;
    }

    public static void PromoteEntityPacket(Packet_Server serverpacket)
    {
        if (pendingById.Count == 0) return;
        var p = serverpacket.EntityPacket;
        if (p != null) Promote(p.EntityId);
    }

    public static void PromoteSpawnPosition(Packet_Server packet)
    {
        if (pendingById.Count == 0) return;
        var p = packet.EntityPosition;
        if (p != null) Promote(p.EntityId);
    }

    /// <summary>
    /// The immediate half of vanilla's entityFromPacket / createOrUpdateEntityFromPacket:
    /// an entity that already exists is updated in place (and, on the load path, re-announced
    /// exactly as vanilla does); a held one takes the update; a new one is created and
    /// deserialised - which is what yields its position - and then held for the budget.
    /// </summary>
    private static void Intake(ClientMain g, Packet_Entity p, bool spawn)
    {
        game = g;
        if (g.LoadedEntities.TryGetValue(p.EntityId, out var existing))
        {
            Update(p, existing);
            // vanilla's loaded path re-fires OnEntityLoaded for an existing entity; the
            // spawn path only updates
            if (!spawn) g.eventManager?.TriggerEntityLoaded(existing);
            return;
        }
        if (pendingById.TryGetValue(p.EntityId, out var held))
        {
            Update(p, held.Entity);
            StatUpdatedPending++;
            return;
        }

        var type = g.GetEntityType(new AssetLocation(p.EntityType));
        if (type == null)
        {
            Log?.Invoke("Server sent a create entity packet for entity code '" + p.EntityType
                        + "', but no such entity exists?. Ignoring");
            return;
        }
        var entity = g.Api.ClassRegistry.CreateEntity(type);
        entity.SimulationRange = p.SimulationRange;
        entity.Api = g.Api;
        Update(p, entity);

        var plr = g.EntityPlayer?.Pos;
        var pos = entity.Pos;
        var distSq = plr == null || pos == null ? double.NaN
            : (pos.X - plr.X) * (pos.X - plr.X) + (pos.Y - plr.Y) * (pos.Y - plr.Y) + (pos.Z - plr.Z) * (pos.Z - plr.Z);
        var pend = new Pending { Id = p.EntityId, Entity = entity, Type = type, Spawn = spawn, Bin = BinOf(distSq) };
        bins[pend.Bin].Add(pend);
        pendingById[pend.Id] = pend;

        // the drain that empties these bins is not running - finish everything here, which is
        // vanilla's own timing, instead of holding entities nobody will ever hand over
        if (DrainStale(Stopwatch.GetTimestamp(), lastBoundaryTs))
        {
            StatStaleFlushes++;
            FlushAll();
        }
    }

    private static void Update(Packet_Entity p, Entity entity)
    {
        var reader = new BinaryReader(new MemoryStream(p.Data));
        entity.FromBytes(reader, isSync: true);
    }

    /// <summary>The deferred half: vanilla's remaining lines, in vanilla's order.</summary>
    private static void Finish(ClientMain g, Pending pend)
    {
        var entity = pend.Entity;
        var chunkIndex = g.WorldMap.ChunkIndex3D(entity.Pos);
        entity.Initialize(pend.Type.Clone(), g.Api, chunkIndex);
        entity.AfterInitialized(onFirstSpawn: false);
        GetClientChunk(g.WorldMap, chunkIndex)?.AddEntity(entity);
        g.LoadedEntities[entity.EntityId] = entity;
        if (pend.Spawn) g.eventManager?.TriggerEntitySpawn(entity);
        else g.eventManager?.TriggerEntityLoaded(entity);
    }

    /// <summary>Finishes one held entity out of turn, because a packet just named it.</summary>
    private static void Promote(long id)
    {
        if (!pendingById.Remove(id, out var pend)) return;
        bins[pend.Bin].Remove(pend);
        var g = game;
        if (g == null) return;
        var t0 = Stopwatch.GetTimestamp();
        Finish(g, pend);
        Book(pend, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        StatPromoted++;
    }

    // ---- the drain ------------------------------------------------------------------------

    /// <summary>The staleness rule, pure: no boundary within <see cref="StaleAfterMs"/> means
    /// nothing is draining the bins. Zero (none seen yet) counts as stale.</summary>
    internal static bool DrainStale(long nowTs, long lastTs)
        => lastTs == 0 || (nowTs - lastTs) * 1000.0 / Stopwatch.Frequency > StaleAfterMs;

    /// <summary>Called on the frame boundary (main thread): nearest bins first, under budget.</summary>
    public static void OnFrameBoundary()
    {
        lastBoundaryTs = Stopwatch.GetTimestamp();
        if (pendingById.Count == 0) return;
        var g = game;
        if (g == null) return;
        var spent = Drain(BudgetMs, pend =>
        {
            var t0 = Stopwatch.GetTimestamp();
            Finish(g, pend);
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        });
        if (spent > 0) Measure.FrameStats.AddEntityLoadMs(spent);
    }

    /// <summary>
    /// The drain mechanics, separated from the engine side effects so verify can drive them:
    /// bins nearest-first, each entry handed to <paramref name="finish"/> (which returns the
    /// milliseconds it took), stopping once the budget rule says so. Returns the ms spent.
    /// </summary>
    internal static double Drain(double budgetMs, System.Func<Pending, double> finish)
    {
        double spent = 0;
        var loaded = 0;
        for (var b = 0; b < BinCount && ShouldLoad(spent, loaded, budgetMs, MinPerFrame); b++)
        {
            var bin = bins[b];
            while (bin.Count > 0 && ShouldLoad(spent, loaded, budgetMs, MinPerFrame))
            {
                var pend = bin[bin.Count - 1];
                bin.RemoveAt(bin.Count - 1);
                pendingById.Remove(pend.Id);
                var ms = finish(pend);
                Book(pend, ms);
                spent += ms;
                loaded++;
            }
        }
        if (pendingById.Count > 0) StatDeferredFrames++;
        return spent;
    }

    private static void Book(Pending pend, double ms)
    {
        StatLoaded++;
        if (ms > StatWorstMs)
        {
            StatWorstMs = ms;
            StatWorstCode = pend.Entity?.Code?.ToShortString();
        }
    }

    /// <summary>Everything held is finished now - the disable path, so nothing strands.</summary>
    public static void FlushAll()
    {
        var g = game;
        if (g == null) { Reset(); return; }
        while (pendingById.Count > 0)
            Drain(double.MaxValue, pend =>
            {
                var t0 = Stopwatch.GetTimestamp();
                Finish(g, pend);
                return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            });
    }

    /// <summary>World leave: held entities die with the world they were meant for.</summary>
    public static void Reset()
    {
        for (var i = 0; i < BinCount; i++) bins[i].Clear();
        pendingById.Clear();
        game = null;
        lastBoundaryTs = 0;
    }

    public static void ResetStats()
    {
        StatLoaded = StatDeferredFrames = StatPromoted = StatDropped = StatUpdatedPending = StatStaleFlushes = 0;
        StatWorstMs = 0;
        StatWorstCode = null;
    }

    // ---- test seams -----------------------------------------------------------------------

    /// <summary>verify: holds a fake entry in a bin (optionally with an entity behind it).</summary>
    internal static void HoldForTest(long id, int bin, Entity entity = null)
    {
        var pend = new Pending { Id = id, Bin = bin, Entity = entity };
        bins[bin].Add(pend);
        pendingById[id] = pend;
    }

    /// <summary>verify: whether a fake entry is still held.</summary>
    internal static bool IsHeld(long id) => pendingById.ContainsKey(id);
}
