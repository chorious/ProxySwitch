using System.Diagnostics;

namespace ProxySwitch.Controls;

public sealed class LaunchZoneControl : Panel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "Drop app here";
    public string Mode { get; set; } = "direct";

    private Color _accentColor = Color.Gray;
    public Color AccentColor
    {
        get => _accentColor;
        set
        {
            _accentColor = value;
            _normalFill = Color.FromArgb(40, value);
            _hoverFill = Color.FromArgb(90, value);
            Invalidate();
        }
    }

    private Color _normalFill = Color.FromArgb(40, Color.Gray);
    private Color _hoverFill = Color.FromArgb(90, Color.Gray);
    private bool _isHover;
    private readonly Font _titleFont;

    public event Action<string>? FileDropped;

    public LaunchZoneControl()
    {
        AllowDrop = true;
        BorderStyle = BorderStyle.FixedSingle;
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        _titleFont = new Font(Font.FontFamily, 11f, FontStyle.Bold);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var fillColor = _isHover ? _hoverFill : _normalFill;
        using (var brush = new SolidBrush(fillColor))
            e.Graphics.FillRectangle(brush, ClientRectangle);

        TextRenderer.DrawText(
            e.Graphics, $"{Title}\n{Subtitle}", _titleFont, ClientRectangle,
            AccentColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
