using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SurveyAnalysis.WinForms;

// Prepares a scanned survey form for OCR by spending the vision model's fixed resolution budget on marks
// instead of blank paper, in four steps. (1) CropToContent trims the blank outer margin. (2) CompressVertically
// removes the blank/rule bands *between* rows, packing the content so a whole-page read sees each row larger
// (forms are read top-to-bottom, so whole rows are the safe unit — a checkbox and its label stay together).
// (3) CompressHorizontally does the same across columns (usually a smaller win, since full-width headers/rules
// keep most interior gutters non-blank, but a genuinely empty side band still packs in). (4) SplitIntoBands cuts
// the compacted page into overlapping horizontal bands only as far as needed: a band taller than TargetBandHeightPx
// reads at too low a DPI, so the page is divided into just enough bands to bring each under that height (capped at
// MaxBands for cost). After compression most forms need far fewer bands — often a single whole-page read. Each
// band is greyscaled + contrast-stretched to make light ticks stand out, and the bands overlap so a choice group
// straddling a cut still appears whole in at least one; the per-band OCR results are merged back by
// OcrExtractor.MergeValues. Best-effort: any failure returns the single whole image so OCR still runs.
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
            // Trim the blank outer margin, then collapse the blank/rule bands between rows and between columns. All
            // three free resolution for the actual marks; the compacted page is then cut into OCR bands.
            using var cropped = CropToContent(decoded);
            using var compacted = CompressVertically(cropped);
            using var src = CompressHorizontally(compacted);
            return SplitIntoBands(src, overlap);
        }
        catch
        {
            return new[] { (bytes, mediaType) };
        }
    }

    // Cuts the compacted page into just enough overlapping, enhanced horizontal bands: as few as bring every band
    // under TargetBandHeightPx (so its content reads at adequate DPI), capped at MaxBands for cost. After
    // compression a normal form is often short enough to read whole — a single band. The bands overlap so a choice
    // group straddling a cut still appears whole in at least one.
    private static IReadOnlyList<(byte[] Bytes, string MediaType)> SplitIntoBands(Bitmap src, double overlap)
    {
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

    // Removes the blank/rule bands *between* rows so the content packs together top-to-bottom and a whole-page read
    // sees each row larger. Forms are read row by row (横書き), so whole rows are the unit; KeptSegments decides which
    // rows survive (content rows, plus a thin gap of empty rows, with blank interiors and rule lines dropped) and
    // Rebuild stacks them. Returns a full-frame copy when nothing is removable.
    private static Bitmap CompressVertically(Bitmap src)
    {
        ScanInk(src, out var rowInk, out _);
        var segments = KeptSegments(rowInk, src.Height, src.Width, UnitLine(src.Width));
        return Rebuild(src, segments, vertical: true);
    }

    // The same compaction applied across columns: removes blank vertical bands and full-height vertical rules so
    // content packs left-to-right. Its effect is usually small — an interior gutter is only removed when it is blank
    // over the *entire* page height, which full-width titles/headers/rules normally prevent (so two-column option
    // groups are not merged) — but a form with a genuinely empty side band still benefits. Returns a full-frame copy
    // when nothing is removable.
    private static Bitmap CompressHorizontally(Bitmap src)
    {
        ScanInk(src, out _, out var colInk);
        var segments = KeptSegments(colInk, src.Width, src.Height, UnitLine(src.Width));
        return Rebuild(src, segments, vertical: false);
    }

    // n: how many source lines collapse into one model pixel — the unit-line. Deciding keep/drop finer than this
    // is pointless (the model averages that many lines into one pixel), so it is the granularity for compaction.
    private static int UnitLine(int width) => Math.Max(1, width / TargetResolution);

    // Axis-agnostic core of the two compaction passes. lineInk is the ink count per line along the compaction axis
    // (rows for vertical, columns for horizontal); lineCount is the axis length; perpDim is each line's full length
    // (so SignalPercent/SolidPercent read as fractions of it); n is how many source lines collapse into one model
    // pixel. Classifies each line as blank (< SignalPercent), rule (≥ SolidPercent in a thin run — a separator a
    // human reads but OCR does not; a *thick* inked run is a filled band such as a shaded header and stays content),
    // or content. Returns the source-line segments worth keeping: every content block, plus one empty block of gap +
    // quiet zone on each side of a content region; rule blocks and blank interiors are dropped.
    private static List<(int Start, int Length)> KeptSegments(int[] lineInk, int lineCount, int perpDim, int n)
    {
        // Rule lines: fully-inked lines (≥ SolidPercent) belonging to a thin run; a thick run is a filled band.
        var solidMin = SolidPercent * perpDim;
        var maxRuleThickness = Math.Max(2, lineCount / RuleThicknessDivisor);
        var isRule = new bool[lineCount];
        for (var i = 0; i < lineCount;)
        {
            if (lineInk[i] < solidMin) { i++; continue; }
            var start = i;
            while (i < lineCount && lineInk[i] >= solidMin) i++;
            if (i - start <= maxRuleThickness)
                for (var r = start; r < i; r++)
                    isRule[r] = true;
        }

        // Classify each n-line block: content (any inked, non-rule line), empty (only blank), or rule (rule lines +
        // blank, no content). Only empty blocks may be kept as a gap; rule blocks are always dropped.
        var blankMax = SignalPercent * perpDim;
        var blockCount = (lineCount + n - 1) / n;
        var content = new bool[blockCount];
        var empty = new bool[blockCount];
        for (var b = 0; b < blockCount; b++)
        {
            var first = b * n;
            var lines = Math.Min(n, lineCount - first);
            bool hasContent = false, hasRule = false;
            for (var i = first; i < first + lines; i++)
            {
                if (isRule[i]) hasRule = true;
                else if (lineInk[i] >= blankMax) hasContent = true;
            }
            content[b] = hasContent;
            empty[b] = !hasContent && !hasRule;
        }

        // Keep content blocks, plus an empty block wherever it touches content (one gap + quiet zone each side).
        var keep = new bool[blockCount];
        for (var b = 0; b < blockCount; b++)
            keep[b] = content[b]
                   || (empty[b] && ((b > 0 && content[b - 1]) || (b < blockCount - 1 && content[b + 1])));

        // Gather kept blocks into contiguous source-line segments.
        var segments = new List<(int Start, int Length)>();
        for (var b = 0; b < blockCount;)
        {
            if (!keep[b]) { b++; continue; }
            var start = b;
            while (b < blockCount && keep[b]) b++;
            var first = start * n;
            segments.Add((first, Math.Min(lineCount, b * n) - first));
        }
        return segments;
    }

    // Stacks the kept segments back into a bitmap — vertically (row segments) or horizontally (column segments).
    // Nearest-neighbour + half-pixel offset keeps the 1:1 copy crisp (no blur on thin marks). Returns a full-frame
    // copy when the segments already span the whole axis (nothing removed), so the caller can dispose uniformly.
    private static Bitmap Rebuild(Bitmap src, List<(int Start, int Length)> segments, bool vertical)
    {
        var kept = 0;
        foreach (var s in segments)
            kept += s.Length;
        var axisLength = vertical ? src.Height : src.Width;
        if (segments.Count == 0 || kept >= axisLength)
            return (Bitmap)src.Clone();

        var outBmp = vertical
            ? new Bitmap(src.Width, kept, PixelFormat.Format32bppArgb)
            : new Bitmap(kept, src.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(outBmp))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var dest = 0;
            foreach (var (start, length) in segments)
            {
                var srcRect = vertical
                    ? new Rectangle(0, start, src.Width, length)
                    : new Rectangle(start, 0, length, src.Height);
                var destRect = vertical
                    ? new Rectangle(0, dest, src.Width, length)
                    : new Rectangle(dest, 0, length, src.Height);
                graphics.DrawImage(src, destRect, srcRect, GraphicsUnit.Pixel);
                dest += length;
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
