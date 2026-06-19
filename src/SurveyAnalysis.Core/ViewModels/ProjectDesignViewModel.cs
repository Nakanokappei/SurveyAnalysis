using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.ViewModels;

// The project creation dialog. The survey field definitions (項目名・データ型・分析方法・
// アラート閾値) are stacked vertically and editable. Completion is signalled via events so the
// hosting window can close with the result.
public partial class ProjectDesignViewModel : ViewModelBase
{
    // Raised with the new project when the user confirms; the host closes the dialog with it.
    public event Action<Project>? Completed;

    // Raised when the user cancels; the host closes the dialog with no result.
    public event Action? Cancelled;

    // Raised when the user confirms deleting the existing project being edited; the host deletes it and
    // returns to the welcome screen. Only ever fired in edit mode (the delete action is edit-only).
    public event Action? DeleteRequested;

    // Returns true if the given (trimmed) name is free to use — set by the host to the shell's uniqueness
    // check, which excludes the project being edited. Null when no check is wired (design-time / tests),
    // in which case any name is accepted. The host confirms against it before the dialog closes.
    public Func<string, bool>? IsNameAvailable { get; set; }

    [ObservableProperty]
    private string _projectName = "新しいプロジェクト";

    // プロジェクトの説明（任意）。取り込み/OCR 時に LLM へ与えるヒント。
    [ObservableProperty]
    private string _projectDescription = "";

    // 設計中のデータ項目（縦に並ぶ）
    public ObservableCollection<DataField> Fields { get; } = new();

    // Non-null when editing an existing project's schema rather than creating a new one. Drives
    // the dialog title and the confirm button label, and tells CreateProject to update in place.
    private readonly Project? _editingProject;
    public bool IsEditing => _editingProject is not null;

    // The id of the project being edited (0/null in create modes). The host uses it to reload the saved
    // project when re-analysing existing responses after a topic rebuild.
    public long? EditingProjectId => _editingProject?.Id;
    public string DialogTitle => IsEditing ? "プロジェクトの構成" : "プロジェクト作成";
    public string ConfirmLabel => IsEditing ? "変更を保存" : "このデータ形式で作成";

    // Create mode: seed a few starter rows so the layout is populated.
    public ProjectDesignViewModel()
    {
        // Re-evaluate the confirm button whenever the field list or a field name changes.
        Fields.CollectionChanged += OnFieldsChanged;

        Fields.Add(new DataField { Name = "氏名", FieldType = FieldType.Name, Analysis = AnalysisMethod.None });
        Fields.Add(new DataField { Name = "記入日", FieldType = FieldType.Date, Analysis = AnalysisMethod.None, UseForAggregation = true });
        Fields.Add(new DataField { Name = "ご意見・ご要望", FieldType = FieldType.FreeText, Analysis = AnalysisMethod.Sentiment });
    }

    // Edit mode: open an existing project's schema. Fields are cloned so edits stay inside the
    // dialog until saved — Cancel discards them, and on save the live project is rebuilt from
    // storage by the host.
    public ProjectDesignViewModel(Project existing)
    {
        _editingProject = existing;
        Fields.CollectionChanged += OnFieldsChanged;
        ProjectName = existing.Name;
        ProjectDescription = existing.Description;
        foreach (var field in existing.Fields)
            Fields.Add(CloneField(field));
    }

    // Overrides the heuristic field types with the LLM's inference, matched by column name. Columns the
    // inference omits keep their heuristic type. Called by the CSV-create flow before the dialog is shown.
    public void ApplyInferredTypes(IReadOnlyDictionary<string, FieldType> inferred)
    {
        foreach (var field in Fields)
            if (inferred.TryGetValue(field.Name, out var type))
                field.FieldType = type;
    }

    // CSV-seeded create mode: one field per CSV column (data type guessed from the values), the
    // project name suggested from the file name. The parsed CSV is retained as SourceCsv so the host
    // can import its rows once the user has reviewed and confirmed the guessed schema.
    public ProjectDesignViewModel(byte[] csvBytes, string fileName)
    {
        SourceCsv = CsvFile.Parse(csvBytes);
        Fields.CollectionChanged += OnFieldsChanged;
        var suggested = System.IO.Path.GetFileNameWithoutExtension(fileName);
        ProjectName = string.IsNullOrWhiteSpace(suggested) ? "新しいプロジェクト" : suggested;
        foreach (var field in CsvProjectImport.InferFields(SourceCsv))
            Fields.Add(field);
    }

    // Non-null when this dialog was seeded from a CSV (the "CSV からプロジェクトを作る" flow); carries the
    // parsed rows so the host can import them after the project is created. Null for manual/edit modes.
    public CsvFile? SourceCsv { get; }

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

    // 確定（新規作成または変更保存）。必須欄が埋まっているときだけ実行可能。編集時は既存IDを引き継いだ
    // 下書きを返し、ホストが保存して開き直す。
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateProject()
    {
        var project = new Project
        {
            Id = _editingProject?.Id ?? 0,
            Name = string.IsNullOrWhiteSpace(ProjectName) ? "新しいプロジェクト" : ProjectName.Trim(),
            Description = ProjectDescription.Trim(),
        };
        foreach (var field in Fields)
            project.Fields.Add(field);

        Completed?.Invoke(project);
    }

    // キャンセルして閉じる
    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    // プロジェクトを削除（編集中の既存プロジェクトのみ）。破壊的操作なので、ホスト側で確認ダイアログを
    // 出してから実行する。
    [RelayCommand]
    private void DeleteProject() => DeleteRequested?.Invoke();

    // Clones a field so the edit dialog works on a detached copy. UseForAggregation is assigned
    // before UseLoadDateAsDefault so the aggregation→load-date rule does not overwrite the copy.
    private static DataField CloneField(DataField source) => new()
    {
        // Carry the row id so a schema edit updates the existing field in place (keeping its answers)
        // rather than deleting and re-inserting it.
        Id = source.Id,
        Name = source.Name,
        FieldType = source.FieldType,
        Analysis = source.Analysis,
        UseForAggregation = source.UseForAggregation,
        UseLoadDateAsDefault = source.UseLoadDateAsDefault,
    };
}
