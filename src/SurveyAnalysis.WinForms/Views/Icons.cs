namespace SurveyAnalysis.WinForms;

// The app's button icons as (font, glyph) pairs, in one place. Directional icons come from the Webdings
// family so they render as clean, box-free monochrome symbols (Segoe UI Emoji encloses arrows/triangles
// in a rounded box). The Wingdings/Webdings glyph is the ASCII character that maps to the symbol in that
// font (e.g. "u" in Wingdings 3 is ▶). Pictographs with no good Webdings equivalent stay on Segoe UI
// Emoji, which draws them box-free anyway. IconButton renders Glyph in Font; an empty Glyph = no icon.
internal static class Icons
{
    // No icon (caption only) — e.g. the sub-nav items.
    public static readonly (string Font, string Glyph) None = (Theme.IconFontName, "");

    // Directional — Wingdings 3 (clean triangles/arrows, no box).
    public static readonly (string Font, string Glyph) Collapse = (Theme.Wingdings3, "u");  // ▶
    public static readonly (string Font, string Glyph) Expand   = (Theme.Wingdings3, "q");  // ▼
    public static readonly (string Font, string Glyph) Bullet   = (Theme.Wingdings3, "u");  // ▶
    public static readonly (string Font, string Glyph) Export   = (Theme.Wingdings3, "#");  // ↑
    public static readonly (string Font, string Glyph) Back     = (Theme.Wingdings3, "!");  // ←
    public static readonly (string Font, string Glyph) Reset    = (Theme.Wingdings3, "Q");  // ↺

    // Record-navigation pager — Webdings media controls (consistent family).
    public static readonly (string Font, string Glyph) First = (Theme.Webdings, "9");  // ⏮
    public static readonly (string Font, string Glyph) Prev  = (Theme.Webdings, "3");  // ◀
    public static readonly (string Font, string Glyph) Next  = (Theme.Webdings, "4");  // ▶
    public static readonly (string Font, string Glyph) Last  = (Theme.Webdings, ":");  // ⏭

    // Pictographs from the Webdings family where a clean glyph exists.
    public static readonly (string Font, string Glyph) Edit  = (Theme.Wingdings, "!");  // ✏ pencil
    public static readonly (string Font, string Glyph) Close = (Theme.Webdings,  "r");  // ✖

    // Pictographs with no good Webdings equivalent — box-free Segoe UI Emoji.
    public static readonly (string Font, string Glyph) Dashboard = (Theme.IconFontName, "📊");
    public static readonly (string Font, string Glyph) Add       = (Theme.IconFontName, "➕");
    public static readonly (string Font, string Glyph) Settings  = (Theme.IconFontName, "⚙");
    public static readonly (string Font, string Glyph) Calendar  = (Theme.IconFontName, "📅");
}
