using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SurveyAnalysis.WinForms;

// Prepares a scanned survey form for OCR by spending the vision model's fixed resolution budget on marks
// instead of blank paper, in three steps. (1) CropToContent trims the blank outer margin. (2) CompressVertically
// removes the blank bands *between* filled rows, packing the content so a whole-page read sees each row larger
// (forms are read top-to-bottom, so whole rows are the safe unit — a checkbox and its label stay together).
// (3) The compacted page is split into overlapping horizontal bands only as far as needed: a band taller than
// TargetBandHeightPx reads at too low a DPI, so the page is divided into just enough bands to bring each under
// that height (capped at MaxBands for cost). After compression most forms need far fewer bands — often a single
// whole-page read. Each band is greyscaled + contrast-stretched to make light ticks stand out, and the bands
// overlap so a choice group straddling a cut still appears whole in at least one; the per-band OCR results are
// merged back by OcrExtractor.MergeValues. Best-effort: any failure returns the single whole image so OCR runs.
internal static class ImageTiler
{
    private const double DefaultOverlap = 0.35;   // each band is 35% taller than an equal split → overlap
    private const float Contrast = 1.6f;          // > 1 darkens ticks, lightens paper (stretch around mid-gray)
    private const int InkLuma = 220;              // a pixel counts as content (ink) when its luma is below this; paper is brighter
    private const int TargetResolution = 512;     // the cheap vision model's working square; sets the n-row decision granularity
    private const int TargetBandHeightPx = 600;   // a band taller than this reads at too low a DPI → split the page further
    private const int MaxBands = 4;               // cap on per-image OCR calls (cost ceiling)
    private const float SignalPercent = 0.01f;    // a row with < 1% ink is blank paper
    private const float SolidPercent = 0.90f;     // a row with ≥ this much ink is "filled" right across the width
    private const int RuleThicknessDivisor = 200; // a filled run thinner than height/this is a rule line; thicker is a filled band (kept)

    public static IReadOnlyList<(byte[] Bytes, string MediaType)> ToBands(byte[] bytes, string mediaType, double overlap = DefaultOverlap)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var decoded = new Bitmap(input);
            // Trim the blank outer margin, then collapse the blank bands between filled rows. Both free vertical
            // resolution for the actual marks; the band sizing below then works on the compacted page.
            using var cropped = CropToContent(decoded);
            using var src = CompressVertically(cropped);

            // Split only as far as needed: just enough overlapping bands to bring each under TargetBandHeightPx,
            // capped for cost. After compression a normal form is often short enough to read whole (one band).
            var bands = Math.Clamp((src.Height + TargetBandHeightPx - 1) / TargetBandHeightPx, 1, MaxBands);
            if (bands < 2)
                return new[] { (Enhance(src, 0, src.Height), "image/png") };

            var bandHeight = (int)Math.Ceiling(src.Height * (1.0 / bands) * (1.0 + overlap));
            bandHeight = Math.Min(bandHeight, src.Height);
            var step = (src.Height - bandHeight) / (double)(bands - 1);

            var result = new List<(byte[], string)>(bands);
            for (var i = 0; i < bands; i++)
            {
                var top = (int)Math.Round(i * step);
                top = Math.Min(top, src.Height - bandHeight);
                result.Add((Enhance(src, top, bandHeight), "image/png"));
            }
            return result;
        }
        catch
        {
            return new[] { (bytes, mediaType) };
        }
    }

    // Returns the smallest region that still contains all of the form's content, dropping the blank scan
    // margin around it. Ink pixels (darker than the paper) are tallied per row and per column in one pass;
    // a row/column is treated as content only when enough of it is ink (≥1% of its length), which ignores
    // dust and JPEG speckle in the margin. The crop is the span between the first and last content lines on
    // each axis, grown by a thin quiet zone so a glyph or tick at the very edge is never shaved. Trims
    // inward only — interior whitespace is left alone. Returns a full-frame copy when there is nothing to
    // trim or detection is inconclusive, so the caller always gets a fresh Bitmap it can dispose uniformly.
    private static Bitmap CropToContent(Bitmap src)
    {
        var width = src.Width;
        var height = src.Height;
        ScanInk(src, out var rowInk, out var colInk);

        // A content line needs a sustained run of ink, not a few stray dark pixels (≥1% of its length, min 3).
        var minRow = Math.Max(3, width / 100);
        var minCol = Math.Max(3, height / 100);
        var top = FirstContent(rowInk, minRow);
        var bottom = LastContent(rowInk, minRow);
        var left = FirstContent(colInk, minCol);
        var right = LastContent(colInk, minCol);

        // Nothing crossed the threshold (blank or unusual image) → keep the whole frame.
        if (top < 0 || bottom < top || left < 0 || right < left)
            return (Bitmap)src.Clone();

        // Grow by a thin quiet zone (1% of the content extent, min 2px) so edge marks survive the crop.
        var padX = Math.Max(2, (right - left) / 100);
        var padY = Math.Max(2, (bottom - top) / 100);
        left = Math.Max(0, left - padX);
        top = Math.Max(0, top - padY);
        right = Math.Min(width - 1, right + padX);
        bottom = Math.Min(height - 1, bottom + padY);

        var crop = new Rectangle(left, top, right - left + 1, bottom - top + 1);
        // No meaningful margin to remove → full-frame copy (uniform disposal for the caller).
        if (crop.Width >= width && crop.Height >= height)
            return (Bitmap)src.Clone();
        return src.Clone(crop, src.PixelFormat);
    }

    // First / last index whose tally reaches the content threshold, or -1 when none do.
    private static int FirstContent(int[] counts, int min)
    {
        for (var i = 0; i < counts.Length; i++)
            if (counts[i] >= min)
                return i;
        return -1;
    }

    private static int LastContent(int[] counts, int min)
    {
        for (var i = counts.Length - 1; i >= 0; i--)
            if (counts[i] >= min)
                return i;
        return -1;
    }

    // Counts ink pixels (luma below InkLuma) per row and per column in one read-only pass. LockBits converts to
    // a known BGRA layout regardless of the source format, so GetPixel's per-call cost is avoided on multi-
    // megapixel scans.
    private static void ScanInk(Bitmap src, out int[] rowInk, out int[] colInk)
    {
        var width = src.Width;
        var height = src.Height;
        rowInk = new int[height];
        colInk = new int[width];

        var data = src.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (var y = 0; y < height; y++)
            {
                var rowBase = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var i = rowBase + x * 4;   // BGRA
                    // Integer luma (0.114B + 0.587G + 0.299R), ×1000 to keep it in ints.
                    var luma = (114 * buffer[i] + 587 * buffer[i + 1] + 299 * buffer[i + 2]) / 1000;
                    if (luma < InkLuma)
                    {
                        rowInk[y]++;
                        colInk[x]++;
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(data);
        }
    }

    // Removes the horizontal bands that carry no OCR value so the content packs together and a whole-page read sees
    // each row larger. Forms are read top-to-bottom (横書き), so whole rows are the unit. Three kinds of row: a
    // *blank* row (< SignalPercent ink) is empty paper; a *rule* row is ink right across the width (≥ SolidPercent)
    // AND part of a thin run — a separator line or box border a human reads but OCR does not; a *content* row is
    // anything else. The thinness test is what protects shaded / reverse-video section headers: a header is also
    // inked across the width, but its run is thick (a band, not a line), so it stays content and is kept — only a
    // hairline rule is dropped. Rows are grouped into n-row blocks (n = source rows per model pixel,
    // width / TargetResolution — finer than that the model cannot resolve). Rule/blank-only blocks are dropped,
    // except one blank block is kept on each side of every content region as a small gap + quiet zone, so packed
    // rows do not merge and an edge mark is never sliced. Returns a full-frame copy when nothing is removable.
    private static Bitmap CompressVertically(Bitmap src)
    {
        var width = src.Width;
        var height = src.Height;
        ScanInk(src, out var rowInk, out _);

        // Find rule rows: full-width-inked rows (≥ SolidPercent) that belong to a thin run. A thick run of inked
        // rows is a filled band (a shaded/reverse-video header), not a line, so it is left as content.
        var solidRowMin = SolidPercent * width;
        var maxRuleThickness = Math.Max(2, height / RuleThicknessDivisor);
        var isRule = new bool[height];
        for (var y = 0; y < height;)
        {
            if (rowInk[y] < solidRowMin) { y++; continue; }
            var start = y;
            while (y < height && rowInk[y] >= solidRowMin) y++;
            if (y - start <= maxRuleThickness)
                for (var r = start; r < y; r++)
                    isRule[r] = true;
        }

        // n: source rows per model pixel — the unit-line. Below this granularity the model averages rows together,
        // so there is no point deciding keep/drop more finely.
        var n = Math.Max(1, width / TargetResolution);
        var blockCount = (height + n - 1) / n;

        // Classify each n-row block: content (any inked, non-rule row), empty (only blank paper), or rule (rule
        // lines + blank, no content). Only empty blocks may be kept as a gap; rule blocks are always dropped.
        var blankRowMax = SignalPercent * width;
        var content = new bool[blockCount];
        var empty = new bool[blockCount];
        for (var b = 0; b < blockCount; b++)
        {
            var topRow = b * n;
            var rows = Math.Min(n, height - topRow);
            bool hasContent = false, hasRule = false;
            for (var y = topRow; y < topRow + rows; y++)
            {
                if (isRule[y]) hasRule = true;
                else if (rowInk[y] >= blankRowMax) hasContent = true;
            }
            content[b] = hasContent;
            empty[b] = !hasContent && !hasRule;   // pure blank paper (a rule-bearing block is neither → dropped)
        }

        // Keep every content block. Keep an empty block only where it touches content — one gap + quiet zone on
        // each side of a content region — and drop the interior of blank runs and every rule/border block.
        var keep = new bool[blockCount];
        for (var b = 0; b < blockCount; b++)
            keep[b] = content[b]
                   || (empty[b] && ((b > 0 && content[b - 1]) || (b < blockCount - 1 && content[b + 1])));

        // Gather the kept blocks into contiguous source-row segments.
        var segments = new List<(int Top, int Height)>();
        for (var b = 0; b < blockCount;)
        {
            if (!keep[b]) { b++; continue; }
            var start = b;
            while (b < blockCount && keep[b]) b++;
            var top = start * n;
            segments.Add((top, Math.Min(height, b * n) - top));
        }

        var outHeight = 0;
        foreach (var s in segments)
            outHeight += s.Height;

        // Nothing to gain (no blank removed, or the whole page is blank) → full-frame copy.
        if (segments.Count == 0 || outHeight >= height)
            return (Bitmap)src.Clone();

        // Stack the kept segments. Nearest-neighbour + half-pixel offset keeps the 1:1 copy crisp (no blur on
        // thin marks).
        var outBmp = new Bitmap(width, outHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(outBmp))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var destY = 0;
            foreach (var (top, segHeight) in segments)
            {
                graphics.DrawImage(src, new Rectangle(0, destY, width, segHeight), new Rectangle(0, top, width, segHeight), GraphicsUnit.Pixel);
                destY += segHeight;
            }
        }
        return outBmp;
    }

    // Crops [top, top+height) of the source and returns it greyscaled + contrast-stretched as PNG (lossless;
    // JPEG artifacts blur thin marks). No upscaling — the band is left small so the vision API enlarges it.
    private static byte[] Enhance(Bitmap src, int top, int height)
    {
        using var band = new Bitmap(src.Width, height);
        using (var graphics = Graphics.FromImage(band))
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(GrayscaleContrast(Contrast));
            graphics.DrawImage(src, new Rectangle(0, 0, src.Width, height), 0, top, src.Width, height, GraphicsUnit.Pixel, attributes);
        }
        using var encoded = new MemoryStream();
        band.Save(encoded, ImageFormat.Png);
        return encoded.ToArray();
    }

    // One ColorMatrix that greyscales (luma weights) then stretches contrast by c around mid-gray:
    // out = luma(in) * c + (0.5 − 0.5c). c > 1 makes light pen ticks darker and the paper lighter.
    private static ColorMatrix GrayscaleContrast(float c)
    {
        var t = 0.5f - 0.5f * c;
        return new ColorMatrix(new[]
        {
            new[] { 0.299f * c, 0.299f * c, 0.299f * c, 0f, 0f },
            new[] { 0.587f * c, 0.587f * c, 0.587f * c, 0f, 0f },
            new[] { 0.114f * c, 0.114f * c, 0.114f * c, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { t, t, t, 0f, 1f },
        });
    }
}
