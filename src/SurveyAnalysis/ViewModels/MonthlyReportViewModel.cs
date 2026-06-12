using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The monthly report screen reached from the sidebar month links. Lays out the
// management-facing summary for one month and offers PDF export / email send actions.
// Both actions are placeholders in this prototype.
public partial class MonthlyReportViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _month;

    [ObservableProperty]
    private string _projectName;

    // 会社名（設定で指定。レポートのヘッダーに表示する。プロトタイプでは既定値を表示）。
    public string CompanyName { get; } = "○○ケーブル株式会社";

    [ObservableProperty]
    private string _statusMessage = "";

    // KPI summary cards for the month.
    public int TotalResponses { get; } = 137;
    public int NegativeCount { get; } = 12;
    public string AverageSentiment { get; } = "+0.42";
    public string ResponseRate { get; } = "68%";

    // Topic distribution reused for the report body.
    public ObservableCollection<BarItem> TopicBars { get; } = new();

    // 経営向けハイライト（ダミー）
    public ObservableCollection<string> Highlights { get; } = new()
    {
        "総回答数は前月比 +9%。配線・接続に関する声が最多。",
        "ネガティブ回答は12件（全体の8.8%）。料金説明と配線品質に集中。",
        "スタッフ対応の満足度は高水準を維持（平均 +0.61）。",
    };

    public MonthlyReportViewModel(Project project, string month)
    {
        _projectName = project.Name;
        _month = month;

        var max = 1;
        foreach (var d in SampleData.TopicCounts)
            if (d.Count > max) max = d.Count;
        foreach (var d in SampleData.TopicCounts)
            TopicBars.Add(new BarItem { Label = d.Label, Count = d.Count, BarWidth = d.Count / (double)max * 180 });
    }

    // PDFを出力（プロトタイプでは未実装）
    [RelayCommand]
    private void ExportPdf() => StatusMessage = "（プロトタイプ）PDF出力は未実装です。";

    // 経営向けにメール送信（プロトタイプでは未実装）
    [RelayCommand]
    private void SendEmail() => StatusMessage = "（プロトタイプ）メール送信は未実装です。";
}
