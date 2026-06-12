namespace SurveyAnalysis.Models;

// The data type assigned to a survey field in the project design screen (データ型).
// Five values are personal information (氏名・性別・住所・電話番号・メールアドレス) that the
// spec requires to be encrypted at rest and hidden from the user. (生年月日 was dropped as a
// type and folded into the generic 日付, which is NOT treated as PII.) The partial name /
// address fragments (姓のみ・名のみ・都道府県のみ・市区町村のみ・郵便番号のみ) are also
// treated as NON-PII here: they are deliberately coarse and not individually identifying.
// For this layout prototype no encryption happens; IsPersonalInformation only drives the
// "暗号化" badge so the design intent is visible on screen.
public enum FieldType
{
    Name,           // 氏名 (PII)
    LastNameOnly,   // 姓のみ
    FirstNameOnly,  // 名のみ
    Gender,         // 性別 (PII)
    Address,        // 住所 (PII)
    PrefectureOnly, // 都道府県のみ
    CityOnly,       // 市区町村のみ
    PostalCodeOnly, // 郵便番号のみ
    Phone,          // 電話番号 (PII)
    Email,          // メールアドレス (PII)
    Date,           // 日付
    ChoiceText,     // 選択肢（テキストのまま）
    ChoiceNumber,   // 選択肢（数値に変換）
    Number,         // 数値
    FreeText        // フリーテキスト
}

// Japanese labels and the PII flag for FieldType / AnalysisMethod. Kept next to the enums
// so the UI wording (the words a future maintainer will grep for) stays beside the values.
public static class FieldTypeInfo
{
    // Returns the Japanese label shown in the UI for a field type.
    public static string Label(FieldType type) => type switch
    {
        FieldType.Name => "氏名",
        FieldType.LastNameOnly => "姓のみ",
        FieldType.FirstNameOnly => "名のみ",
        FieldType.Gender => "性別",
        FieldType.Address => "住所",
        FieldType.PrefectureOnly => "都道府県のみ",
        FieldType.CityOnly => "市区町村のみ",
        FieldType.PostalCodeOnly => "郵便番号のみ",
        FieldType.Phone => "電話番号",
        FieldType.Email => "メールアドレス",
        FieldType.Date => "日付",
        FieldType.ChoiceText => "選択肢（テキストのまま）",
        FieldType.ChoiceNumber => "選択肢（数値に変換）",
        FieldType.Number => "数値",
        FieldType.FreeText => "フリーテキスト",
        _ => type.ToString()
    };

    // True for the six personal-information types the spec requires to be encrypted/hidden.
    public static bool IsPersonalInformation(FieldType type) => type
        is FieldType.Name
        or FieldType.Gender
        or FieldType.Address
        or FieldType.Phone
        or FieldType.Email;

    // Returns the Japanese label shown in the UI for an analysis method.
    public static string Label(AnalysisMethod method) => method switch
    {
        AnalysisMethod.None => "なし",
        AnalysisMethod.Topic => "トピック割り当て",
        AnalysisMethod.Sentiment => "感情極性分析",
        _ => method.ToString()
    };
}
