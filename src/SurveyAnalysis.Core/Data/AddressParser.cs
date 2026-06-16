using System.Linq;

namespace SurveyAnalysis.Data;

// Splits a Japanese address string into a 都道府県 (prefecture) and 市区町村 (municipality) so the
// region dimension can be queried hierarchically. The split is deliberately simple — it matches the
// 47 prefecture names at the start, then takes the municipality as everything up to and including the
// first 市 / 区 / 町 / 村 in the remainder (so 郡部 like "○○郡△△町" keep the 郡 prefix, and 政令市
// collapse to the 市 level). Anything that does not start with a known prefecture is （不明）.
public static class AddressParser
{
    // The 47 prefectures, full names including the 都/道/府/県 suffix, so StartsWith is unambiguous.
    private static readonly string[] Prefectures =
    {
        "北海道",
        "青森県", "岩手県", "宮城県", "秋田県", "山形県", "福島県",
        "茨城県", "栃木県", "群馬県", "埼玉県", "千葉県", "東京都", "神奈川県",
        "新潟県", "富山県", "石川県", "福井県", "山梨県", "長野県", "岐阜県", "静岡県", "愛知県",
        "三重県", "滋賀県", "京都府", "大阪府", "兵庫県", "奈良県", "和歌山県",
        "鳥取県", "島根県", "岡山県", "広島県", "山口県",
        "徳島県", "香川県", "愛媛県", "高知県",
        "福岡県", "佐賀県", "長崎県", "熊本県", "大分県", "宮崎県", "鹿児島県", "沖縄県",
    };

    // Parses an address into (prefecture, city). A blank or unrecognised address yields ("（不明）", "");
    // a recognised prefecture with no municipality marker yields (prefecture, "").
    public static (string Prefecture, string City) Parse(string? address)
    {
        var text = address?.Trim() ?? "";
        var prefecture = Prefectures.FirstOrDefault(text.StartsWith);
        if (prefecture is null)
            return ("（不明）", "");
        return (prefecture, ExtractCity(text[prefecture.Length..]));
    }

    // The municipality: the remainder up to and including the first 市 / 区 / 町 / 村, or "" if none.
    private static string ExtractCity(string remainder)
    {
        for (var i = 0; i < remainder.Length; i++)
            if (remainder[i] is '市' or '区' or '町' or '村')
                return remainder[..(i + 1)];
        return "";
    }
}
