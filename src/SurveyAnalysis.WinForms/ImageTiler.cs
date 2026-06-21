using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SurveyAnalysis.WinForms;

// Splits a scanned survey form into overlapping horizontal bands so each section's checkboxes fill more of
// the vision model's resolution budget. A whole A4 page is downsampled by the vision API to ~768px on its
// shortest side, shrinking a pen ✓ to a few pixels the model mis-reads. A short, wide band is upscaled
// instead (its shortest side — the height — is small), so the same checkboxes are seen far larger. Each
// band is greyscaled + contrast-stretched to make light ticks stand out. The bands overlap so a choice
// group that straddles a cut still appears whole in at least one band; the per-band OCR results are merged
// back by OcrExtractor.MergeValues. Best-effort: any failure returns the single whole image so OCR still
// runs.
internal static class ImageTiler
{
    private const int DefaultBands = 4;
    private const double DefaultOverlap = 0.35;   // each band is 35% taller than an equal split → overlap
    private const float Contrast = 1.6f;          // > 1 darkens ticks, lightens paper (stretch around mid-gray)

    public static IReadOnlyList<(byte[] Bytes, string MediaType)> ToBands(byte[] bytes, string mediaType, int bands = DefaultBands, double overlap = DefaultOverlap)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var src = new Bitmap(input);

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
