namespace SurveyAnalysis.Models;

// The data type assigned to a survey field in the project design screen (データ型).
// Five values are personal information (氏名・性別・住所・電話番号・メールアドレス) that the spec
// requires to be encrypted at rest and hidden from the user. (生年月日 was dropped and folded into
// the generic 日付, which is NOT treated as PII.) The earlier partial-fragment types (姓のみ・名のみ・
// 都道府県のみ・市区町村のみ・郵便番号のみ) and the split 選択肢（テキスト/数値）were consolidated into
// 氏名・住所・選択肢; ParseStored migrates projects saved before that. テキスト (short text) and 文章
// (long free text) are the two free-form types. For this prototype no encryption happens;
// IsPersonalInformation only drives the "暗号化" badge so the design intent is visible on screen.
public enum FieldType
{
    Name,       // 氏名 (PII)
    Gender,     // 性別 (PII)
    Address,    // 住所 (PII)
    Phone,      // 電話番号 (PII)
    Email,      // メールアドレス (PII)
    Date,       // 日付
    Choice,     // 選択肢
    Number,     // 数値
    Text,       // テキスト（短文）
    FreeText    // 文章（自由記述）
}

// Japanese labels and the PII flag for FieldType / AnalysisMethod. Kept next to the enums
// so the UI wording (the words a future maintainer will grep for) stays beside the values.
public static class FieldTypeInfo
{
    // Returns the Japanese label shown in the UI for a field type.
    public static string Label(FieldType type) => type switch
    {
        FieldType.Name => "氏名",
        FieldType.Gender => "性別",
        FieldType.Address => "住所",
        FieldType.Phone => "電話番号",
        FieldType.Email => "メールアドレス",
        FieldType.Date => "日付",
        FieldType.Choice => "選択肢",
        FieldType.Number => "数値",
        FieldType.Text => "テキスト",
        FieldType.FreeText => "文章",
        _ => type.ToString()
    };

    // True for the five personal-information types the spec requires to be encrypted/hidden.
    public static bool IsPersonalInformation(FieldType type) => type
        is FieldType.Name
        or FieldType.Gender
        or FieldType.Address
        or FieldType.Phone
        or FieldType.Email;

    // Parses a stored field_type, migrating values saved before the type list was consolidated:
    // 姓のみ/名のみ → 氏名, 都道府県/市区町村/郵便番号のみ → 住所, 選択肢(テキスト/数値) → 選択肢.
    // Anything unrecognised falls back to 文章 (free text).
    public static FieldType ParseStored(string stored) => stored switch
    {
        "LastNameOnly" or "FirstNameOnly" => FieldType.Name,
        "PrefectureOnly" or "CityOnly" or "PostalCodeOnly" => FieldType.Address,
        "ChoiceText" or "ChoiceNumber" => FieldType.Choice,
        _ => System.Enum.TryParse<FieldType>(stored, out var type) ? type : FieldType.FreeText,
    };

    // Returns the Japanese label shown in the UI for an analysis method.
    public static string Label(AnalysisMethod method) => method switch
    {
        AnalysisMethod.None => "なし",
        AnalysisMethod.Topic => "トピック割り当て",
        AnalysisMethod.Sentiment => "感情極性分析",
        _ => method.ToString()
    };
}
