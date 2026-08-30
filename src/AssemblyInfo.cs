using Vintagestory.API.Common;

[assembly: ModInfo(
    "Komet",
    "komet",
    Version = "1.1.0",
    Side = "Universal",
    RequiredOnClient = false,
    RequiredOnServer = false,
    Description = "Client-side performance mod: faster chunk visibility sweep and occlusion culling, distance culling for block entity renderers, stabilised shadow texels, smoother chunk loading with adaptive inflow, VRAM pool reclaiming - plus an F7 performance HUD with per-renderer timings, hitch log and built-in stress test. Gains grow with view distance.",
    Authors = new[] { "xToast" })]
