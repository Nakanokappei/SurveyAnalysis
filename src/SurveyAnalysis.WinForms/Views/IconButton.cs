using System;
using System.Drawing;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// A flat button that draws its icon glyph in Segoe UI Emoji and its caption in the UI font. WinForms
// can't mix two fonts within one control's Text, so the button owner-draws the two runs side by side:
// the glyph in the OS-standard emoji font (no missing-glyph worry) and the caption in the UI font. GDI
// renders the glyph monochrome, tinted by ForeColor, so it sits cleanly next to the text. The flat look
// (solid background, hover colour, optional border) mirrors a FlatStyle.Flat button — callers set
// BackColor / ForeColor / Font / Padding / TextAlign / FlatAppearance exactly as for a normal Button,
// and pass the icon separately via Glyph (so it never lands in the UI-font Text).
internal sealed class IconButton : Button
{
    private bool _hover;

    public IconButton()
    {
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    // The icon glyph, drawn in IconFontName. Empty = caption only.
    public string Glyph { get; init; } = "";

    // The font the glyph is drawn in (Segoe UI Emoji by default; Webdings/Wingdings for box-free icons).
    public string IconFontName { get; init; } = Theme.IconFontName;

    // Glyph point size; 0 = the caption font's size.
    public float IconSize { get; init; }

    // Draw the glyph after the caption (e.g. 次へ ▶) rather than before it.
    public bool GlyphTrailing { get; init; }

    // Space between glyph and caption (smaller for compact pagers).
    public int IconGap { get; init; } = 6;

    private float GlyphSizePt => IconSize > 0 ? IconSize : Font.SizeInPoints;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    // Size to the glyph + caption content plus Padding — computed directly rather than via the base,
    // which pads short captions out to the standard ~75px Button minimum (that inflated the compact
    // pager buttons). Same NoPadding measurement the paint uses, so the content always fits.
    public override Size GetPreferredSize(Size proposedSize)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var caption = TextRenderer.MeasureText(Text, Font, Size.Empty, flags);
        var width = caption.Width;
        var height = caption.Height;
        if (!string.IsNullOrEmpty(Glyph))
        {
            using var iconFont = Theme.IconFont(IconFontName, GlyphSizePt);
            var glyph = TextRenderer.MeasureText(Glyph, iconFont, Size.Empty, flags);
            width += glyph.Width + IconGap;
            height = Math.Max(height, glyph.Height);
        }
        return new Size(width + Padding.Horizontal + 2, height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // Flat background, hover-aware.
        var back = _hover && FlatAppearance.MouseOverBackColor != Color.Empty
            ? FlatAppearance.MouseOverBackColor
            : BackColor;
        g.Clear(back);

        // Optional flat border (matches FlatAppearance.BorderSize / BorderColor).
        if (FlatAppearance.BorderSize > 0)
        {
            using var pen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize);
            var r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            g.DrawRectangle(pen, r);
        }

        var color = Enabled ? ForeColor : Color.FromArgb(120, ForeColor);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        var content = Rectangle.FromLTRB(
            ClientRectangle.Left + Padding.Left, ClientRectangle.Top + Padding.Top,
            ClientRectangle.Right - Padding.Right, ClientRectangle.Bottom - Padding.Bottom);

        var captionSize = TextRenderer.MeasureText(g, Text, Font, content.Size, flags);

        // No glyph: just place the caption per TextAlign.
        if (string.IsNullOrEmpty(Glyph))
        {
            DrawRun(g, Text, Font, content, AlignLeft(content, captionSize.Width), captionSize, color, flags);
            return;
        }

        // Lay out [glyph + gap + caption] (or caption + gap + glyph when trailing) as one group, aligned
        // per TextAlign and vertically centred run by run.
        using var iconFont = Theme.IconFont(IconFontName, GlyphSizePt);
        var glyphSize = TextRenderer.MeasureText(g, Glyph, iconFont, content.Size, flags);
        var groupLeft = AlignLeft(content, captionSize.Width + IconGap + glyphSize.Width);

        if (GlyphTrailing)
        {
            DrawRun(g, Text, Font, content, groupLeft, captionSize, color, flags);
            DrawRun(g, Glyph, iconFont, content, groupLeft + captionSize.Width + IconGap, glyphSize, color, flags);
        }
        else
        {
            DrawRun(g, Glyph, iconFont, content, groupLeft, glyphSize, color, flags);
            DrawRun(g, Text, Font, content, groupLeft + glyphSize.Width + IconGap, captionSize, color, flags);
        }
    }

    // Left edge of a run/group of the given width, honouring TextAlign's horizontal part.
    private int AlignLeft(Rectangle content, int width) => TextAlign switch
    {
        ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft => content.Left,
        ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => content.Right - width,
        _ => content.Left + Math.Max(0, (content.Width - width) / 2),
    };

    // Draws one run vertically centred in the content box at the given left edge.
    private static void DrawRun(Graphics g, string text, Font font, Rectangle content, int x, Size size, Color color, TextFormatFlags flags)
    {
        var y = content.Top + (content.Height - size.Height) / 2;
        TextRenderer.DrawText(g, text, font, new Rectangle(x, y, size.Width, size.Height), color, flags);
    }
}
