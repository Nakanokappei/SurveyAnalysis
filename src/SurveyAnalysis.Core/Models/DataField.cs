using CommunityToolkit.Mvvm.ComponentModel;

namespace SurveyAnalysis.Models;

// One row in the project design screen: a survey field (データ項目) with its name,
// data type, analysis method, and — only when sentiment analysis is selected — whether to
// fire an alert and the threshold below which a claim is flagged. A 日付 field can be marked
// as the basis for monthly aggregation. Observable so the designer edits live.
public partial class DataField : ObservableObject
{
    // 項目名
    [ObservableProperty]
    private string _name = "";

    // データ型
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersonalInformation))]
    [NotifyPropertyChangedFor(nameof(IsDate))]
    private FieldType _fieldType = FieldType.FreeText;

    // 分析方法（なし / トピック割り当て / 感情極性分析）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSentimentSelected))]
    [NotifyPropertyChangedFor(nameof(ShowThreshold))]
    private AnalysisMethod _analysis = AnalysisMethod.None;

    // 月次集計の基準日にする（日付型のときだけ意味を持つ。これが無いと月次集計できない）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLoadDateAsDefaultEnabled))]
    private bool _useForAggregation;

    // 読み込んだときの日付をデフォルト値にする（日付型のみ）。
    [ObservableProperty]
    private bool _useLoadDateAsDefault;

    // アラートを発報する（不要なプロジェクトはオフにできる）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThreshold))]
    private bool _enableAlert = true;

    // アラート発報の閾値 (-0.9 most negative .. +0.5). Alert fires below this.
    [ObservableProperty]
    private double _alertThreshold = -0.5;

    // 感情極性分析が選ばれているか（アラート発報チェックの表示条件）。
    public bool IsSentimentSelected => Analysis == AnalysisMethod.Sentiment;

    // 閾値スライダーの表示条件：感情極性分析かつアラート発報がオン。
    public bool ShowThreshold => IsSentimentSelected && EnableAlert;

    // 日付型か（月次集計チェックボックスの表示条件）。
    public bool IsDate => FieldType == FieldType.Date;

    // 「読み込み日をデフォルトにする」を操作できるか。月次集計に使うときは、基準日に値が必ず
    // 入るよう固定オンにして変更不可（false）にする。
    public bool UseLoadDateAsDefaultEnabled => !UseForAggregation;

    // 月次集計をオンにしたら「読み込み日デフォルト」を強制オン（値が必ず入るように）。
    partial void OnUseForAggregationChanged(bool value)
    {
        if (value)
            UseLoadDateAsDefault = true;
    }

    // Drives the "暗号化" badge for the six personal-information types.
    public bool IsPersonalInformation => FieldTypeInfo.IsPersonalInformation(FieldType);

    // All selectable values, exposed (as instance properties so per-row binding works)
    // for the design screen's combo boxes.
    public FieldType[] FieldTypeOptions => System.Enum.GetValues<FieldType>();
    public AnalysisMethod[] AnalysisOptions => System.Enum.GetValues<AnalysisMethod>();
}
