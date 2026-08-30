using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Komet.Measure;

namespace KometBaseline;

/// <summary>
/// The same performance HUD as Komet, with none of the optimisations - a measuring stick for
/// vanilla.
///
/// It shares its measurement code with the optimising mod file for file (../shared),
/// so a number here and a number there mean exactly the same thing and can be subtracted.
/// The only Harmony patches it installs are the timing prefixes and postfixes, which read a
/// clock and change no behaviour.
///
/// Workflow: enable this one and note the numbers, then swap to Komet and compare.
/// </summary>
public class BaselineModSystem : ModSystem
{
    private Harmony harmony;
    private ICoreClientAPI capi;
    private DebugHud hud;
    private GpuFrameTimer.BeginRenderer gpuBegin;
    private GpuFrameTimer.EndRenderer gpuEnd;
    private Action cameraSampler;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    /// <summary>Same slot as Komet, so the timing brackets sit in the same place.</summary>
    public override double ExecuteOrder() => 0.05;

    public override void Start(ICoreAPI api)
    {
        // Running both at once would mean two overlapping overlays and, more importantly, a
        // "baseline" that is not one. Refuse instead of quietly measuring the wrong thing.
        // "vsperf" is the modid Komet shipped under before the rename - a tester with the old
        // DLL still lying in Mods would otherwise get a baseline that is not one.
        if (api.ModLoader.IsModEnabled("komet") || api.ModLoader.IsModEnabled("vsperf"))
        {
            Mod.Logger.Notification(
                "komet ist aktiv - Baseline haelt sich raus. Zum Vergleichen: komet im Mod-Manager " +
                "deaktivieren, Spiel neu starten, Zahlen notieren, dann wieder aktivieren.");
            return;
        }

        harmony = new Harmony(Mod.Info.ModID);
        try
        {
            MeasurementPatches.Apply(harmony);
            Mod.Logger.Notification("Messung aktiv (nur Zeitmessung, keine Optimierung)");
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Messung konnte nicht installiert werden:");
            Mod.Logger.Error(e);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (harmony == null) return; // stood down because komet is loaded
        capi = api;

        hud = new DebugHud(api, "vanilla " + KometVersion.Display(Mod.Info.Version));

        // The GPU frame timer is pure measurement, so the baseline carries it too - the
        // CPU-vs-GPU comparison is exactly the kind of number the baseline exists to anchor.
        GpuFrameTimer.Enabled = true;
        gpuBegin = new GpuFrameTimer.BeginRenderer();
        gpuEnd = new GpuFrameTimer.EndRenderer();
        api.Event.RegisterRenderer(gpuBegin, EnumRenderStage.Before, "kometbgpu0");
        api.Event.RegisterRenderer(gpuEnd, EnumRenderStage.Done, "kometbgpu1");
        api.Event.RegisterRenderer(hud, EnumRenderStage.Ortho, "kometbasehud");

        api.Input.RegisterHotKey("kometbasehud", "Baseline: Performance-HUD", GlKeys.F7, HotkeyType.HelpAndOverlays);
        api.Input.SetHotKeyHandler("kometbasehud", _ => { hud.Visible = !hud.Visible; return true; });

        // The hitch log is pure measurement and lives in shared/, so the baseline records the
        // very same thing - "ruckler/min vanilla vs komet" is then a legitimate comparison.
        HitchLog.Log = msg => Mod.Logger.Notification(msg);
        HitchLog.CommandHint = "client-main.log";
        cameraSampler = () =>
        {
            Vintagestory.API.Common.Entities.EntityPos pos = capi?.World?.Player?.Entity?.Pos;
            if (pos != null) HitchLog.NoteCamera(pos.Yaw, pos.Pitch, pos.X, pos.Y, pos.Z);
        };
        MeasurementPatches.FrameBoundary += cameraSampler;

        api.ChatCommands.Create("vsbase")
            .WithDescription("Vanilla-Performance-Messung: HUD umschalten")
            .HandleWith(_ =>
            {
                hud.Visible = !hud.Visible;
                return TextCommandResult.Success(hud.Visible ? "Baseline-HUD an (F7)" : "Baseline-HUD aus");
            });
    }

    public override void Dispose()
    {
        if (cameraSampler != null)
        {
            MeasurementPatches.FrameBoundary -= cameraSampler;
            cameraSampler = null;
            HitchLog.Log = null;
        }
        if (gpuBegin != null && capi != null)
        {
            capi.Event.UnregisterRenderer(gpuBegin, EnumRenderStage.Before);
            capi.Event.UnregisterRenderer(gpuEnd, EnumRenderStage.Done);
            gpuBegin = null;
            gpuEnd = null;
            GpuFrameTimer.Enabled = false;
        }
        if (hud != null && capi != null)
        {
            capi.Event.UnregisterRenderer(hud, EnumRenderStage.Ortho);
            hud.Dispose();
            hud = null;
        }
        harmony?.UnpatchAll(harmony.Id);
        base.Dispose();
    }
}
