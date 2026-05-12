using System.Drawing.Drawing2D;

namespace ProxySwitch.UI;

/// <summary>
/// Rounded pill-style button — Stitch design uses these for Pinned Apps and
/// a few "+ Add" actions. Drawn via GraphicsPath rather than relying on
/// Windows' native button chrome (which doesn't support pill shape).
/// Two variants: PillVariant.Default (subtle gray) and PillVariant.Primary
/// (blue, used for primary CTAs).
/// </summary>
public sealed class PillButton : Button
{
    public enum PillVariant { Default, Primary }

    private PillVariant _variant = PillVariant.Default;
    public PillVariant Variant
    {
        get => _variant;
        set { _variant = value; Invalidate(); }
    }

    /// <summary>Optional accent dot shown to the left of the text — used to mark
    /// a pinned app's proxy (green for clash, blue for v2ray, gray for direct).</summary>
    public Color? AccentDot { get; set; }

    public PillButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 4, 12, 4);
        Margin = new Padding(4, 2, 4, 2);
        Font = Theme.BodyFont;
        TextAlign = ContentAlignment.MiddleCenter;
        UseCompatibleTextRendering = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        // BackColor doesn't matter — the Region clip below removes the corner
        // pixels at the Win32 level so the parent shows through there.
        BackColor = Theme.PanelBg;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var r = new RectangleF(0, 0, Width, Height);
        using var path = RoundedRect(r, Math.Min(r.Width, r.Height) / 2f);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        using var path = RoundedRect(r, Math.Min(r.Width, r.Height) / 2f);

        Color fill, fore, border;
        if (Variant == PillVariant.Primary)
        {
            fill = Theme.PrimaryBlue;
            fore = Theme.OnPrimary;
            border = Theme.PrimaryBlue;
        }
        else
        {
            fill = Theme.PanelBg;
            fore = Theme.TextPrimary;
            border = Theme.BorderSubtle;
        }

        if (!Enabled)
        {
            fill = Theme.SurfaceContainerLow;
            fore = Theme.TextSecondary;
        }

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        // Border: inset by 0.5px so AA stroke doesn't get clipped by the Region.
        var borderPath = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f),
            Math.Min(Width, Height) / 2f - 0.5f);
        using (var pen = new Pen(border, 1f))
            g.DrawPath(pen, borderPath);
        borderPath.Dispose();

        int textX = Padding.Left;
        if (AccentDot.HasValue)
        {
            int d = (int)(Height * 0.40f);
            int dotY = (Height - d) / 2;
            using var brush = new SolidBrush(AccentDot.Value);
            g.FillEllipse(brush, textX, dotY, d, d);
            textX += d + 6;
        }

        // Render label using TextRenderer for ClearType-quality glyphs.
        var textBounds = new Rectangle(textX, 0, Width - textX - Padding.Right, Height);
        TextRenderer.DrawText(g, Text, Font, textBounds, fore,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.StartFigure();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
