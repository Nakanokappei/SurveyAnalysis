using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// インポート画面。デジタル回収したCSVを1件ずつ（単票）プレビューし、CSVの各列を
// プロジェクトのデータ項目へ対応づける。プロトタイプではダミーデータ。
public partial class ImportViewModel : ViewModelBase
{
    private const string NoMapping = "（取り込まない）";

    [ObservableProperty]
    private string _selectedFile = "（CSVファイル未選択）";

    [ObservableProperty]
    private string _statusMessage = "";

    // CSVの列（列名＋プロジェクト項目への対応）。
    public ObservableCollection<ImportColumn> Columns { get; }

    // 取り込み対象の行（各列の値）。プロトタイプのダミー。
    private readonly List<string[]> _rows;

    // 対応づけ候補（「取り込まない」＋プロジェクトの項目名）。
    public ObservableCollection<string> MappingOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowPosition))]
    [NotifyCanExecuteChangedFor(nameof(FirstCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(LastCommand))]
    private int _currentIndex;

    // 「3 / 10」のような位置表示。
    public string RowPosition => _rows.Count == 0 ? "0 / 0" : $"{CurrentIndex + 1} / {_rows.Count}";

    public ImportViewModel(Project? project)
    {
        MappingOptions = new ObservableCollection<string> { NoMapping };
        if (project is not null)
            foreach (var field in project.Fields)
                if (!string.IsNullOrWhiteSpace(field.Name) && !MappingOptions.Contains(field.Name))
                    MappingOptions.Add(field.Name);

        // CSVの生の列名。自動マッピングはしない（誤割り当て防止）。各列は既定で未選択
        // (SelectedMapping = null) にして、ユーザーに取り込み先の選択を迫る。
        Columns = new ObservableCollection<ImportColumn>
        {
            new() { Name = "回答日" },
            new() { Name = "工事内容" },
            new() { Name = "スタッフ評価" },
            new() { Name = "自由記述" },
        };
        // 対応づけが変わるたびにマージ可否（全列が決まったか）を再判定する。
        foreach (var column in Columns)
            column.PropertyChanged += OnColumnChanged;

        _rows = new List<string[]>
        {
            new[] { "2026/05/20", "宅内配線工事", "4", "料金プランの比較資料がほしい。" },
            new[] { "2026/05/21", "訪問・点検", "5", "希望日にすぐ予約できて助かった。" },
            new[] { "2026/05/22", "宅内配線工事", "2", "接続が不安定で再訪問をお願いした。" },
            new[] { "2026/05/23", "宅内配線工事", "5", "担当の方の説明が分かりやすかった。" },
        };

        UpdateValues();
    }

    // 列の対応づけが変わったら、マージボタンの有効/無効を更新。
    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportColumn.SelectedMapping))
            MergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentIndexChanged(int value) => UpdateValues();

    // 列（ドロップダウン付き）は固定のまま、現在行の値だけ各列へ流し込む。
    private void UpdateValues()
    {
        if (CurrentIndex < 0 || CurrentIndex >= _rows.Count)
            return;
        var row = _rows[CurrentIndex];
        for (var i = 0; i < Columns.Count; i++)
            Columns[i].CurrentValue = i < row.Length ? row[i] : "";
    }

    private bool CanGoPrevious() => CurrentIndex > 0;
    private bool CanGoNext() => CurrentIndex < _rows.Count - 1;

    // 先頭
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void First() => CurrentIndex = 0;

    // 戻る
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => CurrentIndex--;

    // 次へ
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => CurrentIndex++;

    // 最後
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Last() => CurrentIndex = _rows.Count - 1;

    // CSVファイルを選択（プロトタイプでは未実装）
    [RelayCommand]
    private void SelectFile()
    {
        SelectedFile = @"C:\Surveys\digital_responses_202605.csv";
        StatusMessage = "（プロトタイプ）ファイル選択ダイアログは未実装です。プレビューはダミーです。";
    }

    // すべての列で取り込み先が選ばれている（未選択が1つも無い）ときだけマージ可能。
    private bool CanMerge() => Columns.Count > 0 && Columns.All(c => c.SelectedMapping is not null);

    // マージ（プロトタイプでは未実装）
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void Merge() => StatusMessage = $"（プロトタイプ）{_rows.Count} 件を、設定した対応づけでマージした想定です。";

    // CSVの1列：列名、プロジェクト項目への対応（ドロップダウン選択）、現在行の値。
    // 列インスタンスは固定なので、行移動しても対応づけ（SelectedMapping）は保持される。
    public partial class ImportColumn : ObservableObject
    {
        public required string Name { get; init; }

        [ObservableProperty]
        private string? _selectedMapping;

        [ObservableProperty]
        private string _currentValue = "";
    }
}
