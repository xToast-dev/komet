using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace Komet;

/// <summary>
/// The one home for the ClientMain tesselation-queue field refs. Three classes used to
/// carry their own copies of these four accessors (the tesselation patches, the window
/// prebuilder and the edge-retess priority sweep) - four copies of the same field-name
/// strings is three places for a rename after a game update to slip through.
///
/// The fields are internal on ClientMain, hence FieldRef instead of direct access. A wrong
/// name throws in this type's static initializer, which surfaces inside the first feature's
/// Patch() wrapper as a logged failure - the same failure mode the local copies had.
/// </summary>
internal static class ClientQueues
{
    /// <summary>ClientSystem's internal back reference to the game - the same instance every
    /// system carries; handy in patches whose __instance is some ClientSystem subclass.</summary>
    internal static readonly AccessTools.FieldRef<ClientSystem, ClientMain> GameOf =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");

    /// <summary>The normal tesselation queue. Sign bit on a key = edge-only re-tesselation.</summary>
    internal static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> Dirty =
        AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunks");

    /// <summary>The priority queue - drained completely before the normal queue.</summary>
    internal static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> DirtyPrio =
        AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunksPriority");

    internal static readonly AccessTools.FieldRef<ClientMain, object> DirtyLock =
        AccessTools.FieldRefAccess<ClientMain, object>("dirtyChunksLock");

    internal static readonly AccessTools.FieldRef<ClientMain, object> DirtyPrioLock =
        AccessTools.FieldRefAccess<ClientMain, object>("dirtyChunksPriorityLock");
}
