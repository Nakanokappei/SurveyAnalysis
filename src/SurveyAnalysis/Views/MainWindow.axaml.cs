using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            _shell.ImportRequested -= OnImportRequested;
            _shell.EditSchemaRequested -= OnEditSchemaRequested;
        }
        _shell = DataContext as MainWindowViewModel;
        if (_shell is not null)
        {
            _shell.CreateProjectRequested += OnCreateProjectRequested;
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

    // インポート（モーダルダイアログ）。CSVをマージする画面を開く。
    private async void OnImportRequested(Project project)
    {
        var dialog = new ImportWindow { DataContext = new ImportViewModel(project, AppServices.Responses) };
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
