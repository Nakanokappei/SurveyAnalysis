using System.Drawing;

namespace SurveyAnalysis.WinForms;

// Central palette and font choices for the WinForms UI. Mirrors the accent and neutral colours the
// Avalonia preview used — Windows-standard blue (#005FB8) and a slate sidebar — so the app keeps its
// identity, while otherwise relying on stock WinForms controls (標準ネイティブ基調). One place to grep
// when a colour or the base font needs to change.
internal static class Theme
{
    // Sidebar (dark slate) and its nav text / hover states.
    public static readonly Color SidebarBack = ColorTranslator.FromHtml("#1E293B");
    public static readonly Color SidebarHover = ColorTranslator.FromHtml("#334155");
    public static readonly Color NavText = ColorTranslator.FromHtml("#E5E7EB");
    public static readonly Color SubNavText = ColorTranslator.FromHtml("#CBD5E1");
    public static readonly Color SectionHeader = ColorTranslator.FromHtml("#64748B");
    public static readonly Color ProjectName = ColorTranslator.FromHtml("#93C5FD");

    // Content pane and typography.
    public static readonly Color ContentBack = ColorTranslator.FromHtml("#F1F5F9");
    public static readonly Color TitleText = ColorTranslator.FromHtml("#0F172A");
    public static readonly Color BodyText = ColorTranslator.FromHtml("#475569");
    public static readonly Color CardBorder = ColorTranslator.FromHtml("#E2E8F0");

    // Secondary text and KPI value colours used on the dashboard.
    public static readonly Color Muted = ColorTranslator.FromHtml("#64748B");
    public static readonly Color Faint = ColorTranslator.FromHtml("#94A3B8");
    public static readonly Color Danger = ColorTranslator.FromHtml("#DC2626");
    public static readonly Color Success = ColorTranslator.FromHtml("#16A34A");
    public static readonly Color BarTrackText = ColorTranslator.FromHtml("#334155");

    // Accent (primary action) — Windows-standard blue.
    public static readonly Color Accent = ColorTranslator.FromHtml("#005FB8");
    public static readonly Color AccentText = Color.White;

    // Yu Gothic UI is the native Japanese UI font on Windows 10/11; no bundling is needed since this
    // app only runs on Windows. (BIZ UDPGothic is also present on Win10+ if a swap is ever wanted.)
    public const string FontName = "Yu Gothic UI";

    public static Font Font(float size = 9f, FontStyle style = FontStyle.Regular) => new(FontName, size, style);

    // Icon fonts. Directional glyphs (arrows / triangles) use the Webdings family — Wingdings 3 has clean
    // box-free triangles and arrows, Webdings has the media controls — because Segoe UI Emoji draws those
    // with an enclosing rounded box. Pictographs with no Webdings equivalent (gear, calendar, lock, bar
    // chart, plus) stay on Segoe UI Emoji, which is the OS-standard emoji font (no missing-glyph tofu) and
    // draws them box-free. All render monochrome under GDI, tinted by the control's ForeColor. See Icons.
    public const string IconFontName = "Segoe UI Emoji";
    public const string Webdings = "Webdings";
    public const string Wingdings = "Wingdings";
    public const string Wingdings3 = "Wingdings 3";
    // Segoe MDL2 Assets ships with Windows 10/11; its thin ChevronRight/Down are the native, refined
    // disclosure arrows — lighter and smaller-feeling than the Wingdings 3 filled triangle.
    public const string SegoeIcon = "Segoe MDL2 Assets";

    public static Font IconFont(float size = 9f, FontStyle style = FontStyle.Regular) => new(IconFontName, size, style);

    public static Font IconFont(string fontName, float size, FontStyle style = FontStyle.Regular) => new(fontName, size, style);
}
