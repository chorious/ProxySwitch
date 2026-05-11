using System.Diagnostics;
using ProxySwitch.Models;

namespace ProxySwitch;

public class CorrelatedHandoffDialog : Form
{
    private readonly List<HandoffCandidate> _candidates;
    private readonly List<RadioButton> _radioButtons = new();
    private Button _trackBtn = null!;
    private Button _openBtn = null!;

    public HandoffCandidate? SelectedCandidate { get; private set; }
    public bool Ignored { get; private set; }

    public CorrelatedHandoffDialog(LaunchSession session, List<HandoffCandidate> candidates)
    {
        _candidates = candidates;
        Text = $"Confirm Handoff: {session.Name}";
        Size = new Size(740, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildUI(session);
    }

    private void BuildUI(LaunchSession session)
    {
        // Use a single TableLayoutPanel as the root — avoids the Dock add-order
        // pitfall where Fill consumes space before Top/Bottom siblings are added.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));    // top label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // candidate list
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));    // button row

        var topLbl = new Label
        {
            Text = $"{Path.GetFileName(session.ExePath)} exited. ProxySwitch detected the following processes that may be the launched app.\n" +
                   "Confidence is heuristic — select one only if you recognize it as the right target.",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 4),
            AutoSize = false
        };
        root.Controls.Add(topLbl, 0, 0);

        var listPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false,
            Padding = new Padding(12, 4, 12, 4)
        };
        foreach (var cand in _candidates.Take(5))
            listPanel.Controls.Add(BuildCandidateRow(cand));
        root.Controls.Add(listPanel, 0, 1);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var ignoreBtn = new Button { Text = "Ignore", AutoSize = true };
        ignoreBtn.Click += (_, _) => { Ignored = true; DialogResult = DialogResult.Cancel; Close(); };

        _trackBtn = new Button { Text = "Track Selected", AutoSize = true, Enabled = false };
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

        _openBtn = new Button { Text = "Open Location", AutoSize = true, Enabled = false };
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

        btnPanel.Controls.Add(ignoreBtn);
        btnPanel.Controls.Add(_trackBtn);
        btnPanel.Controls.Add(_openBtn);
        root.Controls.Add(btnPanel, 0, 2);

        Controls.Add(root);
    }

    private Panel BuildCandidateRow(HandoffCandidate cand)
    {
        var row = new Panel
        {
            Width = 680,
            Height = 84,
            Margin = new Padding(0, 4, 0, 4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = cand.Confidence == "high"
                ? Color.FromArgb(220, 252, 231)
                : Color.FromArgb(254, 249, 195)
        };

        var rb = new RadioButton
        {
            Text = $"{cand.Process.Name}  (PID {cand.Process.ProcessId})",
            Location = new Point(8, 6),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold)
        };
        rb.CheckedChanged += (_, _) =>
        {
            _trackBtn.Enabled = _radioButtons.Any(x => x.Checked);
            _openBtn.Enabled = _radioButtons.Any(x => x.Checked);
        };
        _radioButtons.Add(rb);
        row.Controls.Add(rb);

        var scoreLbl = new Label
        {
            Text = $"Score: {cand.Score}  ({cand.Confidence})",
            Location = new Point(490, 6),
            AutoSize = true,
            ForeColor = cand.Confidence == "high" ? Color.DarkGreen : Color.DarkOrange,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };
        row.Controls.Add(scoreLbl);

        var pathLbl = new Label
        {
            Text = cand.Process.ExecutablePath ?? "(no path)",
            Location = new Point(28, 30),
            Width = 640,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, 8.5f)
        };
        row.Controls.Add(pathLbl);

        var reasonsLbl = new Label
        {
            Text = "Reasons: " + string.Join(", ", cand.Reasons),
            Location = new Point(28, 52),
            Width = 640,
            Height = 28,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, 8f)
        };
        row.Controls.Add(reasonsLbl);

        return row;
    }
}
