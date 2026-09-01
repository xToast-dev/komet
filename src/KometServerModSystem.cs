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
    }

    private void Apply(ICoreAPI api)
    {
        // The mod's value is authoritative: MagicNum.Load already ran and filled the statics
        // from servermagicnumbers.json, and whatever that file says is deliberately ignored
        // here - one source of truth, and it is komet.json. A value outside the usable range
        // is clamped rather than refused; 1 behaves exactly like vanilla.
        var threads = Math.Clamp(config.ServerWorldgenThreads, 1, 6);
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
