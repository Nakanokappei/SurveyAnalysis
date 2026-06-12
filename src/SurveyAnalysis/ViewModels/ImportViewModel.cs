using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// インポート画面。デジタル回収したCSVを1件ずつ（単票）プレビューし、CSVの各列をプロジェクトの
// データ項目へ対応づけてマージ（保存）する。列は読み込んだCSVのヘッダーから作られ、行を移動しても
// 対応づけ（SelectedMapping）は保持される。マージは対応づけを適用して回答(responses)を永続化する。
public partial class ImportViewModel : ViewModelBase
{
    private const string NoMapping = "（取り込まない）";

    private readonly Project _project;
    private readonly ResponseRepository _responses;
    private readonly AnalyticsRepository _analytics;

    [ObservableProperty]
    private string _selectedFile = "（CSVファイル未選択）";

    [ObservableProperty]
    private string _statusMessage = "";

    // CSVの列（列名＋プロジェクト項目への対応）。読み込み時にヘッダーから作り直す。
    public ObservableCollection<ImportColumn> Columns { get; } = new();

    // 取り込み対象の行（各列の値）。読み込んだCSVのデータ行。
    private List<string[]> _rows = new();

    // 取り込み元ファイル名（responses.source に保存）。
    private string _source = "";

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

    public ImportViewModel(Project project, ResponseRepository responses, AnalyticsRepository analytics)
    {
        _project = project;
        _responses = responses;
        _analytics = analytics;

        // 対応づけ候補は「取り込まない」＋プロジェクトの項目名。自動マッピングはしない（誤割り当て防止）。
        MappingOptions = new ObservableCollection<string> { NoMapping };
        foreach (var field in project.Fields)
            if (!string.IsNullOrWhiteSpace(field.Name) && !MappingOptions.Contains(field.Name))
                MappingOptions.Add(field.Name);
    }

    // 選択されたCSVの中身を読み込み、ヘッダーから列を作り直してプレビューを表示する。
    public void LoadCsv(byte[] bytes, string fileName)
    {
        var csv = CsvFile.Parse(bytes);

        // 前回の列の購読を解除してから、ヘッダーで作り直す。
        foreach (var column in Columns)
            column.PropertyChanged -= OnColumnChanged;
        Columns.Clear();
        foreach (var name in csv.Header)
        {
            var column = new ImportColumn { Name = name };
            column.PropertyChanged += OnColumnChanged;
            Columns.Add(column);
        }

        _rows = new List<string[]>(csv.Rows);
        _source = fileName;
        SelectedFile = fileName;
        CurrentIndex = 0;
        UpdateValues();

        StatusMessage = csv.Rows.Count == 0
            ? "このCSVには取り込める行がありませんでした。"
            : $"{csv.Header.Count} 列・{csv.Rows.Count} 行を読み込みました。各列の取り込み先を選んでください。";

        // 新しいデータに合わせてナビゲーションとマージの可否を更新する。
        FirstCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        LastCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RowPosition));
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

    // 行があり、すべての列で取り込み先が選ばれている（未選択が1つも無い）ときだけマージ可能。
    private bool CanMerge() => _rows.Count > 0 && Columns.Count > 0 && Columns.All(c => c.SelectedMapping is not null);

    // マージ：対応づけを各行に適用して回答(responses)を保存し、件数を表示する。
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void Merge()
    {
        var responses = new List<SurveyResponse>();
        foreach (var row in _rows)
        {
            var answers = new List<FieldAnswer>();
            for (var i = 0; i < Columns.Count; i++)
            {
                var mapping = Columns[i].SelectedMapping;
                if (mapping is null || mapping == NoMapping)
                    continue;
                answers.Add(new FieldAnswer(mapping, i < row.Length ? row[i] : ""));
            }
            if (answers.Count > 0)
                responses.Add(new SurveyResponse { Answers = answers });
        }

        if (responses.Count == 0)
        {
            StatusMessage = "取り込む列がありません（すべて「（取り込まない）」になっています）。";
            return;
        }

        _responses.InsertResponses(_project.Id, _source, responses);

        // Refresh the analytics star immediately so the time/region/topic slices reflect the new
        // rows without waiting for a slice to be opened. Rebuild is idempotent (clears then rebuilds
        // this project's facts), so re-importing simply recomputes the dimensions and facts.
        _analytics.Rebuild(_project);

        var total = _responses.CountForProject(_project.Id);
        StatusMessage = $"{responses.Count} 件をマージしました（このプロジェクトの回答数：{total} 件）。";
    }

    // CSVの1列：列名、プロジェクト項目への対応（ドロップダウン選択）、現在行の値。
    // 列インスタンスは読み込み中は固定なので、行移動しても対応づけ（SelectedMapping）は保持される。
    public partial class ImportColumn : ObservableObject
    {
        public required string Name { get; init; }

        [ObservableProperty]
        private string? _selectedMapping;

        [ObservableProperty]
        private string _currentValue = "";
    }
}
