using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// Shared building blocks for the 切り口 (analysis-table) screens — 時間別(期間/曜日) / 地域別 / トピック別.
// All three derive from PeriodScopedViewModel and render the same parts: a 集計期間 dropdown top-right, a
// row(dimension) × field(column) table whose columns come from the view model, a trailing 全体 total
// row, and — where a dimension drills down — a 個票一覧 list. Centralising these keeps the three controls
// thin and their tables identical (DataGridView Fill mode, soft card border) and matches the dashboard.
internal static class SliceTableView
{
    // A white card with a soft 1px border, mirroring the dashboard's cards (softer than FixedSingle).
    public static Panel Card()
    {
        var panel = new Panel { BackColor = Color.White, Padding = new Padding(16) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.CardBorder);
            var r = panel.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        };
        return panel;
    }

    // The 感情極性の推移 card shown at the top of each 切り口: a title over a line chart of the selected
    // period's monthly average sentiment. The chart is returned so the host can feed it data (SetData)
    // and toggle the card's visibility from the view model on each refresh.
    public static Panel BuildTrendCard(out SentimentTrendChart chart)
    {
        var card = Card();
        var view = new SentimentTrendChart { Dock = DockStyle.Fill };
        chart = view;
        var title = new Label
        {
            Text = "感情極性の推移", Dock = DockStyle.Top, AutoSize = true,
            ForeColor = Theme.TitleText, Font = Theme.Font(11f, FontStyle.Bold), Padding = new Padding(0, 0, 0, 6),
        };
        card.Controls.Add(view);    // Fill (added first, behind)
        card.Controls.Add(title);   // Top
        return card;
    }

    // The 対象期間 picker (label + the shared DateRangePicker trigger) shown top-right — the same picker the
    // dashboard uses. Seeds the trigger from the view model and, when the user applies a range, pushes it
    // into the VM (which re-runs the aggregation) and refreshes the screen via onChanged.
    public static Control BuildPeriodPicker(PeriodScopedViewModel vm, DateRangePicker picker, Action onChanged)
    {
        picker.SetCurrent(vm.Preset, vm.From, vm.To);
        picker.RangeChanged += (preset, from, to) => { vm.SetRange(preset, from, to); onChanged(); };

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right };
        panel.Controls.Add(new Label { Text = "対象期間", AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(10f), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 8, 0) });
        panel.Controls.Add(picker.Trigger);
        return panel;
    }

    // The analysis table: a leading dimension column then one column per project field, each headed by
    // the field name over its aggregation word (種類数 / 合計 / 平均). Built once from the view model's
    // fixed column set; rows are filled per scope via FillAnalysisGrid.
    public static DataGridView BuildAnalysisGrid(string dimensionHeader, IReadOnlyList<AnalysisColumn> columns)
    {
        var grid = NewGrid();
        grid.Columns.Add(TextColumn(dimensionHeader, fillWeight: 26));
        // A dedicated 感情極性 column right after the dimension, shown in every report.
        grid.Columns.Add(TextColumn("感情極性\n平均", fillWeight: 16));
        foreach (var col in columns)
        {
            // Drop the redundant second line when the column name already is its measure (the plain 件数
            // summary column reads just "件数"); cross-tab columns keep "ラベル\n件数".
            var header = col.AggregationLabel == col.Name ? col.Name : $"{col.Name}\n{col.AggregationLabel}";
            grid.Columns.Add(TextColumn(header, fillWeight: 16));
        }
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        return grid;
    }

    // Re-populates the analysis grid: one DataGridView row per AnalysisRow (the source row kept in Tag
    // so a click can map back to it for drilling), then the bold 全体 total row last (no Tag, so it is
    // never drillable).
    public static void FillAnalysisGrid(DataGridView grid, IEnumerable<AnalysisRow> rows, AnalysisRow? total)
    {
        grid.Rows.Clear();
        foreach (var row in rows)
        {
            var index = grid.Rows.Add(Cells(row));
            grid.Rows[index].Tag = row;
        }
        if (total is not null)
        {
            var index = grid.Rows.Add(Cells(total));
            var totalRow = grid.Rows[index];
            totalRow.DefaultCellStyle.Font = Theme.Font(9.5f, FontStyle.Bold);
            totalRow.DefaultCellStyle.BackColor = Theme.ContentBack;
        }
    }

    // The 個票一覧 list (記入日 / トピック / 感情 / 抜粋), identical to the dashboard's responses table.
    // PII never appears here — ResponseRowFactory builds the rows from non-personal fields only.
    public static DataGridView BuildResponsesGrid()
    {
        var grid = NewGrid();
        grid.Columns.Add(TextColumn("記入日", 22));
        grid.Columns.Add(TextColumn("トピック", 22));
        grid.Columns.Add(TextColumn("感情", 14));
        grid.Columns.Add(TextColumn("抜粋（フリーテキスト）", 42));
        return grid;
    }

    public static void FillResponsesGrid(DataGridView grid, IEnumerable<SurveyRow> rows)
    {
        grid.Rows.Clear();
        foreach (var row in rows)
            grid.Rows.Add(row.EntryDate, row.Topic, row.Sentiment, row.Excerpt);
    }

    // A clickable breadcrumb (全期間 ＞ 2026年度 ＞ …): each segment is a link except the last (the
    // current scope), shown bold. Clicking a segment invokes onNavigate to return to that depth.
    public static FlowLayoutPanel BuildBreadcrumb(IReadOnlyList<Crumb> crumbs, Action<Crumb> onNavigate)
    {
        var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            var isLast = i == crumbs.Count - 1;
            var link = new Label
            {
                Text = crumb.Display,
                AutoSize = true,
                Font = Theme.Font(9.5f, isLast ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = isLast ? Theme.TitleText : Theme.Accent,
                Cursor = isLast ? Cursors.Default : Cursors.Hand,
                Margin = new Padding(0, 2, 4, 2),
            };
            if (!isLast)
                link.Click += (_, _) => onNavigate(crumb);
            bar.Controls.Add(link);
        }
        return bar;
    }

    // ===== shared grid primitives =====

    private static DataGridView NewGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            // No gridlines between data cells — a flat list, matching the dashboard's tables.
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            Font = Theme.Font(9.5f),
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Font(9.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        // Soft row-selection highlight (not the heavy system blue) and no header tint for the selected
        // column — same as the dashboard table so the two never diverge.
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = Theme.TitleText;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xD6, 0xE6, 0xF5);
        grid.DefaultCellStyle.SelectionForeColor = Theme.TitleText;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.ContentBack;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
        return grid;
    }

    private static DataGridViewTextBoxColumn TextColumn(string header, int fillWeight) =>
        new() { HeaderText = header, FillWeight = fillWeight, SortMode = DataGridViewColumnSortMode.NotSortable };

    // Flattens an AnalysisRow into grid cells: the dimension label, the 感情極性 measure, then one cell
    // per field column (the order must match BuildAnalysisGrid).
    private static object[] Cells(AnalysisRow row)
    {
        var cells = new object[2 + row.Cells.Count];
        cells[0] = row.Label;
        cells[1] = row.Sentiment;
        for (var i = 0; i < row.Cells.Count; i++)
            cells[i + 2] = row.Cells[i];
        return cells;
    }
}
