using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SurveyAnalysis.Data;

namespace SurveyAnalysis.WinForms;

// A small line chart of 感情極性の推移 — the average row sentiment over the selected 集計期間, bucketed by
// day or week (the repository chooses by span). Y is fixed to [-1, +1] with a zero baseline; X is the
// buckets in order, each carrying its own short axis label. Markers are tinted by sign (green ≥0 / red <0)
// and a tooltip shows the bucket's average and 件数 on hover. Clicking a marker raises PointClicked so the
// host can narrow the 集計期間 to that day / week. Pure GDI+; the host feeds points via SetData.
internal sealed class SentimentTrendChart : Control
{
    private IReadOnlyList<SentimentTrendPoint> _points = Array.Empty<SentimentTrendPoint>();
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private int _hoverIndex = -1;
    private readonly List<PointF> _markers = new();   // device positions of each point, for hit-testing

    // Raised when a marker is clicked — the host narrows the 集計期間 to that point's [From, To].
    public event EventHandler<SentimentTrendPoint>? PointClicked;

    public SentimentTrendChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    public void SetData(IReadOnlyList<SentimentTrendPoint> points)
    {
        _points = points ?? Array.Empty<SentimentTrendPoint>();
        _hoverIndex = -1;
        Invalidate();
    }

    // Logical→device scale for this control's DPI (markers, pens, insets stay crisp at 200%).
    private float Dp(float logical) => logical * DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _markers.Clear();

        // Plot area: leave room for the y labels (left) and the month labels (bottom).
        var padL = Dp(40);
        var padR = Dp(14);
        var padT = Dp(8);
        var padB = Dp(22);
        var plot = RectangleF.FromLTRB(padL, padT, Width - padR, Height - padB);
        if (plot.Width <= 1 || plot.Height <= 1)
            return;

        float Y(double v) => plot.Bottom - (float)((v + 1.0) / 2.0) * plot.Height;

        using var gridPen = new Pen(Theme.CardBorder);
        using var zeroPen = new Pen(Color.FromArgb(0xC2, 0xCC, 0xD8)) { DashStyle = DashStyle.Dash };
        using var labelBrush = new SolidBrush(Theme.Muted);
        using var labelFont = Theme.Font(8f);

        // Y gridlines + labels at +1.0 / 0 / -1.0.
        foreach (var (v, text) in new[] { (1.0, "+1.0"), (0.0, "0"), (-1.0, "-1.0") })
        {
            var y = Y(v);
            g.DrawLine(v == 0 ? zeroPen : gridPen, plot.Left, y, plot.Right, y);
            var size = g.MeasureString(text, labelFont);
            g.DrawString(text, labelFont, labelBrush, plot.Left - size.Width - Dp(4), y - size.Height / 2);
        }

        if (_points.Count == 0)
        {
            using var emptyFont = Theme.Font(9f);
            using var emptyBrush = new SolidBrush(Theme.Faint);
            const string message = "この期間に感情極性のデータがありません。";
            var size = g.MeasureString(message, emptyFont);
            g.DrawString(message, emptyFont, emptyBrush, plot.Left + (plot.Width - size.Width) / 2, plot.Top + (plot.Height - size.Height) / 2);
            return;
        }

        // X positions: evenly spaced; a single point sits in the middle.
        float X(int i) => _points.Count == 1 ? plot.Left + plot.Width / 2f : plot.Left + (float)i / (_points.Count - 1) * plot.Width;
        for (var i = 0; i < _points.Count; i++)
            _markers.Add(new PointF(X(i), Y(_points[i].Average)));

        // The connecting line (accent), then markers on top.
        if (_markers.Count >= 2)
        {
            using var linePen = new Pen(Theme.Accent, Dp(2));
            g.DrawLines(linePen, _markers.ToArray());
        }

        using var positive = new SolidBrush(Theme.Success);
        using var negative = new SolidBrush(Theme.Danger);
        using var ring = new Pen(Color.White, Dp(1.5f));
        using var monthFont = Theme.Font(7.5f);

        // Thin out month labels so they never overlap at narrow widths.
        var labelStep = Math.Max(1, (int)Math.Ceiling(_points.Count / Math.Max(1f, plot.Width / Dp(44))));
        for (var i = 0; i < _points.Count; i++)
        {
            var point = _points[i];
            var center = _markers[i];
            var radius = Dp(i == _hoverIndex ? 5f : 3.5f);
            g.FillEllipse(point.Average < 0 ? negative : positive, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(ring, center.X - radius, center.Y - radius, radius * 2, radius * 2);

            if (i % labelStep == 0 || i == _points.Count - 1)
            {
                var text = point.AxisLabel;
                var size = g.MeasureString(text, monthFont);
                g.DrawString(text, monthFont, labelBrush, center.X - size.Width / 2, plot.Bottom + Dp(3));
            }
        }
    }

    // The marker within the hit radius of the location, or -1.
    private int NearestMarker(Point location)
    {
        var nearest = -1;
        var best = Dp(14);
        for (var i = 0; i < _markers.Count; i++)
        {
            var d = (float)Math.Sqrt(Math.Pow(_markers[i].X - location.X, 2) + Math.Pow(_markers[i].Y - location.Y, 2));
            if (d < best) { best = d; nearest = i; }
        }
        return nearest;
    }

    // Hover: highlight the nearest marker (within a small radius), show its value / 件数, and hint that it
    // is clickable with a hand cursor.
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var nearest = NearestMarker(e.Location);
        Cursor = nearest >= 0 ? Cursors.Hand : Cursors.Default;
        if (nearest == _hoverIndex)
            return;
        _hoverIndex = nearest;
        _tip.SetToolTip(this, nearest >= 0
            ? $"{_points[nearest].Label}：平均 {_points[nearest].Average:+0.00;-0.00;0.00}（{_points[nearest].Count}件）"
            : "");
        Invalidate();
    }

    // Clicking a marker narrows the 集計期間 to that point's day / week (the host re-aggregates the report).
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
            return;
        var index = NearestMarker(e.Location);
        if (index >= 0)
            PointClicked?.Invoke(this, _points[index]);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}
