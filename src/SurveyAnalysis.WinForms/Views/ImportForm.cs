using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The CSV import dialog — the WinForms counterpart of ImportView.axaml. Picks a CSV, previews it one
// record at a time (単票) with row navigation, maps each CSV column to a project field, and merges.
// Binds to the existing ImportViewModel: navigation/merge run its commands, the status line and row
// position follow its properties, and the per-column rows rebuild when a file is loaded.
internal sealed class ImportForm : Form
{
    private readonly ImportViewModel _vm;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.ContentBack, Padding = new Padding(28) };
    private readonly FlowLayoutPanel _stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack };
    private readonly Label _selectedFile = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(10f) };
    private readonly Label _status = new() { AutoSize = false, Height = 22, ForeColor = ColorTranslator.FromHtml("#B45309"), Font = Theme.Font(9.5f) };
    private readonly Label _position = new() { AutoSize = false, Width = 64, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f, FontStyle.Bold) };
    private readonly FlowLayoutPanel _columns = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Button _merge = new() { Text = "マージ", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(10f, FontStyle.Bold), Padding = new Padding(18, 8, 18, 8), Cursor = Cursors.Hand };
    private readonly Button _first = NavButton("◀◀ 先頭");
    private readonly Button _previous = NavButton("◀ 戻る");
    private readonly Button _next = NavButton("次へ ▶");
    private readonly Button _last = NavButton("最後 ▶▶");

    public ImportForm(ImportViewModel vm)
    {
        // Scale the whole layout by the monitor DPI (the 96-dpi design values grow with the font),
        // so nothing clips when Windows runs at >100% scaling. Set before any child is added.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Text = "インポート (CSV)";
        MaximizeBox = false;  // dialogs are not maximizable
        ClientSize = new Size(880, 620);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 460);
        Font = Theme.Font();
        BackColor = Theme.ContentBack;

        BuildLayout();
        RefreshScalars();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Columns.CollectionChanged += OnColumnsChanged;
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
            _vm.Columns.CollectionChanged -= OnColumnsChanged;
        }
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        var intro = new Label { Text = "デジタルに回収したアンケートのCSVを取り込み、既存データにマージします。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 14) };

        // File card: file name + select + merge.
        var fileCard = Card(72);
        var fileCaption = new Label { Text = "CSVファイル", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(14, 12) };
        _selectedFile.Location = new Point(14, 32);
        var select = new Button { Text = "ファイルを選択", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardBorder, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f), Padding = new Padding(12, 7, 12, 7), Cursor = Cursors.Hand };
        select.FlatAppearance.BorderSize = 0;
        select.Click += (_, _) => PickAndLoad();
        fileCard.Controls.AddRange(new Control[] { fileCaption, _selectedFile, select, _merge });
        fileCard.Resize += (_, _) =>
        {
            _merge.Location = new Point(fileCard.Width - _merge.Width - 14, (fileCard.Height - _merge.Height) / 2);
            select.Location = new Point(_merge.Left - select.Width - 10, (fileCard.Height - select.Height) / 2);
        };

        _status.Margin = new Padding(0, 0, 0, 8);

        // Preview card. Sections are docked (not laid out with TableLayoutPanel Absolute rows, whose
        // heights do NOT scale with DPI): docked panel heights are control bounds, so they scale with
        // the font under AutoScaleMode.Dpi and the headings never clip.
        var preview = Card(0);
        preview.Height = 360;
        var pv = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        var headRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.White };
        var title = new Label { Text = "取り込みプレビュー（単票）", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(12f, FontStyle.Bold), Location = new Point(0, 8) };
        var nav = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Location = new Point(260, 4) };
        nav.Controls.AddRange(new Control[] { _first, _previous, _position, _next, _last });
        headRow.Controls.Add(title);
        headRow.Controls.Add(nav);
        headRow.Resize += (_, _) => nav.Location = new Point(System.Math.Max(0, headRow.Width - nav.Width), 4);

        var colHeader = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.White };
        colHeader.Controls.Add(new Label { Text = "CSVの列", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(0, 2) });
        colHeader.Controls.Add(new Label { Text = "取り込み先（項目）", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(156, 2) });
        colHeader.Controls.Add(new Label { Text = "値", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(372, 2) });
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.CardBorder };

        var hint = new Label { Text = "すべての列の取り込み先を選ぶと「マージ」が有効になります（取り込まない列は「（取り込まない）」を選択）。", Dock = DockStyle.Bottom, Height = 40, ForeColor = Theme.Muted, Font = Theme.Font(8.5f) };

        // Add order matters for docking: Fill first (back), then Bottom, then the Top rows in reverse
        // visual order so headRow ends up at the very top.
        pv.Controls.Add(_columns);
        pv.Controls.Add(hint);
        pv.Controls.Add(divider);
        pv.Controls.Add(colHeader);
        pv.Controls.Add(headRow);
        preview.Controls.Add(pv);

        _stack.Controls.Add(intro);
        _stack.Controls.Add(fileCard);
        _stack.Controls.Add(_status);
        _stack.Controls.Add(preview);
        _content.Controls.Add(_stack);
        Controls.Add(_content);

        _content.Resize += (_, _) => SyncWidths();
        _columns.Resize += (_, _) => ResizeColumnRows();
        _fileCard = fileCard; _preview = preview; _intro = intro;
        SyncWidths();
    }

    private Panel _fileCard = null!, _preview = null!;
    private Label _intro = null!;

    private void SyncWidths()
    {
        var width = _content.ClientSize.Width - _content.Padding.Horizontal;
        if (_content.VerticalScroll.Visible)
            width -= SystemInformation.VerticalScrollBarWidth;
        width = Math.Max(420, width);

        foreach (Control c in new Control[] { _intro, _fileCard, _status, _preview })
            c.Width = width;
        ResizeColumnRows();
    }

    // Stretches each column row to the scrolling area's width.
    private void ResizeColumnRows()
    {
        var inner = Math.Max(360, _columns.ClientSize.Width);
        foreach (Control row in _columns.Controls)
            row.Width = inner;
    }

    // ===== File pick =====

    private void PickAndLoad()
    {
        using var picker = new OpenFileDialog
        {
            Title = "CSV / TSV ファイルを選択",
            Filter = "CSV / TSV / テキスト (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|すべてのファイル (*.*)|*.*",
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            _vm.LoadCsv(File.ReadAllBytes(picker.FileName), Path.GetFileName(picker.FileName));
    }

    // ===== Reactive =====

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshScalars();
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();

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
        _columns.SuspendLayout();
        foreach (Control old in _columns.Controls)
            old.Dispose();
        _columns.Controls.Clear();
        var inner = Math.Max(360, _columns.ClientSize.Width);
        foreach (var column in _vm.Columns)
            _columns.Controls.Add(new ImportColumnRow(column, _vm.MappingOptions) { Width = inner });
        _columns.ResumeLayout();
        ResizeColumnRows();
        RefreshScalars();
    }

    // ===== Helpers =====

    private void WireCommand(System.Windows.Input.ICommand command, Button button)
    {
        button.Click += (_, _) => command.Execute(null);
        command.CanExecuteChanged += (_, _) => button.Enabled = command.CanExecute(null);
        button.Enabled = command.CanExecute(null);
    }

    private static Button NavButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Theme.CardBorder,
        ForeColor = Theme.TitleText,
        Font = Theme.Font(9f),
        Padding = new Padding(8, 5, 8, 5),
        Margin = new Padding(3, 0, 3, 0),
        Cursor = Cursors.Hand,
    };

    private static Panel Card(int height)
    {
        var card = new Panel { BackColor = Color.White, Padding = new Padding(20), BorderStyle = BorderStyle.FixedSingle };
        if (height > 0)
            card.Height = height;
        return card;
    }
}

// One column row in the import preview: the CSV column name, a mapping dropdown (project fields plus
// 取り込まない), and the current record's value. Writes the chosen mapping back to the column and
// refreshes the value label whenever the navigated record changes.
internal sealed class ImportColumnRow : Panel
{
    private readonly ImportViewModel.ImportColumn _column;
    private readonly Label _value = new() { AutoSize = false, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f), Location = new Point(372, 6) };
    private readonly ComboBox _mapping = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Location = new Point(156, 4), Font = Theme.Font(9.5f) };

    public ImportColumnRow(ImportViewModel.ImportColumn column, System.Collections.ObjectModel.ObservableCollection<string> options)
    {
        _column = column;
        Height = 32;
        BackColor = Color.White;

        Controls.Add(new Label { Text = column.Name, AutoSize = false, Size = new Size(150, 24), Location = new Point(0, 6), ForeColor = Theme.BarTrackText, Font = Theme.Font(9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
        foreach (var option in options)
            _mapping.Items.Add(option);
        if (column.SelectedMapping is { } current)
            _mapping.SelectedItem = current;
        _mapping.SelectedIndexChanged += (_, _) => _column.SelectedMapping = _mapping.SelectedItem as string;
        Controls.Add(_mapping);

        _value.Text = column.CurrentValue;
        Controls.Add(_value);

        _column.PropertyChanged += OnColumnChanged;
        Resize += (_, _) => _value.SetBounds(372, 6, Math.Max(40, Width - 372), 20);
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
