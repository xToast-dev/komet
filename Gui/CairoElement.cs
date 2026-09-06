using Cairo;
using Vintagestory.API.Client;

namespace Komet.Gui;

/// <summary>
/// The Cairo pair both of this window's own elements draw into - the text panel and the frame
/// graph - and the one rule about it: the Context is disposed before the ImageSurface it draws
/// on, never the other way round.
///
/// The surface is the size of the ELEMENT, not of its content, and it is only ever reallocated
/// when the element's bounds change (a resized dialog, a different GUI scale). That is what
/// keeps the per-refresh upload constant instead of growing with a longer report.
/// </summary>
internal abstract class CairoElement(ICoreClientAPI capi, ElementBounds bounds)
    : GuiElement(capi, bounds)
{
    protected ImageSurface Surface;
    protected Context Ctx;
    protected int SurfW, SurfH;

    /// <summary>
    /// A surface matching the element's current inner bounds, reallocated only when they
    /// changed. False when the element is too small to draw anything into - the caller skips
    /// its refresh rather than making a degenerate surface.
    /// </summary>
    protected bool EnsureSurface(int minSize)
    {
        var w = (int)Bounds.InnerWidth;
        var h = (int)Bounds.InnerHeight;
        if (w < minSize || h < minSize) return false;
        if (Surface != null && w == SurfW && h == SurfH) return true;

        DisposeSurface();
        SurfW = w;
        SurfH = h;
        Surface = new ImageSurface(Format.Argb32, SurfW, SurfH);
        Ctx = new Context(Surface);
        return true;
    }

    /// <summary>The surface pair goes with the element. Subclasses dispose their own textures
    /// and then call base.Dispose().</summary>
    public override void Dispose() => DisposeSurface();

    private void DisposeSurface()
    {
        Ctx?.Dispose();
        Surface?.Dispose();
        Ctx = null;
        Surface = null;
    }

    /// <summary>Clears the surface to transparent and puts the operator back for drawing.</summary>
    protected void ClearSurface()
    {
        Ctx.Operator = Operator.Clear;
        Ctx.Paint();
        Ctx.Operator = Operator.Over;
    }
}
