using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
    }

    // The shell view model signals "create a project" via an event; opening a window is a
    // view concern, so we host the modal design dialog here.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null)
        {
            _shell.CreateProjectRequested -= OnCreateProjectRequested;
            _shell.CreateProjectFromCsvRequested -= OnCreateProjectFromCsvRequested;
            _shell.ImportRequested -= OnImportRequested;
            _shell.EditSchemaRequested -= OnEditSchemaRequested;
        }
        _shell = DataContext as MainWindowViewModel;
        if (_shell is not null)
        {
            _shell.CreateProjectRequested += OnCreateProjectRequested;
            _shell.CreateProjectFromCsvRequested += OnCreateProjectFromCsvRequested;
            _shell.ImportRequested += OnImportRequested;
            _shell.EditSchemaRequested += OnEditSchemaRequested;
        }
    }

    // プロジェクト作成（モーダルダイアログ）。作成された Project を受け取って開く。
    private async void OnCreateProjectRequested()
    {
        var dialog = new ProjectDesignWindow { DataContext = new ProjectDesignViewModel() };
        var project = await dialog.ShowDialog<Project?>(this);
        if (project is not null)
            _shell?.FinishProjectCreation(project);
    }

    // CSV からプロジェクトを作る。ファイルを選び、列からデータ項目を起こした作成ダイアログを開く。確定
    // されたら、見直し済みのスキーマでプロジェクトを保存し、同じCSVの行を回答として取り込む。
    private async void OnCreateProjectFromCsvRequested()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "CSV / TSV ファイルからプロジェクトを作成",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                // Same set the import picker accepts; the delimiter is auto-detected on parse (CsvFile).
                new FilePickerFileType("CSV / TSV / テキスト")
                {
                    Patterns = new[] { "*.csv", "*.tsv", "*.txt" },
                    MimeTypes = new[] { "text/csv", "text/tab-separated-values", "text/plain" },
                },
            },
        });

        if (files.Count == 0)
            return;

        await using var stream = await files[0].OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        var designViewModel = new ProjectDesignViewModel(memory.ToArray(), files[0].Name);
        var dialog = new ProjectDesignWindow { DataContext = designViewModel };
        var project = await dialog.ShowDialog<Project?>(this);
        if (project is not null && designViewModel.SourceCsv is { } csv)
            _shell?.FinishProjectFromCsv(project, csv, files[0].Name);
    }

    // インポート（モーダルダイアログ）。CSVをマージする画面を開く。
    private async void OnImportRequested(Project project)
    {
        var dialog = new ImportWindow { DataContext = new ImportViewModel(project, AppServices.Responses, AppServices.Analytics) };
        await dialog.ShowDialog(this);
    }

    // データ項目の編集（モーダルダイアログ）。作成ダイアログを編集モードで開き、保存された下書きを
    // 受け取って永続化する。
    private async void OnEditSchemaRequested(Project project)
    {
        var dialog = new ProjectDesignWindow { DataContext = new ProjectDesignViewModel(project) };
        var edited = await dialog.ShowDialog<Project?>(this);
        if (edited is not null)
            _shell?.ApplySchemaEdit(edited);
    }

    // 設定（モーダルダイアログ）。開くときに保存値を読み込み、閉じたら書き戻す。
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null)
            return;
        var viewModel = _shell.CreateSettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        await dialog.ShowDialog(this);
        viewModel.Save();
    }
}
