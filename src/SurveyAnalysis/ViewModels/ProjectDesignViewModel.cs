using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The project creation dialog. The survey field definitions (項目名・データ型・分析方法・
// アラート閾値) are stacked vertically and editable; the column design can be test-run
// against an already-loaded scan image. No OCR runs here — RunTest shows placeholder output.
// Completion is signalled via events so the hosting window can close with the result.
public partial class ProjectDesignViewModel : ViewModelBase
{
    // Raised with the new project when the user confirms; the host closes the dialog with it.
    public event Action<Project>? Completed;

    // Raised when the user cancels; the host closes the dialog with no result.
    public event Action? Cancelled;

    [ObservableProperty]
    private string _projectName = "新しいプロジェクト";

    // 設計中のデータ項目（縦に並ぶ）
    public ObservableCollection<DataField> Fields { get; } = new();

    // テスト用にその場で指定した画像（新規プロジェクトには読み込み済み回答がまだ無いため、
    // 一覧から選ぶのではなく単発でファイルを指定する）。プロトタイプでは選択ダイアログは未実装。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestImage))]
    private string _testImagePath = "";

    public bool HasTestImage => !string.IsNullOrEmpty(TestImagePath);

    // テスト実行結果（各項目に何が抽出されるかのプレビュー）
    public ObservableCollection<TestResultRow> TestResults { get; } = new();

    [ObservableProperty]
    private bool _hasTestResult;

    public ProjectDesignViewModel()
    {
        // Re-evaluate the create button whenever the field list or a field name changes.
        Fields.CollectionChanged += OnFieldsChanged;

        // Seed with a few starter rows so the layout is populated.
        Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name, Analysis = AnalysisMethod.None });
        Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, Analysis = AnalysisMethod.None, UseForAggregation = true });
        Fields.Add(new DataField { Name = "ご意見・ご要望", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
    }

    // プロジェクト名が変わったら作成可否を再判定
    partial void OnProjectNameChanged(string value) => CreateProjectCommand.NotifyCanExecuteChanged();

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DataField field in e.OldItems)
                field.PropertyChanged -= OnFieldPropertyChanged;
        if (e.NewItems is not null)
            foreach (DataField field in e.NewItems)
                field.PropertyChanged += OnFieldPropertyChanged;
        CreateProjectCommand.NotifyCanExecuteChanged();
    }

    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataField.Name))
            CreateProjectCommand.NotifyCanExecuteChanged();
    }

    // 作成可否：プロジェクト名と全項目名が入力済みであること（必須欄が空ならボタン無効）。
    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(ProjectName)
        && Fields.Count > 0
        && Fields.All(field => !string.IsNullOrWhiteSpace(field.Name));

    // 項目を追加
    [RelayCommand]
    private void AddField() => Fields.Add(new DataField { Name = "", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.None });

    // 項目を削除
    [RelayCommand]
    private void RemoveField(DataField field) => Fields.Remove(field);

    // テスト用の画像をその場で指定（プロトタイプではファイル選択ダイアログは未実装）
    [RelayCommand]
    private void PickTestImage()
    {
        TestImagePath = @"C:\Temp\sample_scan.jpg";
        TestResults.Clear();
        HasTestResult = false;
    }

    // 読み込み済み画像でテスト（ダミー抽出結果を表示）
    [RelayCommand]
    private void RunTest()
    {
        TestResults.Clear();
        foreach (var field in Fields)
        {
            // Personal information is never shown to the user (spec §8); show a masked marker.
            var value = field.IsPersonalInformation
                ? "（暗号化・非表示）"
                : SampleExtraction(field);
            TestResults.Add(new TestResultRow
            {
                FieldName = string.IsNullOrWhiteSpace(field.Name) ? "（未命名）" : field.Name,
                FieldTypeLabel = FieldTypeInfo.Label(field.FieldType),
                AnalysisLabel = FieldTypeInfo.Label(field.Analysis),
                AnalysisResult = SampleAnalysisResult(field.Analysis),
                ExtractedValue = value
            });
        }
        HasTestResult = true;
    }

    // このデータ形式でプロジェクトを作成（必須欄が埋まっているときだけ実行可能）
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateProject()
    {
        var project = new Project { Name = string.IsNullOrWhiteSpace(ProjectName) ? "新しいプロジェクト" : ProjectName };
        foreach (var field in Fields)
            project.Fields.Add(field);
        foreach (var month in new[] { "2026年5月", "2026年4月", "2026年3月" })
            project.Months.Add(month);
        Completed?.Invoke(project);
    }

    // キャンセルして閉じる
    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    // Produces a placeholder analysis result per analysis method (none / topic / sentiment).
    private static string SampleAnalysisResult(AnalysisMethod method) => method switch
    {
        AnalysisMethod.Topic => "配線・接続",
        AnalysisMethod.Sentiment => "ポジティブ (+0.6)",
        _ => "—"
    };

    // Produces a placeholder extracted value per non-PII field type.
    private static string SampleExtraction(DataField field) => field.FieldType switch
    {
        FieldType.Date => "2026/05/28",
        FieldType.ChoiceText => "とても満足",
        FieldType.ChoiceNumber => "5",
        FieldType.Number => "4",
        FieldType.PrefectureOnly => "東京都",
        FieldType.CityOnly => "新宿区",
        FieldType.PostalCodeOnly => "160-0022",
        FieldType.FreeText => "担当の方が丁寧に説明してくれて安心できました。",
        _ => "（サンプル値）"
    };

    // One row of the test preview table.
    public class TestResultRow
    {
        public required string FieldName { get; init; }
        public required string FieldTypeLabel { get; init; }
        public required string AnalysisLabel { get; init; }
        public required string AnalysisResult { get; init; }
        public required string ExtractedValue { get; init; }
    }
}
