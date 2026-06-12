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
            Title = "CSV / TSV ファイルを選択",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                // CSV (comma), TSV (tab — Excel の「テキスト(タブ区切り)」), and plain .txt exports.
                // The delimiter is auto-detected on parse (see CsvFile), so all share one filter.
                new FilePickerFileType("CSV / TSV / テキスト")
                {
                    Patterns = new[] { "*.csv", "*.tsv", "*.txt" },
                    MimeTypes = new[] { "text/csv", "text/tab-separated-values", "text/plain" },
                },
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
