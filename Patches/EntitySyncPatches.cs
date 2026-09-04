using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Network.Packets;
using Vintagestory.Server;
using Vintagestory.Server.Systems;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Server-side entity sync tuning - in singleplayer the integrated server shares the process
/// (and the GC) with the client, so every packet it does not build is garbage the client's
/// frame does not pay for. All four rules live in PhysicsManager, transcribed 1:1 with one
/// added condition each:
///
/// 1. Distance-based send rate. Vanilla sends a position packet for every tracked entity
///    that moved, every physics tick (30 Hz), to every client, whatever the distance. Beyond
///    40 blocks a creature is a few pixels tall; its position now goes out at 15 Hz, beyond
///    80 blocks at 10 Hz. The client interpolates between snapshots anyway
///    (EntityBehaviorInterpolatePosition), the packet's tick counter already tolerates gaps
///    (UDP), and teleports are never thinned.
/// 2. Tracking hysteresis. An entity is tracked while inside the range and untracked the
///    moment it is outside, so one wandering a few blocks around the boundary - or a player
///    walking back and forth - gets a full entity packet, a client-side create, and a despawn
///    over and over. Now an entity that is already tracked stays tracked until it is
///    HysteresisFactor times the range away. Simulation state (active/inactive) and the
///    IsTracked byte stay exactly vanilla; only the per-client list gains the band.
/// 3. Nearest-first tracking under the cap. When TrackedEntitiesPerClient bites, vanilla
///    admits new entities in dictionary order; now the nearest ones win.
/// 4. Attribute sync no-op skip. Every 200 ms each tracked entity's dirty attribute paths
///    are serialised and sent. Server code marks paths dirty on every Set, changed or not;
///    a path whose bytes equal what was last sent for that entity is dropped, and a packet
///    that ends up empty is not built. The cache is invalidated whenever a full entity
///    packet goes out (a new tracker gets the complete tree), so no client can be left
///    behind on a value that went A-B-A. Client-side listeners are not invoked for a value
///    that did not change - the engine's own convention for "ping" attributes is a counter
///    for exactly that reason (onHurtCounter etc.).
///
/// Everything here is runtime-gated per rule and the client's '.komet toggle entsync' /
/// 'attrskip' reach these statics directly in singleplayer.
/// </summary>
public static class EntitySyncPatches
{
    public static bool DistanceSendRate = true;
    public static bool TrackingHysteresis = true;
    public static bool AttributeNoOpSkip = true;

    /// <summary>Untrack only beyond range x this. 1.0 = vanilla.</summary>
    public static double HysteresisFactor = 1.15;

    public static long StatPositionsSent, StatPositionsSkipped, StatHysteresisHolds,
                       StatAttrPathsSent, StatAttrPathsSkipped, StatAttrPacketsSuppressed, StatCapOrderings;

    private static readonly AccessTools.FieldRef<PhysicsManager, ServerSystemEntitySimulation> EsRef =
        AccessTools.FieldRefAccess<PhysicsManager, ServerSystemEntitySimulation>("es");
    private static readonly AccessTools.FieldRef<ServerSystemEntitySimulation, int> TrackingRangeSqRef =
        AccessTools.FieldRefAccess<ServerSystemEntitySimulation, int>("trackingRangeSq");
    private static readonly AccessTools.FieldRef<PhysicsManager, double[]> PositionsRef =
        AccessTools.FieldRefAccess<PhysicsManager, double[]>("positions");
    private static readonly AccessTools.FieldRef<PhysicsManager, List<ConnectedClient>> ClientListRef =
        AccessTools.FieldRefAccess<PhysicsManager, List<ConnectedClient>>("ClientList");
    private static readonly AccessTools.FieldRef<PhysicsManager, List<long>> AlreadyTrackedRef =
        AccessTools.FieldRefAccess<PhysicsManager, List<long>>("alreadyTracked");
    private static readonly AccessTools.FieldRef<PhysicsManager, List<Entity>> NewlyTrackedRef =
        AccessTools.FieldRefAccess<PhysicsManager, List<Entity>>("newlyTracked");
    private static readonly AccessTools.FieldRef<PhysicsManager, EntityDespawnData> OutOfRangeRef =
        AccessTools.FieldRefAccess<PhysicsManager, EntityDespawnData>("outofRangeDespawnData");
    private static readonly AccessTools.FieldRef<PhysicsManager, ConcurrentDictionary<long, EntityTagPacket>> TagPacketsRef =
        AccessTools.FieldRefAccess<PhysicsManager, ConcurrentDictionary<long, EntityTagPacket>>("entitiesTagPackets");
    private static readonly AccessTools.FieldRef<PhysicsManager, ServerUdpNetwork> UdpRef =
        AccessTools.FieldRefAccess<PhysicsManager, ServerUdpNetwork>("udpNetwork");
    private static readonly AccessTools.FieldRef<Entity, bool> TagsDirty =
        AccessTools.FieldRefAccess<Entity, bool>("tagsDirty");

    /// <summary>Advances once per server tick; the phase of the distance-thinned sends.</summary>
    private static int tickIndex;
    private static int pruneCountdown = 1500;
    private static IServerWorldAccessor world;

    /// <summary>Last bytes sent per entity and attribute path (rule 4).</summary>
    private static readonly ConcurrentDictionary<long, Dictionary<string, byte[]>> lastSent = new();

    public static void Apply(Harmony harmony, IServerWorldAccessor serverWorld)
    {
        world = serverWorld;
        var pm = typeof(PhysicsManager);
        harmony.Patch(AccessTools.Method(pm, nameof(PhysicsManager.ServerTick), [typeof(float)])
                      ?? throw new InvalidOperationException("PhysicsManager.ServerTick not found"),
            prefix: new HarmonyMethod(typeof(EntitySyncPatches), nameof(ServerTickPrefix)));
        harmony.Patch(AccessTools.Method(pm, nameof(PhysicsManager.SendPositionsAndAnimations))
                      ?? throw new InvalidOperationException("PhysicsManager.SendPositionsAndAnimations not found"),
            prefix: new HarmonyMethod(typeof(EntitySyncPatches), nameof(SendPrefix)));
        harmony.Patch(AccessTools.Method(pm, "UpdateTrackedEntityState")
                      ?? throw new InvalidOperationException("PhysicsManager.UpdateTrackedEntityState not found"),
            prefix: new HarmonyMethod(typeof(EntitySyncPatches), nameof(TrackedStatePrefix)));
        harmony.Patch(AccessTools.Method(pm, "UpdateTrackedEntityLists")
                      ?? throw new InvalidOperationException("PhysicsManager.UpdateTrackedEntityLists not found"),
            prefix: new HarmonyMethod(typeof(EntitySyncPatches), nameof(TrackedListsPrefix)));
        harmony.Patch(AccessTools.Method(pm, "BuildAttributesPackets")
                      ?? throw new InvalidOperationException("PhysicsManager.BuildAttributesPackets not found"),
            prefix: new HarmonyMethod(typeof(EntitySyncPatches), nameof(AttributesPrefix)));

        // a full entity packet resets what "last sent" means for that entity
        var sp = typeof(ServerPackets);
        var invalidate = new HarmonyMethod(typeof(EntitySyncPatches), nameof(InvalidateAfterFullPacket));
        harmony.Patch(AccessTools.Method(sp, nameof(ServerPackets.GetEntityPacket), [typeof(FastMemoryStream), typeof(Entity)])
                      ?? throw new InvalidOperationException("ServerPackets.GetEntityPacket(ms, entity) not found"), postfix: invalidate);
        harmony.Patch(AccessTools.Method(sp, nameof(ServerPackets.GetEntityPacket), [typeof(Entity), typeof(FastMemoryStream), typeof(System.IO.BinaryWriter)])
                      ?? throw new InvalidOperationException("ServerPackets.GetEntityPacket(entity, ms, writer) not found"), postfix: invalidate);
        harmony.Patch(AccessTools.Method(sp, nameof(ServerPackets.GetEntityPacket), [typeof(Entity)])
                      ?? throw new InvalidOperationException("ServerPackets.GetEntityPacket(entity) not found"), postfix: invalidate);
    }

    // ---- the rules, pure ------------------------------------------------------------------

    /// <summary>Send every Nth physics tick, by squared distance to the client.</summary>
    internal static int SendDivisor(double distSq)
        => distSq < 40.0 * 40.0 ? 1 : distSq < 80.0 * 80.0 ? 2 : 3;

    /// <summary>Whether this tick is the entity's turn; the id spreads entities over the ticks.</summary>
    internal static bool ShouldSend(int divisor, int tick, long entityId)
    {
        if (divisor <= 1) return true;
        var phase = (tick + entityId) % divisor;
        if (phase < 0) phase += divisor;
        return phase == 0;
    }

    /// <summary>In range, or already tracked and still inside the hysteresis band.</summary>
    internal static bool InTrackingRange(double distSq, double rangeSq, bool alreadyTracked, double factor)
        => distSq < rangeSq || (alreadyTracked && distSq < rangeSq * factor * factor);

    /// <summary>Whether a partial update for a path is redundant against the last sent bytes.</summary>
    internal static bool SameAsLastSent(byte[] last, bool hadLast, byte[] now)
    {
        if (!hadLast) return false;
        if (last == null || now == null) return last == null && now == null;
        return last.AsSpan().SequenceEqual(now);
    }

    // ---- tick counter + cache upkeep --------------------------------------------------------

    public static void ServerTickPrefix()
    {
        Interlocked.Increment(ref tickIndex);
        if (--pruneCountdown > 0) return;
        pruneCountdown = 1500;
        var loaded = world?.LoadedEntities;
        if (loaded == null) return;
        foreach (var id in lastSent.Keys)
            if (!loaded.ContainsKey(id)) lastSent.TryRemove(id, out _);
    }

    public static void InvalidateAfterFullPacket(Entity entity)
    {
        if (entity != null) lastSent.TryRemove(entity.EntityId, out _);
    }

    // ---- rule 1: SendPositionsAndAnimations ------------------------------------------------

    public static bool SendPrefix(PhysicsManager __instance, Dictionary<long, Packet_EntityPosition> entityPositionPackets,
        Dictionary<long, AnimationPacket> entityAnimPackets, int zeroBasedThreadNum, bool stateUpdateTick)
    {
        if (!DistanceSendRate) return true;
        var clientList = ClientListRef(__instance);
        var tags = TagPacketsRef(__instance);
        var udp = UdpRef(__instance);
        if (clientList == null || tags == null || udp == null) return true;
        var channel = __instance.AnimationsAndTagsChannel;
        var tick = Volatile.Read(ref tickIndex);

        var list = new List<Packet_EntityPosition>();
        var list2 = new List<AnimationPacket>();
        var list3 = new List<EntityTagPacket>();
        foreach (var client in clientList)
        {
            list.Clear();
            list2.Clear();
            list3.Clear();
            var cpos = client.Position;
            if (stateUpdateTick)
            {
                var tracked = client.threadedTrackedEntities[zeroBasedThreadNum];
                foreach (var item in tracked)
                {
                    var id = item.EntityId;
                    if (entityPositionPackets.TryGetValue(id, out var value)) Consider(list, value, cpos, tick);
                    if (entityAnimPackets.TryGetValue(id, out var value2)) list2.Add(value2);
                    if (tags.TryGetValue(id, out var value3)) list3.Add(value3);
                }
                if (entityAnimPackets.TryGetValue(client.Entityplayer.EntityId, out var value4) && !tracked.Contains(client.Entityplayer))
                    list2.Add(value4);
            }
            else
            {
                foreach (var id in client.TrackedEntities)
                {
                    if (entityPositionPackets.TryGetValue(id, out var value5)) Consider(list, value5, cpos, tick);
                    if (entityAnimPackets.TryGetValue(id, out var value6)) list2.Add(value6);
                    if (tags.TryGetValue(id, out var value7)) list3.Add(value7);
                }
            }

            var count = list.Count;
            if (count > 8 && !client.IsSinglePlayerClient && !client.FallBackToTcp)
            {
                for (var i = 0; i < count; i += 8)
                {
                    var array = new Packet_EntityPosition[Math.Min(8, count - i)];
                    for (var j = 0; j < array.Length; j++) array[j] = list[i + j];
                    var bulk = new Packet_BulkEntityPosition();
                    bulk.SetEntityPositions(array);
                    udp.SendPacket_Threadsafe(client, bulk);
                }
            }
            else if (count > 0)
            {
                var bulk2 = new Packet_BulkEntityPosition();
                bulk2.SetEntityPositions(list.ToArray());
                udp.SendPacket_Threadsafe(client, bulk2);
            }
            if (list2.Count > 0)
                channel.SendPacket(new BulkAnimationPacket { Packets = list2.ToArray() }, client.Player);
            if (list3.Count <= 0) continue;
            foreach (var item2 in list3) channel.SendPacket(item2, client.Player);
        }
        return false;
    }

    private static void Consider(List<Packet_EntityPosition> list, Packet_EntityPosition packet, EntityPos clientPos, int tick)
    {
        if (clientPos == null || packet.Teleport)
        {
            list.Add(packet);
            Interlocked.Increment(ref StatPositionsSent);
            return;
        }
        var dx = packet.X / 16384.0 - clientPos.X;
        var dy = packet.Y / 16384.0 - clientPos.Y;
        var dz = packet.Z / 16384.0 - clientPos.Z;
        if (ShouldSend(SendDivisor(dx * dx + dy * dy + dz * dz), tick, packet.EntityId))
        {
            list.Add(packet);
            Interlocked.Increment(ref StatPositionsSent);
        }
        else Interlocked.Increment(ref StatPositionsSkipped);
    }

    // ---- rule 2: UpdateTrackedEntityState ---------------------------------------------------

    public static bool TrackedStatePrefix(PhysicsManager __instance, Entity entity, List<ConnectedClient> clients,
        int zeroBasedThreadNum, ref bool __result)
    {
        if (!TrackingHysteresis) return true;
        var es = EsRef(__instance);
        var array = PositionsRef(__instance);
        if (es == null || array == null) return true;

        var pos = entity.Pos;
        double x = pos.X, y = pos.Y, z = pos.Z;
        var nearest = double.MaxValue;
        var simSq = (double)entity.SimulationRange * entity.SimulationRange;
        var rangeSq = Math.Max(TrackingRangeSqRef(es), simSq);
        var id = entity.EntityId;
        var chunkIndex = entity.InChunkIndex3d;
        var outside = entity.AllowOutsideLoadedRange;
        var k = 0;
        foreach (var client in clients)
        {
            var dx = x - array[k];
            var dy = y - array[k + 1];
            var dz = z - array[k + 2];
            k += 3;
            var d = dx * dx + dz * dz + dy * dy;
            if (d < nearest) nearest = d;
            var tracked = d < rangeSq;
            if (!tracked && InTrackingRange(d, rangeSq, client.TrackedEntities.Contains(id), HysteresisFactor))
            {
                tracked = true;
                Interlocked.Increment(ref StatHysteresisHolds);
            }
            if (tracked && ((client.DidSendChunk(chunkIndex) || id == client.Player.Entity.EntityId) | outside))
                client.threadedTrackedEntities[zeroBasedThreadNum].Add(entity);
        }
        if (nearest < rangeSq)
        {
            entity.IsTracked = (byte)((nearest >= 2500.0) ? 1 : 2);
        }
        else
        {
            entity.IsTracked = 0;
            if (!(entity is EntityPlayer))
            {
                entity.PreviousServerPos.SetFrom(entity.Pos);
                entity.IsTeleport = false;
            }
        }
        entity.NearestPlayerDistance = (float)Math.Sqrt(nearest);
        __result = false;
        if (!entity.AlwaysActive)
        {
            var active = nearest < simSq;
            if (active != (entity.State == EnumEntityState.Active))
            {
                entity.State = active ? EnumEntityState.Active : EnumEntityState.Inactive;
                __result = true;
            }
        }
        return false;
    }

    // ---- rule 3: UpdateTrackedEntityLists ---------------------------------------------------

    public static bool TrackedListsPrefix(PhysicsManager __instance, ConnectedClient client, int threadCount)
    {
        if (!TrackingHysteresis) return true;
        var alreadyTracked = AlreadyTrackedRef(__instance);
        var newlyTracked = NewlyTrackedRef(__instance);
        var outOfRange = OutOfRangeRef(__instance);
        if (alreadyTracked == null || newlyTracked == null || outOfRange == null) return true;

        var threaded = client.threadedTrackedEntities;
        var list = threaded[0];
        var trackedEntities = client.TrackedEntities;
        alreadyTracked.EnsureCapacity(trackedEntities.Count);
        foreach (var item in list)
        {
            long entityId;
            if (trackedEntities.Remove(entityId = item.EntityId)) alreadyTracked.Add(entityId);
            else newlyTracked.Add(item);
        }
        for (var i = 1; i < threadCount; i++)
        {
            foreach (var item2 in threaded[i])
            {
                long entityId;
                if (trackedEntities.Remove(entityId = item2.EntityId)) alreadyTracked.Add(entityId);
                else newlyTracked.Add(item2);
                list.Add(item2);
            }
            threaded[i].Clear();
        }
        foreach (var item3 in trackedEntities)
        {
            client.entitiesNowOutOfRange.Add(new EntityDespawn
            {
                ForClientId = client.Id,
                DespawnData = outOfRange,
                EntityId = item3
            });
        }
        trackedEntities.Clear();
        trackedEntities.AddRange(alreadyTracked);
        alreadyTracked.Clear();

        // the one added line: when the cap will bite, the nearest entities are admitted first
        var cap = MagicNum.TrackedEntitiesPerClient;
        var cpos = client.Position;
        if (trackedEntities.Count + newlyTracked.Count > cap && cpos != null)
        {
            newlyTracked.Sort((a, b) => DistSq(a, cpos).CompareTo(DistSq(b, cpos)));
            Interlocked.Increment(ref StatCapOrderings);
        }
        foreach (var item4 in newlyTracked)
        {
            if (trackedEntities.Count >= cap) break;
            trackedEntities.Add(item4.EntityId);
            client.entitiesNowInRange.Add(new EntityInRange
            {
                ForClientId = client.Id,
                Entity = item4
            });
        }
        newlyTracked.Clear();
        return false;
    }

    private static double DistSq(Entity e, EntityPos p)
    {
        var pos = e.Pos;
        double dx = pos.X - p.X, dy = pos.Y - p.Y, dz = pos.Z - p.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    // ---- rule 4: BuildAttributesPackets -----------------------------------------------------

    public static bool AttributesPrefix(Entity entity, FastMemoryStream ms, bool debugMode,
        Dictionary<long, Packet_EntityAttributes> eFullUpdate, Dictionary<long, Packet_EntityAttributeUpdate> ePartialUpdate,
        Dictionary<long, Packet_EntityAttributes> eDebugUpdate, ref EntityTagPacket __result)
    {
        if (!AttributeNoOpSkip) return true;
        EntityTagPacket result = null;
        var watched = entity.WatchedAttributes;
        if (watched.AllDirty)
        {
            ms.Reset();
            eFullUpdate[entity.EntityId] = ServerPackets.GetEntityPacket(ms, entity);
            TagsDirty(entity) = false;
        }
        else
        {
            if (watched.PartialDirty)
            {
                ms.Reset();
                var partial = Filter(entity.EntityId, ServerPackets.GetEntityPartialAttributePacket(ms, entity));
                if (partial != null) ePartialUpdate[entity.EntityId] = partial;
            }
            if (TagsDirty(entity))
            {
                result = ServerPackets.GetEntityTagPacket(entity);
                TagsDirty(entity) = false;
            }
        }
        if (debugMode && (entity.DebugAttributes.AllDirty || entity.DebugAttributes.PartialDirty))
        {
            ms.Reset();
            eDebugUpdate[entity.EntityId] = ServerPackets.GetEntityDebugAttributePacket(ms, entity);
        }
        watched.MarkClean();
        __result = result;
        return false;
    }

    /// <summary>Drops the paths whose bytes match what was last sent; null when nothing is left.</summary>
    internal static Packet_EntityAttributeUpdate Filter(long entityId, Packet_EntityAttributeUpdate packet)
    {
        var attrs = packet?.Attributes;
        if (attrs == null) return packet;
        var n = Math.Min(packet.AttributesCount, attrs.Length);
        var cache = lastSent.GetOrAdd(entityId, _ => new Dictionary<string, byte[]>(8));
        Packet_PartialAttribute[] kept = null;
        var keep = 0;
        for (var i = 0; i < n; i++)
        {
            var a = attrs[i];
            if (a == null) continue;
            var path = a.Path ?? "";
            var had = cache.TryGetValue(path, out var last);
            if (SameAsLastSent(last, had, a.Data))
            {
                Interlocked.Increment(ref StatAttrPathsSkipped);
                continue;
            }
            cache[path] = a.Data;
            kept ??= new Packet_PartialAttribute[n];
            kept[keep++] = a;
            Interlocked.Increment(ref StatAttrPathsSent);
        }
        if (keep == 0)
        {
            Interlocked.Increment(ref StatAttrPacketsSuppressed);
            return null;
        }
        if (keep < n)
        {
            Array.Resize(ref kept, keep);
            packet.SetAttributes(kept);
        }
        return packet;
    }

    public static void ResetStats()
    {
        StatPositionsSent = StatPositionsSkipped = StatHysteresisHolds = 0;
        StatAttrPathsSent = StatAttrPathsSkipped = StatAttrPacketsSuppressed = StatCapOrderings = 0;
    }

    /// <summary>World shutdown: the cache keys are entity ids of a world that is gone.</summary>
    public static void Clear()
    {
        lastSent.Clear();
        world = null;
    }
}
