using System.Drawing.Drawing2D;

namespace ProxySwitch.UI;

/// <summary>
/// Draws the Stitch zone-icons directly with GDI primitives — avoids shipping
/// PNG/SVG resources and the embedded-asset plumbing. Resolution-independent
/// (everything is path-based) and tints with the per-zone accent color.
/// </summary>
public static class IconRenderer
{
    public enum IconKind { Direct, Clash, V2ray, Rocket, Save, Clock, Info }

    /// <summary>Draw <paramref name="kind"/> inside <paramref name="bounds"/>
    /// using <paramref name="color"/> as the stroke/fill tint.</summary>
    public static void Draw(Graphics g, RectangleF bounds, IconKind kind, Color color)
    {
        var prevMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            switch (kind)
            {
                case IconKind.Direct: DrawMonitor(g, bounds, color); break;
                case IconKind.Clash: DrawShield(g, bounds, color); break;
                case IconKind.V2ray: DrawPaperPlane(g, bounds, color); break;
                case IconKind.Rocket: DrawRocket(g, bounds, color); break;
                case IconKind.Save: DrawSave(g, bounds, color); break;
                case IconKind.Clock: DrawClock(g, bounds, color); break;
                case IconKind.Info: DrawInfo(g, bounds, color); break;
            }
        }
        finally
        {
            g.SmoothingMode = prevMode;
        }
    }

    // ---------- Monitor (Direct) ----------
    private static void DrawMonitor(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, Stroke(b));
        // Screen frame
        var screen = Inset(b, 0.10f, 0.05f, 0.10f, 0.25f);
        g.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
        // Stand
        var standY = screen.Bottom + b.Height * 0.04f;
        var standH = b.Height * 0.10f;
        var standX = b.Left + b.Width * 0.32f;
        var standW = b.Width * 0.36f;
        g.DrawLine(pen, b.Left + b.Width * 0.25f, b.Bottom - b.Height * 0.02f, b.Right - b.Width * 0.25f, b.Bottom - b.Height * 0.02f);
        g.DrawLine(pen, b.Left + b.Width * 0.50f, screen.Bottom, b.Left + b.Width * 0.50f, b.Bottom - b.Height * 0.02f);
    }

    // ---------- Shield (Clash) ----------
    private static void DrawShield(Graphics g, RectangleF b, Color color)
    {
        using var path = new GraphicsPath();
        float cx = b.Left + b.Width / 2f;
        float top = b.Top + b.Height * 0.08f;
        float side = b.Width * 0.36f;
        float midY = b.Top + b.Height * 0.52f;
        float bottom = b.Bottom - b.Height * 0.06f;
        path.AddLines(new[]
        {
            new PointF(cx, top),
            new PointF(cx + side, top + b.Height * 0.08f),
            new PointF(cx + side, midY),
        });
        path.AddBezier(
            new PointF(cx + side, midY),
            new PointF(cx + side, bottom - b.Height * 0.12f),
            new PointF(cx + side * 0.4f, bottom),
            new PointF(cx, bottom));
        path.AddBezier(
            new PointF(cx, bottom),
            new PointF(cx - side * 0.4f, bottom),
            new PointF(cx - side, bottom - b.Height * 0.12f),
            new PointF(cx - side, midY));
        path.AddLines(new[]
        {
            new PointF(cx - side, midY),
            new PointF(cx - side, top + b.Height * 0.08f),
            new PointF(cx, top),
        });
        using var pen = new Pen(color, Stroke(b));
        g.DrawPath(pen, path);
        // tick inside
        using var tick = new Pen(color, Stroke(b) * 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(tick, new[]
        {
            new PointF(cx - b.Width * 0.14f, b.Top + b.Height * 0.50f),
            new PointF(cx - b.Width * 0.02f, b.Top + b.Height * 0.62f),
            new PointF(cx + b.Width * 0.18f, b.Top + b.Height * 0.36f),
        });
    }

    // ---------- Paper plane (v2ray) ----------
    private static void DrawPaperPlane(Graphics g, RectangleF b, Color color)
    {
        using var path = new GraphicsPath();
        float x1 = b.Left + b.Width * 0.10f;
        float y1 = b.Top + b.Height * 0.50f;
        float x2 = b.Right - b.Width * 0.05f;
        float y2 = b.Top + b.Height * 0.18f;
        float x3 = b.Right - b.Width * 0.30f;
        float y3 = b.Bottom - b.Height * 0.10f;
        float x4 = b.Left + b.Width * 0.40f;
        float y4 = b.Top + b.Height * 0.60f;
        path.AddPolygon(new[]
        {
            new PointF(x1, y1),
            new PointF(x2, y2),
            new PointF(x3, y3),
            new PointF(x4, y4),
        });
        using var pen = new Pen(color, Stroke(b)) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        // fold crease
        g.DrawLine(pen, x2, y2, x4, y4);
    }

    // ---------- Rocket (LaunchConfirmDialog title) ----------
    private static void DrawRocket(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, Stroke(b));
        float cx = b.Left + b.Width / 2f;
        // Body
        using var body = new GraphicsPath();
        body.AddBezier(
            new PointF(cx, b.Top + b.Height * 0.05f),
            new PointF(cx + b.Width * 0.30f, b.Top + b.Height * 0.30f),
            new PointF(cx + b.Width * 0.30f, b.Top + b.Height * 0.65f),
            new PointF(cx, b.Top + b.Height * 0.78f));
        body.AddBezier(
            new PointF(cx, b.Top + b.Height * 0.78f),
            new PointF(cx - b.Width * 0.30f, b.Top + b.Height * 0.65f),
            new PointF(cx - b.Width * 0.30f, b.Top + b.Height * 0.30f),
            new PointF(cx, b.Top + b.Height * 0.05f));
        g.DrawPath(pen, body);
        // Porthole
        var port = new RectangleF(cx - b.Width * 0.10f, b.Top + b.Height * 0.30f, b.Width * 0.20f, b.Height * 0.20f);
        g.DrawEllipse(pen, port);
        // Fins
        g.DrawLines(pen, new[]
        {
            new PointF(cx - b.Width * 0.28f, b.Top + b.Height * 0.55f),
            new PointF(cx - b.Width * 0.44f, b.Top + b.Height * 0.82f),
            new PointF(cx - b.Width * 0.15f, b.Top + b.Height * 0.75f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(cx + b.Width * 0.28f, b.Top + b.Height * 0.55f),
            new PointF(cx + b.Width * 0.44f, b.Top + b.Height * 0.82f),
            new PointF(cx + b.Width * 0.15f, b.Top + b.Height * 0.75f),
        });
    }

    // ---------- Save / floppy (LaunchConfirmDialog persistent option) ----------
    private static void DrawSave(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, Stroke(b));
        var outer = Inset(b, 0.10f, 0.10f, 0.10f, 0.10f);
        g.DrawRectangle(pen, outer.X, outer.Y, outer.Width, outer.Height);
        // top flap
        var flap = new RectangleF(outer.Left + outer.Width * 0.20f, outer.Top, outer.Width * 0.60f, outer.Height * 0.30f);
        g.DrawRectangle(pen, flap.X, flap.Y, flap.Width, flap.Height);
        // label area
        var label = new RectangleF(outer.Left + outer.Width * 0.15f, outer.Bottom - outer.Height * 0.45f, outer.Width * 0.70f, outer.Height * 0.30f);
        g.DrawRectangle(pen, label.X, label.Y, label.Width, label.Height);
    }

    // ---------- Clock (LaunchConfirmDialog session-only option) ----------
    private static void DrawClock(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, Stroke(b));
        var face = Inset(b, 0.10f, 0.10f, 0.10f, 0.10f);
        g.DrawEllipse(pen, face);
        float cx = face.Left + face.Width / 2f;
        float cy = face.Top + face.Height / 2f;
        g.DrawLine(pen, cx, cy, cx, face.Top + face.Height * 0.22f);
        g.DrawLine(pen, cx, cy, face.Right - face.Width * 0.20f, cy);
    }

    // ---------- Info circle ----------
    private static void DrawInfo(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, Stroke(b));
        var face = Inset(b, 0.08f, 0.08f, 0.08f, 0.08f);
        g.DrawEllipse(pen, face);
        float cx = face.Left + face.Width / 2f;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - face.Width * 0.05f, face.Top + face.Height * 0.18f, face.Width * 0.10f, face.Width * 0.10f);
        var stem = new RectangleF(cx - face.Width * 0.07f, face.Top + face.Height * 0.40f, face.Width * 0.14f, face.Height * 0.40f);
        g.FillRectangle(brush, stem);
    }

    // ---------- helpers ----------
    private static float Stroke(RectangleF b) => Math.Max(1.4f, Math.Min(b.Width, b.Height) / 18f);

    private static RectangleF Inset(RectangleF r, float l, float t, float rr, float bb)
        => new(r.Left + r.Width * l, r.Top + r.Height * t, r.Width * (1 - l - rr), r.Height * (1 - t - bb));
}
