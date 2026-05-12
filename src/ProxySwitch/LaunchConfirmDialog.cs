using ProxySwitch.UI;

namespace ProxySwitch;

public enum LaunchChoice { Cancel, SessionOnly, Persistent }

/// <summary>
/// Stitch-style three-way launch confirmation. Replaces the legacy
/// MessageBox.YesNoCancel flow in DashboardForm.HandleDrop with a typed enum
/// + a more readable visual layout (rocket title + two option rows + bottom
/// button bar). The dialog returns the picked option via <see cref="Choice"/>.
/// </summary>
public class LaunchConfirmDialog : Form
{
    public LaunchChoice Choice { get; private set; } = LaunchChoice.Cancel;

    public LaunchConfirmDialog(string proxyLabel)
    {
        Text = $"Launch via {proxyLabel}?";
        Size = new Size(560, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.PanelBg;
        Font = Theme.BodyFont;
        ForeColor = Theme.TextPrimary;

        BuildUI(proxyLabel);
    }

    private void BuildUI(string proxyLabel)
    {
        // --- Title row (rocket icon + title text) ---
        var titleHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Theme.PanelBg,
            Padding = new Padding(20, 12, 20, 8),
        };
        var rocket = MakeIcon(IconRenderer.IconKind.Rocket, Theme.TextPrimary, 24, Theme.PanelBg);
        rocket.Location = new Point(20, 14);
        var titleLbl = new Label
        {
            Text = $"Launch via {proxyLabel}?",
            Font = new Font(Theme.BodySemibold.FontFamily, 12f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.PanelBg,
            AutoSize = true,
            Location = new Point(52, 16),
        };
        titleHost.Controls.Add(rocket);
        titleHost.Controls.Add(titleLbl);

        // --- Divider ---
        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.BorderSubtle,
        };

        // --- Description ---
        var descHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 50,
            BackColor = Theme.PanelBg,
            Padding = new Padding(20, 12, 20, 8),
        };
        var descLbl = new Label
        {
            Text = "Route this application through the configured proxy. " +
                   "Choose whether the route is saved or only valid for this session.",
            Dock = DockStyle.Fill,
            Font = Theme.BodyFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Theme.PanelBg,
        };
        descHost.Controls.Add(descLbl);

        // --- Bottom button bar ---
        var btnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.SurfaceContainerLow,
            Padding = new Padding(20, 12, 20, 16),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var saveBtn = new PillButton
        {
            Text = "Save Route",
            Variant = PillButton.PillVariant.Primary,
            Padding = new Padding(18, 6, 18, 6),
        };
        saveBtn.Click += (_, _) => { Choice = LaunchChoice.Persistent; DialogResult = DialogResult.OK; Close(); };

        var sessionBtn = new PillButton
        {
            Text = "Just This Session",
            Variant = PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
        };
        sessionBtn.Click += (_, _) => { Choice = LaunchChoice.SessionOnly; DialogResult = DialogResult.OK; Close(); };

        var cancelBtn = new PillButton
        {
            Text = "Cancel",
            Variant = PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
        };
        cancelBtn.Click += (_, _) => { Choice = LaunchChoice.Cancel; DialogResult = DialogResult.Cancel; Close(); };

        btnBar.Controls.Add(saveBtn);
        btnBar.Controls.Add(sessionBtn);
        btnBar.Controls.Add(cancelBtn);

        // --- Options host (Fill — last to add so it shrinks correctly) ---
        var optionsHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelBg,
            Padding = new Padding(20, 4, 20, 12),
        };
        var optStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.PanelBg,
        };
        optStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        optStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        optStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        optStack.Controls.Add(BuildOption(
            IconRenderer.IconKind.Save,
            "Save route permanently",
            "Applies to all future sessions. Written to proxyswitch.json and may need UAC to restart ProxiFyre."), 0, 0);
        optStack.Controls.Add(BuildOption(
            IconRenderer.IconKind.Clock,
            "Just this session",
            "Applies until the launched process exits. Removed automatically afterwards."), 0, 1);
        optionsHost.Controls.Add(optStack);

        // Dock add order: Fill last (lowest priority), Bottom + Top siblings claim
        // their slices first.
        Controls.Add(optionsHost);
        Controls.Add(descHost);
        Controls.Add(divider);
        Controls.Add(titleHost);
        Controls.Add(btnBar);

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    private static Panel BuildOption(IconRenderer.IconKind icon, string title, string desc)
    {
        const int padX = 16;
        const int padY = 12;
        var p = new Panel
        {
            Margin = new Padding(0, 4, 0, 4),
            Dock = DockStyle.Fill,
            BackColor = Theme.SurfaceContainerLow,
            Padding = new Padding(padX, padY, padX, padY),
        };
        var iconBox = MakeIcon(icon, Theme.TextSecondary, 28, Theme.SurfaceContainerLow);
        iconBox.Location = new Point(padX, padY + 4);
        p.Controls.Add(iconBox);

        var titleLbl = new Label
        {
            Text = title,
            Font = Theme.BodySemibold,
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.SurfaceContainerLow,
            AutoSize = true,
            Location = new Point(padX + 40, padY),
        };
        p.Controls.Add(titleLbl);

        var descLbl = new Label
        {
            Text = desc,
            Font = Theme.BodyFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Theme.SurfaceContainerLow,
            Location = new Point(padX + 40, padY + 22),
            Width = 440,
            AutoEllipsis = false,
        };
        p.Controls.Add(descLbl);
        return p;
    }

    /// <summary>
    /// Tiny IconRenderer host panel with a fixed background color. We avoid
    /// BackColor=Transparent because WinForms transparency requires
    /// SupportsTransparentBackColor on each parent — caller passes the parent's
    /// background explicitly so the icon sits flush.
    /// </summary>
    private static Panel MakeIcon(IconRenderer.IconKind kind, Color color, int size, Color bg)
    {
        var p = new Panel { Size = new Size(size, size), BackColor = bg };
        p.Paint += (_, e) => IconRenderer.Draw(e.Graphics, new RectangleF(0, 0, size, size), kind, color);
        return p;
    }
}
