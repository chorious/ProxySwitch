using System.Diagnostics;

namespace ProxySwitch.Controls;

public sealed class LaunchZoneControl : Panel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "Drop app here";
    public string Mode { get; set; } = "direct";
    public Color AccentColor { get; set; } = Color.Gray;
    private Color _normalBackColor;
    private Color _hoverBackColor;

    public event Action<string>? FileDropped;

    public LaunchZoneControl()
    {
        AllowDrop = true;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        _normalBackColor = Color.FromArgb(30, AccentColor);
        _hoverBackColor = Color.FromArgb(60, AccentColor);

        if (BackColor == Color.Empty)
            BackColor = _normalBackColor;

        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);

        var font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        TextRenderer.DrawText(
            e.Graphics, $"{Title}\n{Subtitle}", font, ClientRectangle,
            AccentColor,
            BackColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            BackColor = _hoverBackColor;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        BackColor = _normalBackColor;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        BackColor = _normalBackColor;
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
