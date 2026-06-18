using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

    // Raised after a successful merge (responses persisted + star rebuilt), carrying the project so the
    // host can run the import-time sentiment/topic analysis and re-project. That step lives in the
    // WinForms layer because it needs the LLM client and a progress dialog; headless callers / tests
    // simply ignore the event (the merge itself is already complete).
    public event Action<Project>? Merged;

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

        // 対応づけ候補は「取り込まない」＋プロジェクトの項目名。読み込み時に列名一致で自動割り当てし
        // （AutoMapColumnsByName）、一致しない列はホストが LLM で補う。
        MappingOptions = new ObservableCollection<string> { NoMapping };
        foreach (var field in project.Fields)
            if (!string.IsNullOrWhiteSpace(field.Name) && !MappingOptions.Contains(field.Name))
                MappingOptions.Add(field.Name);
    }

    // 選択されたCSVの中身を読み込み、ヘッダーから列を作り直してプレビューを表示する（同期版・テスト用）。
    public void LoadCsv(byte[] bytes, string fileName) => ApplyCsv(CsvFile.Parse(bytes), fileName);

    // UI用の非同期版。重い解析（CsvFile.Parse）を背景スレッドで行い、その間UIを止めない。解析後に
    // UIスレッドへ戻って列とプレビューをまとめて適用するので、表を1列ずつ更新せず大きなCSVでも滑らか。
    public async Task LoadCsvAsync(byte[] bytes, string fileName)
    {
        StatusMessage = "CSVを読み込んでいます…";
        var csv = await Task.Run(() => CsvFile.Parse(bytes));
        ApplyCsv(csv, fileName);
    }

    // 解析済みのCSVをプレビューへ適用する：ヘッダーで列を作り直し、行を取り込み、状態を更新する。
    private void ApplyCsv(CsvFile csv, string fileName)
    {
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

        // Pre-assign each column whose name exactly matches a project field; the rest stay blank for the
        // host's LLM pass / the user.
        AutoMapColumnsByName();

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

    // マージ：対応づけを各行に適用して回答(responses)を保存し、件数を表示する。CSV列と取り込み先は原則
    // 1：1。ただし取り込み先が選択肢型のときに限り、複数列を「; 」区切りの複数選択にまとめられる。
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void Merge()
    {
        var groups = MappedGroups();

        // Enforce 1:1 for every non-選択肢 target — only a 選択肢 field may collect more than one column.
        var offending = groups.FirstOrDefault(g => !g.IsChoice && g.Indices.Count > 1);
        if (offending.Field is not null)
        {
            StatusMessage = $"「{offending.Field}」に複数の列が割り当てられています。選択肢型以外の項目は1列だけ対応づけできます。";
            return;
        }

        var responses = new List<SurveyResponse>();
        foreach (var row in _rows)
        {
            var answers = new List<FieldAnswer>();
            foreach (var group in groups)
            {
                var values = group.Indices
                    .Select(i => i < row.Length ? row[i] : "")
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim());
                // 選択肢: merge the columns into one "; "-joined multi-select; otherwise the single column.
                var value = group.IsChoice ? string.Join("; ", values) : values.FirstOrDefault() ?? "";
                if (value.Length > 0)
                    answers.Add(new FieldAnswer(group.Field, value));
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

        // The raw rows + star are now in place; let the host analyse the new 自由記述 (sentiment/topic)
        // and re-project, mirroring the CSV-create flow.
        Merged?.Invoke(_project);
    }

    // The mapped columns grouped by target field (in first-seen order), each carrying its CSV column
    // indices and whether the target is a 選択肢 field (the only type allowed to gather several columns).
    private List<(string Field, List<int> Indices, bool IsChoice)> MappedGroups()
    {
        var byField = new Dictionary<string, (List<int> Indices, bool IsChoice)>();
        var order = new List<string>();
        for (var i = 0; i < Columns.Count; i++)
        {
            var mapping = Columns[i].SelectedMapping;
            if (mapping is null || mapping == NoMapping)
                continue;
            if (!byField.TryGetValue(mapping, out var entry))
            {
                var field = _project.Fields.FirstOrDefault(f => f.Name == mapping);
                byField[mapping] = entry = (new List<int>(), field?.FieldType == FieldType.Choice);
                order.Add(mapping);
            }
            entry.Indices.Add(i);
        }
        return order.Select(field => (field, byField[field].Indices, byField[field].IsChoice)).ToList();
    }

    // Auto-assigns each CSV column to the project field with the exact same name (a 1:1 mapping; a
    // non-選択肢 field is matched at most once). Columns with no name match are left blank for the LLM
    // pass / the user. Called on load.
    private void AutoMapColumnsByName()
    {
        var takenNonChoice = new HashSet<string>();
        foreach (var column in Columns)
        {
            var field = _project.Fields.FirstOrDefault(f => f.Name == column.Name && !string.IsNullOrWhiteSpace(f.Name));
            if (field is not null)
                TryAssign(column, field.Name, takenNonChoice);
        }
    }

    // Applies host-provided (LLM) suggestions to the columns still blank, respecting 1:1 (a non-選択肢
    // field already taken is skipped). Called by the import dialog after its LLM mapping pass.
    public void ApplyMappingSuggestions(IReadOnlyDictionary<string, string> columnToField)
    {
        var takenNonChoice = TakenNonChoiceFields();
        foreach (var column in Columns)
            if (column.SelectedMapping is null && columnToField.TryGetValue(column.Name, out var fieldName))
                TryAssign(column, fieldName, takenNonChoice);
        MergeCommand.NotifyCanExecuteChanged();
    }

    // Assigns a column to a field unless that would break 1:1 (a non-選択肢 field already mapped). 選択肢
    // fields may be the target of several columns (merged at import).
    private void TryAssign(ImportColumn column, string fieldName, HashSet<string> takenNonChoice)
    {
        var field = _project.Fields.FirstOrDefault(f => f.Name == fieldName);
        if (field is null)
            return;
        var isChoice = field.FieldType == FieldType.Choice;
        if (!isChoice && !takenNonChoice.Add(fieldName))
            return;   // a non-選択肢 field is already taken — keep it 1:1
        column.SelectedMapping = fieldName;
    }

    // The non-選択肢 fields already mapped by some column (so a later assignment keeps 1:1).
    private HashSet<string> TakenNonChoiceFields()
    {
        var taken = new HashSet<string>();
        foreach (var column in Columns)
            if (column.SelectedMapping is { } mapping && mapping != NoMapping)
            {
                var field = _project.Fields.FirstOrDefault(f => f.Name == mapping);
                if (field is not null && field.FieldType != FieldType.Choice)
                    taken.Add(mapping);
            }
        return taken;
    }

    // A few distinct non-empty sample values for a column (by name), for the LLM mapping prompt.
    public IReadOnlyList<string> SampleValuesFor(string columnName)
    {
        var index = -1;
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Name == columnName) { index = i; break; }
        if (index < 0)
            return Array.Empty<string>();
        return _rows
            .Select(row => index < row.Length ? row[index] : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .Take(5)
            .ToList();
    }

    // The project's fields offered as mapping targets (name + Japanese type label), for the LLM prompt.
    public IReadOnlyList<(string Name, string TypeLabel)> TargetFields =>
        _project.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => (f.Name, FieldTypeInfo.Label(f.FieldType)))
            .ToList();

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
