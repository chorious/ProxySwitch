using System.Diagnostics;
using ProxySwitch.UI;

namespace ProxySwitch.Controls;

public sealed class LaunchZoneControl : Panel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "Drop app here";
    public string Mode { get; set; } = "direct";
    public IconRenderer.IconKind IconKind { get; set; } = IconRenderer.IconKind.Direct;

    private Color _accentColor = Color.Gray;
    public Color AccentColor
    {
        get => _accentColor;
        set
        {
            _accentColor = value;
            _normalFill = Color.FromArgb(28, value);   // ~11% tint
            _hoverFill = Color.FromArgb(72, value);    // ~28% tint while dragging
            Invalidate();
        }
    }

    private Color _normalFill = Color.FromArgb(28, Color.Gray);
    private Color _hoverFill = Color.FromArgb(72, Color.Gray);
    private bool _isHover;

    public event Action<string>? FileDropped;

    public LaunchZoneControl()
    {
        AllowDrop = true;
        BorderStyle = BorderStyle.None;   // we paint our own border
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;

        // Background fill — solid PanelBg, then a subtle accent tint laid on top.
        using (var bg = new SolidBrush(Theme.PanelBg))
            g.FillRectangle(bg, r);
        using (var tint = new SolidBrush(_isHover ? _hoverFill : _normalFill))
            g.FillRectangle(tint, r);

        // Border (thicker on hover so the active drop target is obvious).
        var borderWidth = _isHover ? 3f : 1f;
        var borderColor = _isHover ? AccentColor : Theme.BorderSubtle;
        var borderRect = new RectangleF(r.X + borderWidth / 2, r.Y + borderWidth / 2,
                                         r.Width - borderWidth, r.Height - borderWidth);
        using (var pen = new Pen(borderColor, borderWidth))
            g.DrawRectangle(pen, borderRect.X, borderRect.Y, borderRect.Width, borderRect.Height);

        // Icon — sized relative to zone, centered horizontally near the top.
        int iconSize = Math.Min(32, Math.Max(20, r.Height / 4));
        var iconRect = new RectangleF(
            (r.Width - iconSize) / 2f,
            r.Height * 0.18f,
            iconSize,
            iconSize);
        IconRenderer.Draw(g, iconRect, IconKind, AccentColor);

        // Title — centered horizontally, just under the icon.
        var titleSize = TextRenderer.MeasureText(Title, Theme.DropzoneTitle);
        var titleX = (r.Width - titleSize.Width) / 2;
        var titleY = (int)(iconRect.Bottom + 8);
        TextRenderer.DrawText(g, Title, Theme.DropzoneTitle,
            new Point(titleX, titleY), Theme.TextPrimary,
            TextFormatFlags.SingleLine);

        // Subtitle — secondary text under the title.
        var subSize = TextRenderer.MeasureText(Subtitle, Theme.DropzoneSubtitle);
        var subX = (r.Width - subSize.Width) / 2;
        var subY = titleY + titleSize.Height + 2;
        TextRenderer.DrawText(g, Subtitle, Theme.DropzoneSubtitle,
            new Point(subX, subY), Theme.TextSecondary,
            TextFormatFlags.SingleLine);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            _isHover = true;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _isHover = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _isHover = false;
        Invalidate();

        var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) return;

        var first = files[0];
        var ext = Path.GetExtension(first).ToLowerInvariant();

        if (ext == ".lnk")
        {
            var target = ResolveShortcut(first);
            if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                FileDropped?.Invoke(target);
                return;
            }
            MessageBox.Show(
                "Shortcuts are not supported. Drag the .exe directly, or use Settings to register an app template.",
                "Shortcut Not Supported", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (ext == ".exe")
        {
            FileDropped?.Invoke(first);
            return;
        }

        MessageBox.Show(
            $"Only .exe files are supported. Got: {ext}",
            "Unsupported File", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            return target;
        }
        catch
        {
            return null;
        }
    }
}
