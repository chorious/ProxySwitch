using ProxySwitch.Models;
using ProxySwitch.UI;

namespace ProxySwitch;

/// <summary>
/// Multi-select dialog for "Route Child" when a session has 2+ live non-root
/// processes with distinct executable paths. ProxiFyre matches by executable
/// path, so the row key is ExePath; PIDs that share the same path are merged.
/// PR5b: Stitch refresh — info banner on top, alternating row tint, PillButton
/// CTAs.
/// </summary>
public class RouteChildDialog : Form
{
    private readonly List<ChildRow> _rows;
    private readonly List<CheckBox> _checkBoxes = new();
    private PillButton _routeBtn = null!;

    public IReadOnlyList<string> SelectedExePaths { get; private set; } = Array.Empty<string>();

    public RouteChildDialog(LaunchSession session, IReadOnlyList<TrackedProcess> liveChildren)
    {
        _rows = liveChildren
            .Where(c => !string.IsNullOrEmpty(c.ExecutablePath))
            .GroupBy(c => c.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ChildRow
            {
                ExePath = g.Key,
                Name = g.First().Name,
                Pids = g.Select(c => c.ProcessId).ToList()
            })
            .ToList();

        Text = $"Route Children: {session.Name}";
        Size = new Size(740, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        Font = Theme.BodyFont;
        ForeColor = Theme.TextPrimary;

        BuildUI(session);
    }

    private void BuildUI(LaunchSession session)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.WindowBg,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76f));   // info banner
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // list
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));   // button row

        root.Controls.Add(BuildInfoBanner(session), 0, 0);

        var listPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false,
            BackColor = Theme.PanelBg,
            Margin = new Padding(0, 8, 0, 8),
        };
        int idx = 0;
        foreach (var row in _rows)
            listPanel.Controls.Add(BuildRow(row, idx++));
        root.Controls.Add(listPanel, 0, 1);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.WindowBg,
            Padding = new Padding(0, 8, 0, 0),
        };
        var cancelBtn = new PillButton
        {
            Text = "Cancel",
            Variant = PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
        };
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _routeBtn = new PillButton
        {
            Text = "Route Selected",
            Variant = PillButton.PillVariant.Primary,
            Padding = new Padding(18, 6, 18, 6),
            Enabled = false,
        };
        _routeBtn.Click += (_, _) =>
        {
            SelectedExePaths = _checkBoxes
                .Select((cb, i) => (cb, i))
                .Where(t => t.cb.Checked)
                .Select(t => _rows[t.i].ExePath)
                .ToList();
            DialogResult = DialogResult.OK;
            Close();
        };

        btnPanel.Controls.Add(_routeBtn);
        btnPanel.Controls.Add(cancelBtn);
        root.Controls.Add(btnPanel, 0, 2);

        Controls.Add(root);
        AcceptButton = _routeBtn;
        CancelButton = cancelBtn;
    }

    /// <summary>
    /// Top info banner — light-blue background (BgChecking) + Info icon +
    /// explanation. Matches Stitch's "info card" pattern at the top of dialogs.
    /// </summary>
    private static Panel BuildInfoBanner(LaunchSession session)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgChecking,
            Padding = new Padding(12),
        };

        var iconBox = new Panel
        {
            Size = new Size(24, 24),
            BackColor = Theme.BgChecking,
            Location = new Point(12, 12),
        };
        iconBox.Paint += (_, e) =>
            IconRenderer.Draw(e.Graphics, new RectangleF(0, 0, 24, 24), IconRenderer.IconKind.Info, Theme.StatusInfoBlue);
        panel.Controls.Add(iconBox);

        var text = new Label
        {
            Text = $"ProxiFyre matches by executable path. Pick the child processes to add to the routing rules for {session.ProxyId}.\n" +
                   $"Inheriting parent scope: {(session.IsPersistentLaunch ? "persistent (saved)" : "session-only")}.",
            Location = new Point(44, 8),
            Size = new Size(640, 56),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.BgChecking,
            Font = Theme.BodyFont,
        };
        panel.Controls.Add(text);

        return panel;
    }

    private Panel BuildRow(ChildRow row, int idx)
    {
        var bg = (idx % 2 == 0) ? Theme.PanelBg : Theme.SurfaceContainerLow;
        var panel = new Panel
        {
            Width = 660,
            Height = 60,
            Margin = new Padding(0, 1, 0, 0),
            BackColor = bg,
        };

        var cb = new CheckBox
        {
            Text = $"{row.Name}  (PID{(row.Pids.Count > 1 ? "s" : "")} {string.Join(", ", row.Pids)})",
            Location = new Point(12, 8),
            AutoSize = true,
            Font = Theme.BodySemibold,
            ForeColor = Theme.TextPrimary,
            BackColor = bg,
        };
        cb.CheckedChanged += (_, _) =>
        {
            _routeBtn.Enabled = _checkBoxes.Any(x => x.Checked);
        };
        _checkBoxes.Add(cb);
        panel.Controls.Add(cb);

        var pathLbl = new Label
        {
            Text = row.ExePath,
            Location = new Point(32, 32),
            Width = 620,
            AutoEllipsis = true,
            ForeColor = Theme.TextSecondary,
            BackColor = bg,
            Font = Theme.BodyFont,
        };
        panel.Controls.Add(pathLbl);

        return panel;
    }

    private class ChildRow
    {
        public string ExePath { get; set; } = "";
        public string Name { get; set; } = "";
        public List<int> Pids { get; set; } = new();
    }
}
