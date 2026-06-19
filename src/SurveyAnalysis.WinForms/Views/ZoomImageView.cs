using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// An image viewer for proofreading scans: it fits the image to the control, lets the overall zoom be set
// with +/- (buttons, the +/- keys when focused, or the mouse wheel) and panned by dragging once zoomed,
// and shows a magnifier loupe that follows the cursor on hover so small handwriting can be read without
// changing the overall zoom. Pure GDI+; the image is owned by the caller (not disposed here).
internal sealed class ZoomImageView : Control
{
    private Image? _image;
    private float _zoom = 1f;          // overall zoom on top of the fit scale (1 = fit-to-control)
    private PointF _offset;            // pan offset of the display rectangle, used when zoomed past fit
    private bool _hovering;
    private Point _cursor;
    private bool _panning;
    private Point _panStart;
    private PointF _panOrigin;

    private const float MinZoom = 1f;
    private const float MaxZoom = 8f;
    private const float ZoomStep = 1.25f;
    private const int LoupeSize = 168;            // px of the square loupe
    private const float LoupeMagnification = 2.5f; // loupe scale relative to the current display scale

    public ZoomImageView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false;
        BackColor = Color.FromArgb(0xF1, 0xF5, 0xF9);
    }

    // Raised whenever the overall zoom changes (so a host can show the percentage).
    public event EventHandler? ZoomChanged;

    public Image? Image
    {
        get => _image;
        set { _image = value; _zoom = 1f; _offset = PointF.Empty; Invalidate(); ZoomChanged?.Invoke(this, EventArgs.Empty); }
    }

    // The overall zoom as a percentage of fit (100% = fit-to-control).
    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public void ZoomIn() => SetZoom(_zoom * ZoomStep);
    public void ZoomOut() => SetZoom(_zoom / ZoomStep);
    public void ResetZoom() => SetZoom(1f);

    private void SetZoom(float value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.0001f)
            return;
        _zoom = clamped;
        if (_zoom <= 1f)
            _offset = PointF.Empty;   // back to fit → recentre
        ClampOffset();
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    // The scale that fits the whole image inside the control (preserving aspect ratio).
    private float FitScale()
    {
        if (_image is null || _image.Width == 0 || _image.Height == 0)
            return 1f;
        return Math.Min((float)Width / _image.Width, (float)Height / _image.Height);
    }

    // The on-screen rectangle the image is drawn into (fit × zoom, centred, plus the pan offset).
    private RectangleF DisplayRect()
    {
        if (_image is null)
            return RectangleF.Empty;
        var scale = FitScale() * _zoom;
        var w = _image.Width * scale;
        var h = _image.Height * scale;
        return new RectangleF((Width - w) / 2f + _offset.X, (Height - h) / 2f + _offset.Y, w, h);
    }

    // Keeps the pan within bounds so a zoomed image cannot be dragged off into empty space.
    private void ClampOffset()
    {
        if (_image is null) { _offset = PointF.Empty; return; }
        var scale = FitScale() * _zoom;
        var maxX = Math.Max(0, (_image.Width * scale - Width) / 2f);
        var maxY = Math.Max(0, (_image.Height * scale - Height) / 2f);
        _offset = new PointF(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_image is null)
            return;
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var dest = DisplayRect();
        g.DrawImage(_image, dest);

        // The loupe only shows on hover (not while dragging to pan) and only over the image.
        if (_hovering && !_panning && dest.Contains(_cursor))
            DrawLoupe(g, dest);
    }

    // Draws the magnifier: a square near the cursor showing the source region under the cursor at
    // LoupeMagnification × the current display scale.
    private void DrawLoupe(Graphics g, RectangleF dest)
    {
        var displayScale = FitScale() * _zoom;
        var loupeScale = displayScale * LoupeMagnification;

        // Source pixel under the cursor, and the source crop that fills the loupe box at loupeScale.
        var srcX = (_cursor.X - dest.X) / displayScale;
        var srcY = (_cursor.Y - dest.Y) / displayScale;
        var cropW = LoupeSize / loupeScale;
        var cropH = LoupeSize / loupeScale;
        var sx = Math.Clamp(srcX - cropW / 2f, 0, Math.Max(0, _image!.Width - cropW));
        var sy = Math.Clamp(srcY - cropH / 2f, 0, Math.Max(0, _image.Height - cropH));

        // Place the box near the cursor, flipping to the other side near an edge.
        var lx = _cursor.X + 18;
        var ly = _cursor.Y + 18;
        if (lx + LoupeSize > Width) lx = _cursor.X - 18 - LoupeSize;
        if (ly + LoupeSize > Height) ly = _cursor.Y - 18 - LoupeSize;
        var loupe = new Rectangle(lx, ly, LoupeSize, LoupeSize);

        var clip = g.Clip;
        g.SetClip(loupe);
        using (var bg = new SolidBrush(Color.White))
            g.FillRectangle(bg, loupe);
        g.DrawImage(_image, loupe, sx, sy, cropW, cropH, GraphicsUnit.Pixel);
        g.Clip = clip;
        using var pen = new Pen(Color.FromArgb(0x33, 0x41, 0x55), 1.5f);
        g.DrawRectangle(pen, loupe);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovering = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovering = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();   // so the +/- keys reach this control
        if (e.Button == MouseButtons.Left && _zoom > 1f)
        {
            _panning = true;
            _panStart = e.Location;
            _panOrigin = _offset;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _cursor = e.Location;
        if (_panning)
        {
            _offset = new PointF(_panOrigin.X + (e.X - _panStart.X), _panOrigin.Y + (e.Y - _panStart.Y));
            ClampOffset();
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning) { _panning = false; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta > 0) ZoomIn(); else if (e.Delta < 0) ZoomOut();
    }

    // +/- (and =) adjust the zoom when the view has keyboard focus; 0 resets to fit.
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Oemplus or Keys.Add or Keys.OemMinus or Keys.Subtract or Keys.D0 or Keys.NumPad0
            || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Oemplus or Keys.Add: ZoomIn(); e.Handled = true; break;
            case Keys.OemMinus or Keys.Subtract: ZoomOut(); e.Handled = true; break;
            case Keys.D0 or Keys.NumPad0: ResetZoom(); e.Handled = true; break;
        }
    }
}
