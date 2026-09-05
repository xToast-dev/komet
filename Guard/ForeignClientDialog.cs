using Vintagestory.API.Client;

namespace Komet.Guard;

/// <summary>
/// The "hey, this is incompatible" box: title, the text, one button. Laid out exactly like
/// the engine's own GuiDialogConfirm (same bounds, same font, same button row), so it reads
/// as something the game says rather than something drawn over it. Opened once per world
/// join by KometModSystem, a moment after LevelFinalize, so it lands on the game screen and
/// not under the loading screen.
/// </summary>
public sealed class ForeignClientDialog : GuiDialog
{
    private readonly string title;
    private readonly string text;
    private readonly string button;

    public override string ToggleKeyCombinationCode => null;

    /// <summary>Above HUD and chat, like the engine's confirm box.</summary>
    public override double DrawOrder => 2.0;

    public ForeignClientDialog(ICoreClientAPI capi, string title, string text, string button) : base(capi)
    {
        this.title = title;
        this.text = text;
        this.button = button;
    }

    public override void OnGuiOpened()
    {
        Compose();
        base.OnGuiOpened();
    }

    private void Compose()
    {
        var textBounds = ElementStdBounds.Rowed(0.4f, 0.0, EnumDialogArea.LeftFixed).WithFixedWidth(520.0);
        var bg = ElementStdBounds.DialogBackground()
            .WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        var font = CairoFont.WhiteSmallText();
        var height = (float)new TextDrawUtil().GetMultilineTextHeight(font, text, textBounds.fixedWidth);
        SingleComposer = capi.Gui.CreateCompo("kometforeignclient", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(title, () => TryClose())
            .BeginChildElements(bg)
            .AddStaticText(text, font, textBounds)
            .AddSmallButton(button, () => { TryClose(); return true; },
                ElementStdBounds.MenuButton((height + 80f) / 80f).WithAlignment(EnumDialogArea.RightFixed).WithFixedPadding(6.0))
            .EndChildElements()
            .Compose();
    }
}
