using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SurveyAnalysis.Data;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The CSV import dialog — the WinForms counterpart of ImportView.axaml. Picks a CSV, previews it one
// record at a time (単票) with row navigation, maps each CSV column to a project field, and merges.
// Binds to the existing ImportViewModel: navigation/merge run its commands, the status line and row
// position follow its properties, and the per-column rows are rebuilt once after a file is loaded. Built
// from layout containers (no explicit coordinates or width math): a fixed top area (intro / file card /
// status) docks at the top and the preview fills the rest, the file card and preview header are
// two-column grids that right-align their buttons, and the column header shares the rows' three-column
// proportions so the columns line up.
internal sealed class ImportForm : Form
{
    // Shared column proportions for the preview header and every column row, so they align.
    private const float NamePct = 30f, MappingPct = 30f, ValuePct = 40f;

    private readonly ImportViewModel _vm;

    private readonly Label _selectedFile = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(10f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = ColorTranslator.FromHtml("#B45309"), Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _position = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f, FontStyle.Bold), Anchor = AnchorStyles.None, Margin = new Padding(4, 0, 4, 0) };
    private readonly Panel _columns = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly TableLayoutPanel _columnRows = NewStack();
    private readonly Button _select = new() { Text = "ファイルを選択", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardBorder, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f), Padding = new Padding(12, 7, 12, 7), Cursor = Cursors.Hand, Anchor = AnchorStyles.None };
    private readonly Button _merge = new() { Text = "マージ", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(10f, FontStyle.Bold), Padding = new Padding(18, 8, 18, 8), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = new Padding(10, 0, 0, 0) };
    private readonly Button _first = NavButton(Icons.First, "先頭");
    private readonly Button _previous = NavButton(Icons.Prev, "戻る");
    private readonly Button _next = NavButton(Icons.Next, "次へ", trailing: true);
    private readonly Button _last = NavButton(Icons.Last, "最後", trailing: true);

    public ImportForm(ImportViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Text = "インポート (CSV)";
        // Same window basics as the data-items dialog (ProjectDesignForm): resizable + maximizable,
        // opened at the golden-ratio 800 × 500 DIP and never smaller than 560 × 320 DIP.
        MaximizeBox = true;
        ClientSize = new Size(LogicalToDeviceUnits(800), LogicalToDeviceUnits(500));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(320));
        Font = Theme.Font();
        BackColor = Theme.ContentBack;

        BuildLayout();
        RefreshScalars();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Merged += OnMerged;
        WireCommand(_vm.FirstCommand, _first);
        WireCommand(_vm.PreviousCommand, _previous);
        WireCommand(_vm.NextCommand, _next);
        WireCommand(_vm.LastCommand, _last);
        WireCommand(_vm.MergeCommand, _merge);
        _merge.FlatAppearance.BorderSize = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Merged -= OnMerged;
        }
        base.Dispose(disposing);
    }

    // After a successful merge, run the import-time sentiment/topic analysis with a progress dialog and
    // re-project the star — the same step the CSV-create flow runs. Skipped silently when no API key is
    // configured or the project has no 自由記述 columns (the merge already inserted the rows and rebuilt
    // the star, so there is nothing more to do).
    private void OnMerged(Project project)
    {
        var settings = new SettingsViewModel(AppServices.Settings);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || !ImportAnalyzer.HasAnalyzableFields(project))
            return;

        var analyzer = new ImportAnalyzer(AppServices.Llm, AppServices.Responses, AppServices.Topics, AppServices.AnalysisResults, settings.SentimentModel);
        using var dialog = new AnalyzeProgressForm((progress, ct) => analyzer.AnalyzeAsync(project, progress, ct));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AppServices.Analytics.Rebuild(project);
    }

    private void BuildLayout()
    {
        var intro = new Label { Text = "データ化したアンケートの回答をCSV形式で読み込み、プロジェクトにマージできます。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 14) };

        // File card: file name on the left, ファイルを選択 / マージ buttons on the right.
        var fileCard = SoftCard();
        var fileInner = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14), BackColor = Color.White };
        fileInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var fileText = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left };
        fileText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSection(fileText, new Label { Text = "CSVファイル", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = Padding.Empty });
        AddSection(fileText, _selectedFile);

        _select.FlatAppearance.BorderSize = 0;
        _select.Click += (_, _) => PickAndLoad();
        var fileButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right };
        fileButtons.Controls.Add(_select);
        fileButtons.Controls.Add(_merge);

        fileInner.Controls.Add(fileText, 0, 0);
        fileInner.Controls.Add(fileButtons, 1, 0);
        fileCard.Controls.Add(fileInner);

        // Preview card; it fills the remaining space (set in the assembly below) and its column list
        // scrolls inside.
        var preview = SoftCard();
        preview.AutoSize = false;
        var pv = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };

        // The column list scrolls inside the filling preview; the rows live in a Dock=Top stack so their
        // width tracks the panel's client area (shrinking when the vertical scrollbar appears, which
        // avoids a spurious horizontal scrollbar).
        _columnRows.BackColor = Color.White;
        _columns.Controls.Add(_columnRows);

        // Header: the title on its own row, then the record pager on a full-width row below it, right-
        // aligned. (Sharing one row with the title squeezed the pager so the last button overflowed the
        // dialog edge; its own full-width row fits all five controls down to the minimum dialog width.)
        var headRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White };
        headRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headRow.Controls.Add(new Label { Text = "プレビュー", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Anchor = AnchorStyles.Left }, 0, 0);
        var nav = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0) };
        nav.Controls.AddRange(new Control[] { _first, _previous, _position, _next, _last });
        headRow.Controls.Add(nav, 0, 1);

        // Column header sharing the rows' three-column proportions, then a divider.
        var colHeader = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 6, 0, 0) };
        colHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, NamePct));
        colHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, MappingPct));
        colHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ValuePct));
        colHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        colHeader.Controls.Add(HeaderCaption("CSVの列"), 0, 0);
        colHeader.Controls.Add(HeaderCaption("取り込み先（項目）"), 1, 0);
        colHeader.Controls.Add(HeaderCaption("値"), 2, 0);
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.CardBorder };

        var hint = new Label { Text = "すべての列の取り込み先を選ぶと「マージ」が有効になります（取り込まない列は「（取り込まない）」を選択）。", Dock = DockStyle.Bottom, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Padding = new Padding(0, 8, 0, 0) };

        // Docking add order: Fill first (back), then Bottom, then the Top rows in reverse visual order
        // so headRow lands at the very top.
        pv.Controls.Add(_columns);   // Fill
        pv.Controls.Add(hint);       // Bottom
        pv.Controls.Add(divider);    // Top (lowest)
        pv.Controls.Add(colHeader);  // Top
        pv.Controls.Add(headRow);    // Top (highest)
        preview.Controls.Add(pv);

        // Fixed top area (intro, file card, status) docked at the top; the preview fills the rest so
        // resizing / maximizing the dialog enlarges the preview, mirroring the data-items dialog.
        var topArea = NewStack();
        topArea.Dock = DockStyle.Top;
        topArea.Padding = new Padding(28, 28, 28, 0);
        AddSection(topArea, intro);
        AddSection(topArea, fileCard);
        AddSection(topArea, _status);

        // The preview card fills the remaining space; a host panel gives it the 28 DIP outer margin.
        preview.Dock = DockStyle.Fill;
        var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ContentBack, Padding = new Padding(28, 8, 28, 28) };
        previewHost.Controls.Add(preview);

        Controls.Add(previewHost);  // Fill — add first so it yields its edges to the docked top area
        Controls.Add(topArea);      // Top
    }

    // ===== File pick =====

    private async void PickAndLoad()
    {
        using var picker = new OpenFileDialog
        {
            Title = "ファイルを選択",
            // The delimiter / encoding are auto-detected on read, so the two entries differ only in which
            // extensions they list — pick whichever matches the file at hand.
            Filter = "カンマ区切りテキストファイル (*.csv;*.txt)|*.csv;*.txt|タブ区切りテキストファイル (*.tsv;*.txt)|*.tsv;*.txt",
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        var fileName = Path.GetFileName(picker.FileName);

        // Parse on a background thread (LoadCsvAsync) so the dialog stays responsive; disable the picker
        // and show a busy cursor so a large file can't be re-picked mid-load. The preview table is then
        // rebuilt once — not once per column as it loads — so loading no longer stutters.
        _select.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var bytes = File.ReadAllBytes(picker.FileName);
            await _vm.LoadCsvAsync(bytes, fileName);   // exact-name auto-mapping happens inside
            RebuildColumns();
            await RefineMappingWithLlm();               // LLM-map any columns left blank
        }
        catch (CsvFormatException ex)
        {
            // The file couldn't be interpreted (encoding / column count / empty) — show the cause.
            _status.Text = ex.Message;
            _status.Visible = true;
            MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, $"ファイルを読み込めませんでした。\n{ex.Message}", "読み込みエラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _select.Enabled = true;
        }
    }

    // After loading, ask the LLM to map any columns the exact-name pass left blank, using their sample
    // values and the project's fields. Skipped silently with no API key; any failure leaves those columns
    // blank for the user. Auto-mapped columns are never overwritten, and the 1:1 / 選択肢 rule is applied
    // by the view model when the suggestions are merged in.
    private async Task RefineMappingWithLlm()
    {
        var settings = new SettingsViewModel(AppServices.Settings);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return;
        var unmapped = _vm.Columns.Where(c => c.SelectedMapping is null).Select(c => c.Name).ToList();
        if (unmapped.Count == 0)
            return;
        try
        {
            var columns = unmapped.Select(name => (Column: name, Samples: _vm.SampleValuesFor(name))).ToList();
            var mapper = new LlmColumnMapper(AppServices.Llm, settings.TopicModel);
            var suggestions = await mapper.MapAsync(columns, _vm.TargetFields);
            _vm.ApplyMappingSuggestions(suggestions);
            RebuildColumns();
        }
        catch
        {
            // Keep the name-based mapping on any failure (offline, bad key, unparseable reply).
        }
    }

    // ===== Reactive =====

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshScalars();

    private void RefreshScalars()
    {
        _selectedFile.Text = _vm.SelectedFile;
        _position.Text = _vm.RowPosition;
        _status.Text = _vm.StatusMessage;
        _status.Visible = !string.IsNullOrEmpty(_vm.StatusMessage);
    }

    // Rebuilds one row per CSV column. Each row shows the column name, a mapping dropdown bound to the
    // column's SelectedMapping, and the current record's value (which updates as the row is navigated).
    private void RebuildColumns()
    {
        _columnRows.SuspendLayout();
        foreach (Control old in _columnRows.Controls)
            old.Dispose();
        _columnRows.Controls.Clear();
        _columnRows.RowStyles.Clear();
        _columnRows.RowCount = 0;
        foreach (var column in _vm.Columns)
            AddSection(_columnRows, new ImportColumnRow(column, _vm.MappingOptions));
        _columnRows.ResumeLayout();
        RefreshScalars();
    }

    // ===== Helpers =====

    private void WireCommand(System.Windows.Input.ICommand command, Button button)
    {
        button.Click += (_, _) => command.Execute(null);
        command.CanExecuteChanged += (_, _) => button.Enabled = command.CanExecute(null);
        button.Enabled = command.CanExecute(null);
    }

    private static Button NavButton((string Font, string Glyph) icon, string text, bool trailing = false) => new IconButton
    {
        Glyph = icon.Glyph,
        IconFontName = icon.Font,
        Text = text,
        GlyphTrailing = trailing,
        IconSize = 8f,
        IconGap = 3,
        AutoSize = true,
        BackColor = Theme.CardBorder,
        ForeColor = Theme.TitleText,
        Font = Theme.Font(9f),
        Padding = new Padding(5, 4, 5, 4),
        Margin = new Padding(2, 0, 2, 0),
        Cursor = Cursors.Hand,
        Anchor = AnchorStyles.None,
    };

    private static Label HeaderCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.Font(8.5f),
        Anchor = AnchorStyles.Left,
    };

    // A 1-column AutoSize stack whose children flow downward; each child anchors to fill the width.
    // Docks to the top by default (the caller scrolls via an outer AutoScroll panel); the preview's
    // column list overrides this to Dock=Fill + its own AutoScroll.
    private static TableLayoutPanel NewStack()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            BackColor = Theme.ContentBack,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    // Appends a section/row as a new AutoSize row, stretched to the column width (unless it set a
    // left-only Anchor, e.g. captions).
    private static void AddSection(TableLayoutPanel stack, Control section)
    {
        if (section.Anchor == (AnchorStyles.Top | AnchorStyles.Left))  // default → stretch horizontally
            section.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(section, 0, stack.RowCount);
        stack.RowCount++;
    }

    // A white panel with the soft #E2E8F0 border (drawn, not FixedSingle), sized to its content.
    private static BorderedPanel SoftCard() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        BackColor = Color.White,
    };

    // A panel that draws the soft card border itself (ResizeRedraw keeps it crisp as the card reflows).
    internal sealed class BorderedPanel : Panel
    {
        public BorderedPanel() => ResizeRedraw = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.CardBorder);
            var rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            e.Graphics.DrawRectangle(pen, rect);
        }
    }
}

// One column row in the import preview: the CSV column name, a mapping dropdown (project fields plus
// 取り込まない), and the current record's value. Writes the chosen mapping back to the column and
// refreshes the value label whenever the navigated record changes. The three columns share the same
// proportions as the preview's column header so everything lines up; no fixed coordinates.
internal sealed class ImportColumnRow : Panel
{
    private readonly ImportViewModel.ImportColumn _column;
    // Fills its cell vertically (Top|Bottom) as well as horizontally so the real-DPI font is not clipped
    // in the otherwise-unscaled row; the combo in the same row sets the height, the text centres in it.
    private readonly Label _value = new() { AutoSize = false, AutoEllipsis = true, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom, TextAlign = ContentAlignment.MiddleLeft };
    // Scroll-safe so spinning the wheel over the closed drop-down scrolls the preview list rather than
    // silently changing the chosen mapping.
    private readonly ScrollSafeComboBox _mapping = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 8, 0) };

    public ImportColumnRow(ImportViewModel.ImportColumn column, ObservableCollection<string> options)
    {
        _column = column;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Color.White;

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 4), BackColor = Color.White };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label { Text = column.Name, AutoSize = false, AutoEllipsis = true, ForeColor = Theme.BarTrackText, Font = Theme.Font(9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom, Margin = new Padding(0, 0, 8, 0) }, 0, 0);

        foreach (var option in options)
            _mapping.Items.Add(option);
        if (column.SelectedMapping is { } current)
            _mapping.SelectedItem = current;
        _mapping.SelectedIndexChanged += (_, _) => _column.SelectedMapping = _mapping.SelectedItem as string;
        grid.Controls.Add(_mapping, 1, 0);

        _value.Text = column.CurrentValue;
        grid.Controls.Add(_value, 2, 0);

        Controls.Add(grid);
        _column.PropertyChanged += OnColumnChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _column.PropertyChanged -= OnColumnChanged;
        base.Dispose(disposing);
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportViewModel.ImportColumn.CurrentValue))
            _value.Text = _column.CurrentValue;
    }
}
