using PDFtoImage;
using SkiaSharp;

namespace SurveyAnalysis.WinForms;

// Rasterizes a PDF's pages to PNG images so a scanned-form PDF flows through the same vision OCR path as an
// image file. The OpenAI-compatible chat API can't take a PDF directly, so each page is rendered locally
// (via PDFium, through PDFtoImage) and handed to ImageTiler like any other image. One page becomes one
// staged record = one response. PDFtoImage's PDFium + SkiaSharp natives cover win-x64/x86/arm64, so this
// works on both the production x64 build and the arm64 development VM.
internal static class PdfRasterizer
{
    // Render DPI: ~200 keeps small print and checkbox ticks legible; ImageTiler then enlarges bands further
    // for the vision model.
    private const int Dpi = 200;

    // The page count — each page becomes one staged record (one response).
    public static int GetPageCount(byte[] pdfBytes) => Conversion.GetPageCount(pdfBytes);

    // Renders one page (0-based) to PNG bytes on a white background.
    public static byte[] RenderPageToPng(byte[] pdfBytes, int pageIndex)
    {
        var options = new RenderOptions(Dpi: Dpi, BackgroundColor: SKColors.White);
        using var bitmap = Conversion.ToImage(pdfBytes, page: pageIndex, options: options);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
