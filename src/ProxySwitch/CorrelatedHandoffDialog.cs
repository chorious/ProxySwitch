using System.Diagnostics;
using System.Drawing.Drawing2D;
using ProxySwitch.Models;
using ProxySwitch.UI;

namespace ProxySwitch;

/// <summary>
/// Confirm-handoff dialog for the correlated-process detection path. The
/// detector ranks candidate processes by heuristic score; the user picks one
/// to track, or Ignore. PR5c: Stitch refresh — Theme tokens for row tints,
/// rounded score badge, PillButton CTAs. We keep the panel-row layout (not a
/// DataGridView) because each candidate has a multi-line "Reasons" footnote
/// that doesn't fit a single-line cell.
/// </summary>
public class CorrelatedHandoffDialog : Form
{
    private readonly List<HandoffCandidate> _candidates;
    private readonly List<RadioButton> _radioButtons = new();
    private PillButton _trackBtn = null!;
    private PillButton _openBtn = null!;

    public HandoffCandidate? SelectedCandidate { get; private set; }
    public bool Ignored { get; private set; }

    public CorrelatedHandoffDialog(LaunchSession session, List<HandoffCandidate> candidates)
    {
        _candidates = candidates;
        Text = $"Confirm Handoff: {session.Name}";
        Size = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
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
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // candidate list
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
        foreach (var cand in _candidates.Take(5))
            listPanel.Controls.Add(BuildCandidateRow(cand));
        root.Controls.Add(listPanel, 0, 1);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.WindowBg,
            Padding = new Padding(0, 8, 0, 0),
        };
        var ignoreBtn = new PillButton
        {
            Text = "Ignore",
            Variant = PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
        };
        ignoreBtn.Click += (_, _) => { Ignored = true; DialogResult = DialogResult.Cancel; Close(); };

        _trackBtn = new PillButton
        {
            Text = "Track Selected",
            Variant = PillButton.PillVariant.Primary,
            Padding = new Padding(18, 6, 18, 6),
            Enabled = false,
        };
        _trackBtn.Click += (_, _) =>
        {
            var selectedIdx = _radioButtons.FindIndex(rb => rb.Checked);
            if (selectedIdx >= 0)
            {
                SelectedCandidate = _candidates[selectedIdx];
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        _openBtn = new PillButton
        {
            Text = "Open Location",
            Variant = PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
            Enabled = false,
        };
        _openBtn.Click += (_, _) =>
        {
            var selectedIdx = _radioButtons.FindIndex(rb => rb.Checked);
            if (selectedIdx >= 0)
            {
                var path = _candidates[selectedIdx].Process.ExecutablePath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\""
                    });
                }
            }
        };

        // Stitch order: Ignore / Open Location / Track Selected — flow is RightToLeft
        // so adding Track Selected first puts it on the right.
        btnPanel.Controls.Add(_trackBtn);
        btnPanel.Controls.Add(_openBtn);
        btnPanel.Controls.Add(ignoreBtn);
        root.Controls.Add(btnPanel, 0, 2);

        Controls.Add(root);
        AcceptButton = _trackBtn;
        CancelButton = ignoreBtn;
    }

    /// <summary>
    /// Info banner explaining the heuristic. Same shape as RouteChildDialog's
    /// banner (BgChecking light blue + Info icon).
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
            Text = $"{Path.GetFileName(session.ExePath)} exited and ProxySwitch detected processes that may be the launched app.\n" +
                   $"Confidence is heuristic — pick a candidate only if you recognize it as the right target.",
            Location = new Point(44, 8),
            Size = new Size(660, 56),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.BgChecking,
            Font = Theme.BodyFont,
        };
        panel.Controls.Add(text);
        return panel;
    }

    private Panel BuildCandidateRow(HandoffCandidate cand)
    {
        var bg = cand.Confidence == "high" ? Theme.BgRunning : Theme.BgViaChild;
        var row = new Panel
        {
            Width = 680,
            Height = 84,
            Margin = new Padding(0, 1, 0, 0),
            BackColor = bg,
        };

        var rb = new RadioButton
        {
            Text = $"{cand.Process.Name}  (PID {cand.Process.ProcessId})",
            Location = new Point(12, 8),
            AutoSize = true,
            Font = Theme.BodySemibold,
            ForeColor = Theme.TextPrimary,
            BackColor = bg,
        };
        rb.CheckedChanged += (_, _) =>
        {
            _trackBtn.Enabled = _radioButtons.Any(x => x.Checked);
            _openBtn.Enabled = _radioButtons.Any(x => x.Checked);
        };
        _radioButtons.Add(rb);
        row.Controls.Add(rb);

        // Score badge — rounded rect painted on a hosting panel.
        var badge = new ScoreBadge(cand.Score, cand.Confidence)
        {
            Location = new Point(500, 8),
            Size = new Size(160, 22),
            BackColor = bg,
        };
        row.Controls.Add(badge);

        var pathLbl = new Label
        {
            Text = cand.Process.ExecutablePath ?? "(no path)",
            Location = new Point(32, 30),
            Width = 620,
            AutoEllipsis = true,
            ForeColor = Theme.TextSecondary,
            BackColor = bg,
            Font = Theme.BodyFont,
        };
        row.Controls.Add(pathLbl);

        var reasonsLbl = new Label
        {
            Text = "Reasons: " + string.Join(", ", cand.Reasons),
            Location = new Point(32, 52),
            Width = 620,
            Height = 28,
            ForeColor = Theme.TextSecondary,
            BackColor = bg,
            Font = Theme.BodyFont,
        };
        row.Controls.Add(reasonsLbl);

        return row;
    }

    /// <summary>
    /// Small rounded-rect badge: filled in the confidence color, white text.
    /// Stitch's progress-bar style isn't practical in pure WinForms; this is
    /// the readable substitute.
    /// </summary>
    private sealed class ScoreBadge : Control
    {
        private readonly int _score;
        private readonly string _confidence;

        public ScoreBadge(int score, string confidence)
        {
            _score = score;
            _confidence = confidence;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            ForeColor = Theme.OnPrimary;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var fill = _confidence == "high"
                ? Theme.StatusActiveGreen
                : Theme.StatusPendingAmber;

            // Background must match parent (we cleared it via the parent's bg in
            // construction); fill our pill area only.
            using var bgBrush = new SolidBrush(BackColor);
            g.FillRectangle(bgBrush, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using var path = RoundedRect(r, Height / 2f - 0.5f);
            using var fillBrush = new SolidBrush(fill);
            g.FillPath(fillBrush, path);

            var text = $"Score: {_score} · {_confidence}";
            TextRenderer.DrawText(g, text, Theme.BodySemibold, ClientRectangle, Theme.OnPrimary,
                TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
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
}
