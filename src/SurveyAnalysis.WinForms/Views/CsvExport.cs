using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// Exports a 切り口 report's currently visible grid (the analysis table or the drilled 個票 list) to a CSV
// file the user picks — "表示中のデータ" exactly as shown on screen. UTF-8 with BOM so Excel opens Japanese
// correctly; RFC4180 quoting (wrap in quotes when needed, double any inner quotes).
internal static class CsvExport
{
    public static void Export(IWin32Window owner, DataGridView grid, string title)
    {
        if (grid.Rows.Count == 0)
        {
            MessageBox.Show(owner, "エクスポートできるデータがありません。", "CSV エクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = SanitizeFileName(title) + ".csv",
            AddExtension = true,
            DefaultExt = "csv",
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return;
        File.WriteAllText(dialog.FileName, BuildCsv(grid), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // Serialises the grid's visible columns + data rows. The two-line analysis headers ("感情極性\n平均")
    // collapse to one cell ("感情極性 平均").
    private static string BuildCsv(DataGridView grid)
    {
        var columns = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Escape((c.HeaderText ?? "").Replace("\n", " ").Trim()))));
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow)
                continue;
            sb.AppendLine(string.Join(",", columns.Select(c => Escape(row.Cells[c.Index].Value?.ToString() ?? ""))));
        }
        return sb.ToString();
    }

    // RFC4180: quote a value only when it contains a comma, quote or line break; double inner quotes.
    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";

    // A safe default file name from the report title (replacing characters a file name can't contain).
    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "レポート" : cleaned;
    }
}
