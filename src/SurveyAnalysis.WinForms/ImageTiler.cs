using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SurveyAnalysis.WinForms;

// Splits a scanned survey form into overlapping horizontal bands so each section's checkboxes fill more of
// the vision model's resolution budget. A whole A4 page is downsampled by the vision API to ~768px on its
// shortest side, shrinking a pen ✓ to a few pixels the model mis-reads. A short, wide band is upscaled
// instead (its shortest side — the height — is small), so the same checkboxes are seen far larger. The
// blank scan margin is trimmed off first (CropToContent), since every pixel of white border is resolution
// the API spends on nothing. Each band is greyscaled + contrast-stretched to make light ticks stand out.
// The bands overlap so a choice group that straddles a cut still appears whole in at least one band; the
// per-band OCR results are merged back by OcrExtractor.MergeValues. Best-effort: any failure returns the
// single whole image so OCR still runs.
internal static class ImageTiler
{
    private const int DefaultBands = 4;
    private const double DefaultOverlap = 0.35;   // each band is 35% taller than an equal split → overlap
    private const float Contrast = 1.6f;          // > 1 darkens ticks, lightens paper (stretch around mid-gray)
    private const int InkLuma = 220;              // a pixel counts as content (ink) when its luma is below this; paper is brighter

    public static IReadOnlyList<(byte[] Bytes, string MediaType)> ToBands(byte[] bytes, string mediaType, int bands = DefaultBands, double overlap = DefaultOverlap)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var decoded = new Bitmap(input);
            // Trim the blank outer margin first so the form's content fills the frame the vision API will
            // downsample. Everything below (band sizing, the short-page test) then works on the cropped page.
            using var src = CropToContent(decoded);

            // A short page does not benefit from splitting (the bands would be tiny); OCR it whole.
            if (bands < 2 || src.Height < 600)
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
        var rowInk = new int[height];
        var colInk = new int[width];

        // One read-only pass over the pixels (LockBits converts to a known BGRA layout regardless of the
        // source format, so GetPixel's per-call cost is avoided on multi-megapixel scans).
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
