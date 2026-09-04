using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Turns on the engine's own persistent-buffer path for chunk meshes.
///
/// ClientPlatformWindows allocates every dynamic VBO as
/// <c>allowPStorage &amp;&amp; supportsPersistentMapping &amp;&amp; !staticDraw</c>. It detects
/// GL_ARB_buffer_storage into <c>supportsPersistentMapping</c> at startup, and there is a full
/// persistent write path behind it - but <c>allowPStorage</c> is never assigned anywhere in
/// 1.22.7, so the flag is always false and every chunk upload goes through glBufferSubData
/// into a buffer the GPU may still be reading, which is where the upload spikes come from.
///
/// Flipping it on switches chunk VBOs to GL_MAP_PERSISTENT | GL_MAP_COHERENT storage, and
/// makes <see cref="MeshUploadPatches"/> (which replaces the engine's scalar store loop with a
/// bulk copy) actually do something.
///
/// EXPERIMENTAL, and off by default for a good reason: a code path the developers never
/// enable is a code path they never tested. If terrain renders wrong, set
/// ExperimentalPersistentMapping back to false.
/// </summary>
public static class PersistentMappingPatch
{
    public static bool Available { get; private set; }
    public static bool Enabled { get; private set; }

    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> SupportsRef =
        AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("supportsPersistentMapping");

    /// <summary>Reports what the driver offers without changing anything.</summary>
    public static void Probe(ILogger logger)
    {
        if (ScreenManager.Platform is not ClientPlatformWindows platform) return;

        Available = SupportsRef(platform);
        Enabled = platform.allowPStorage;

        logger.Notification(
            "persistent buffer mapping: driver {0}, engine flag {1}{2}",
            Available ? "supports it" : "does NOT support it",
            Enabled ? "on" : "off",
            !Enabled && Available ? " (vanilla never turns this on; ExperimentalPersistentMapping can)" : "");
    }

    /// <summary>Must run before the chunk mesh pools are allocated, i.e. before joining a world.</summary>
    public static void Enable(ILogger logger)
    {
        if (ScreenManager.Platform is not ClientPlatformWindows platform)
            throw new InvalidOperationException("ClientPlatformWindows not active");

        if (!SupportsRef(platform))
        {
            logger.Notification("ExperimentalPersistentMapping requested but the driver has no GL_ARB_buffer_storage - staying on glBufferSubData");
            return;
        }

        platform.allowPStorage = true;
        Enabled = true;
        logger.Notification("ExperimentalPersistentMapping ON - chunk VBOs will use persistent coherent mapping");
    }
}
