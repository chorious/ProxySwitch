namespace ProxySwitch.UI;

/// <summary>
/// Stitch-style top tab strip: brand label + 4 nav tabs (Launch / Sessions /
/// Settings / Logs). Active tab is rendered in PrimaryBlue with a 2px underline.
/// This is a *visual* nav — the strip raises TabClicked but does not switch
/// content views; the host wires each tab to focus a control or open a dialog.
/// </summary>
internal sealed class TopTabStrip : Panel
{
    public event Action<string>? TabClicked;

    public static readonly string[] TabNames = { "Launch", "Sessions", "Settings", "Logs" };

    private readonly List<TabLabel> _tabs = new();

    public TopTabStrip()
    {
        Dock = DockStyle.Top;
        Height = 44;
        BackColor = Theme.PanelBg;
        Padding = new Padding(Theme.ContainerPadding, 0, Theme.ContainerPadding, 0);
        DoubleBuffered = true;

        var inner = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            BackColor = Theme.PanelBg,
            Margin = Padding.Empty
        };

        var brand = new Label
        {
            Text = "ProxySwitch",
            AutoSize = true,
            Font = new Font(Theme.BodySemibold.FontFamily, 10f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.PanelBg,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 13, 16, 0)
        };
        inner.Controls.Add(brand);

        foreach (var name in TabNames)
        {
            var tab = new TabLabel(name);
            tab.Click += OnTabClicked;
            _tabs.Add(tab);
            inner.Controls.Add(tab);
        }

        Controls.Add(inner);
        SetActiveTab("Launch");
    }

    public void SetActiveTab(string name)
    {
        foreach (var t in _tabs)
            t.IsActive = string.Equals(t.TabName, name, StringComparison.Ordinal);
    }

    private void OnTabClicked(object? sender, EventArgs e)
    {
        if (sender is not TabLabel tab) return;
        // Settings opens a modal dialog — don't latch it active. Launch is the
        // resting state; Sessions/Logs are passive focus affordances and don't
        // need a sticky underline either.
        if (tab.TabName != "Settings")
            SetActiveTab(tab.TabName);
        TabClicked?.Invoke(tab.TabName);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.OutlineVariant, 1f);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    private sealed class TabLabel : Label
    {
        public string TabName { get; }
        private bool _active;
        public bool IsActive
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                ForeColor = _active ? Theme.PrimaryBlue : Theme.TextSecondary;
                Invalidate();
            }
        }

        public TabLabel(string name)
        {
            TabName = name;
            Text = name;
            AutoSize = true;
            BackColor = Theme.PanelBg;
            ForeColor = Theme.TextSecondary;
            Font = new Font(Theme.BodySemibold.FontFamily, 10f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            // Reserve 6px of body padding so the 2px underline doesn't kiss the
            // glyph baseline. Top padding pushes the text down so the strip's
            // 44px height stays vertically centered around it.
            Padding = new Padding(8, 13, 8, 6);
            Margin = new Padding(8, 0, 8, 0);
            TextAlign = ContentAlignment.TopLeft;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_active) return;
            using var pen = new Pen(Theme.PrimaryBlue, 2f);
            int y = Height - 2;
            e.Graphics.DrawLine(pen, Padding.Left, y, Width - Padding.Right, y);
        }
    }
}
