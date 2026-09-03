using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Server;

namespace Komet;

/// <summary>
/// The server half of the chunk loading work - in singleplayer the server runs inside the
/// same process, throttled by the conservative defaults in servermagicnumbers.json (one
/// worldgen thread, small queues). Those values are plain static fields, so instead of
/// editing the user's config file this sets them in memory, every world start, only while
/// the mod is installed. Removing the mod restores stock behaviour with no file cleanup.
///
/// Timing that makes this work: ServerMain.Launch constructs the ChunkServerThread (which
/// derives its extra-thread count from MagicNum) in run phase Start, BEFORE mods load - but
/// the additional worldgen threads are only started at GameReady, well AFTER. So the statics
/// get set for everything that reads them later, and the already-computed
/// additionalWorldGenThreadsCount field is corrected directly.
///
/// Why the default is 4 threads and not the maximum: with 6 the server delivered over 3000
/// chunks/s into the client, and the flood throttled the client's own tesselation from ~400
/// to ~53 chunks/s through lock contention - a firehose is not a faster way to fill a glass.
/// </summary>
public class KometServerModSystem : ModSystem
{
    private KometConfig config;
    private Harmony harmony;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    /// <summary>Before other mods, and long before GameReady starts the worldgen threads.</summary>
    public override double ExecuteOrder() => 0.05;

    private static readonly AccessTools.FieldRef<ServerMain, ChunkServerThread> ChunkThreadRef =
        AccessTools.FieldRefAccess<ServerMain, ChunkServerThread>("chunkThread");

    public override void StartPre(ICoreAPI api)
    {
        try
        {
            config = api.LoadModConfig<KometConfig>("komet.json") ?? new KometConfig();
        }
        catch
        {
            config = new KometConfig(); // the client side owns writing the file
        }

        try
        {
            Apply(api);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not tune the server chunk pipeline, running with vanilla values. This is safe.");
            Mod.Logger.Error(e);
        }

        // The entity sync rules are Harmony patches on PhysicsManager, applied here with the
        // server's own Harmony id so the client half's UnpatchAll never touches them (in
        // singleplayer both halves share the process). Always applied, gated at runtime:
        // the client's '.komet toggle entsync|attrskip' flips the statics directly.
        try
        {
            harmony ??= new Harmony(Mod.Info.ModID + ".server");
            Patches.EntitySyncPatches.Apply(harmony, api.World as Vintagestory.API.Server.IServerWorldAccessor);
            Patches.EntitySyncPatches.DistanceSendRate = config.ServerEntitySyncTuning;
            Patches.EntitySyncPatches.TrackingHysteresis = config.ServerEntitySyncTuning;
            Patches.EntitySyncPatches.AttributeNoOpSkip = config.ServerAttributeNoOpSkip;
            Mod.Logger.Notification("enabled: entity sync tuning {0} (distance send rate, tracking hysteresis, nearest-first cap), attribute no-op skip {1}",
                config.ServerEntitySyncTuning ? "on" : "off (vanilla)", config.ServerAttributeNoOpSkip ? "on" : "off (vanilla)");
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not enable the entity sync tuning, running vanilla entity sync. This is safe.");
            Mod.Logger.Error(e);
        }

        // Allocation attribution per server thread and suspect - measurement only, so the
        // client's report can name what used to be "rest" in its alloc-quellen line.
        try
        {
            harmony ??= new Harmony(Mod.Info.ModID + ".server");
            Patches.ServerAllocPatches.Apply(harmony);
            Patches.ServerAllocPatches.Enabled = config.ServerAllocAttribution;
            Mod.Logger.Notification("server allocation attribution {0}", config.ServerAllocAttribution ? "on" : "off");
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not enable the server allocation attribution. This is safe.");
            Mod.Logger.Error(e);
        }

        // Who sends single-block packets (ExchangeBlock / SetBlock) - measurement only, same
        // gate as the allocation attribution; the client's report prints the ranking.
        try
        {
            harmony ??= new Harmony(Mod.Info.ModID + ".server");
            Patches.PacketSourcePatches.Apply(harmony);
            Patches.PacketSourcePatches.Enabled = config.ServerAllocAttribution;
        }
        catch (Exception e)
        {
            Mod.Logger.Error("could not enable the block packet source sampling. This is safe.");
            Mod.Logger.Error(e);
        }
    }

    public override void Dispose()
    {
        Patches.EntitySyncPatches.Clear();
        Patches.ServerAllocPatches.Clear();
        Patches.PacketSourcePatches.Clear();
        harmony?.UnpatchAll(harmony.Id);
        harmony = null;
        base.Dispose();
    }

    /// <summary>
    /// The worldgen thread count actually applied: the configured value, capped so that the
    /// render thread and the client's tesselation thread keep a hardware thread each. On a
    /// 6c/12t desktop the cap never bites (10 >= 6); on a tester's 2c/4t laptop it takes the
    /// default 6 down to 2 - the old code put five extra generator threads on four hardware
    /// threads, next to the render thread, the tesselator and the cull helpers, and every
    /// GC then had to wait for all of them to be scheduled out. Vanilla's own rule is
    /// min(5, MaxWorldgenThreads-1) extra threads with no look at the machine at all.
    /// </summary>
    internal static int EffectiveWorldgenThreads(int configured, int logicalCores)
        => Math.Clamp(Math.Min(configured, logicalCores - 2), 1, 6);

    private void Apply(ICoreAPI api)
    {
        // The mod's value is authoritative: MagicNum.Load already ran and filled the statics
        // from servermagicnumbers.json, and whatever that file says is deliberately ignored
        // here - one source of truth, and it is komet.json. A value outside the usable range
        // is clamped rather than refused; 1 behaves exactly like vanilla.
        var wanted = Math.Clamp(config.ServerWorldgenThreads, 1, 6);
        var threads = EffectiveWorldgenThreads(config.ServerWorldgenThreads, CpuTopology.LogicalCores);
        MagicNum.MaxWorldgenThreads = threads;

        // The ChunkServerThread already computed its extra-thread count from the file's
        // value in ServerMain.Launch, before any mod ran. The threads themselves start
        // at GameReady, so correcting the field here still takes effect.
        if (api.World is ServerMain server)
        {
            var chunkThread = ChunkThreadRef(server);
            if (chunkThread != null && !server.ReducedServerThreads)
                chunkThread.additionalWorldGenThreadsCount = Math.Clamp(threads - 1, 0, 5);
        }
        if (threads < wanted)
            Mod.Logger.Notification("worldgen threads: {0} - configured {1}, capped at {2} hardware threads minus the render and tesselation threads (komet.json is authoritative, servermagicnumbers.json is ignored for this value)",
                threads, wanted, CpuTopology.LogicalCores);
        else
            Mod.Logger.Notification("worldgen threads: {0} (komet.json is authoritative, servermagicnumbers.json is ignored for this value)", threads);

        if (config.ServerRequestQueueSize > 0 && MagicNum.RequestChunkColumnsQueueSize < config.ServerRequestQueueSize)
        {
            // The engine itself logs "try increasing servermagicnumbers RequestChunkColumnsQueueSize"
            // when this overflows, which it does at view distance 1536.
            MagicNum.RequestChunkColumnsQueueSize = config.ServerRequestQueueSize;
            Mod.Logger.Notification("chunk request queue size: {0}", config.ServerRequestQueueSize);
        }

        if (config.ServerChunksColumnsPerTick > 0 && MagicNum.ChunksColumnsToRequestPerTick != config.ServerChunksColumnsPerTick)
        {
            MagicNum.ChunksColumnsToRequestPerTick = config.ServerChunksColumnsPerTick;
            Mod.Logger.Notification("chunk columns requested per tick: {0}", config.ServerChunksColumnsPerTick);
        }
    }
}
