using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.Views;

public partial class ImportView : UserControl
{
    public ImportView() => InitializeComponent();

    // Picking a file is a view concern, so the CSV file picker lives here; the chosen file's bytes
    // are handed to the view model to parse and preview.
    private async void OnSelectFileClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "CSVファイルを選択",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" }, MimeTypes = new[] { "text/csv" } },
            },
        });

        if (files.Count == 0 || DataContext is not ImportViewModel viewModel)
            return;

        await using var stream = await files[0].OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        viewModel.LoadCsv(memory.ToArray(), files[0].Name);
    }
}
