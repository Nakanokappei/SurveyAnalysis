using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The settings dialog — the WinForms counterpart of SettingsWindow.axaml. Three tabs (全般 / メール /
// LLM) over the existing SettingsViewModel, with "デフォルトに戻す" floated top-right. Inputs use
// two-way data binding to the view model (INotifyPropertyChanged), so edits flow back and the reset
// command refreshes every field automatically. The host calls Save() after the dialog closes.
internal sealed class SettingsForm : Form
{
    private readonly SettingsViewModel _vm;
    private readonly Font _tabFont = Theme.Font(10f);
    private readonly Font _tabFontBold = Theme.Font(10f, FontStyle.Bold);
    private readonly TabControl _tabs;
    private readonly TableLayoutPanel _header;

    public SettingsForm(SettingsViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Text = "設定";
        // A fixed-size dialog: not resizable, no maximize / minimize.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 660);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font();
        BackColor = Theme.ContentBack;
        // 8 DIP gap between the window edges and the content (header / tab control).
        Padding = new Padding(LogicalToDeviceUnits(8));

        // Header with the reset action floated to the right (a spacer column pushes it over).
        _header = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Padding = new Padding(0, 0, 0, 6) };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var reset = new IconButton { Glyph = Icons.Reset.Glyph, IconFontName = Icons.Reset.Font, Text = "デフォルトに戻す", AutoSize = true, BackColor = Theme.ContentBack, ForeColor = Theme.Muted, Font = Theme.Font(9f), Cursor = Cursors.Hand, Anchor = AnchorStyles.Right, Padding = new Padding(10, 5, 10, 5) };
        reset.FlatAppearance.BorderColor = Theme.CardBorder;
        reset.FlatAppearance.BorderSize = 1;
        reset.Click += (_, _) => _vm.ResetToDefaultsCommand.Execute(null);
        _header.Controls.Add(reset, 1, 0);

        // Owner-drawn tabs so the selected tab can be bold + accent-coloured (the default control gives
        // no per-tab font), making the current tab obvious.
        _tabs = new TabControl { Dock = DockStyle.Fill, Font = _tabFont, DrawMode = TabDrawMode.OwnerDrawFixed };
        _tabs.DrawItem += DrawTab;
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildMailTab());
        _tabs.TabPages.Add(BuildLlmTab());

        Controls.Add(_tabs);
        Controls.Add(_header);
    }

    // Size the dialog to the tallest tab (メール → SMTP, 8 rows) plus a 20% margin, with the width
    // derived from that height by the golden ratio. Done once on load — the form renders at
    // AutoScaleFactor 1 (the app-wide DPI behaviour), so the row pitch is measured rather than assumed.
    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);

        // Per-row pitch from the LLM tab's grid (6 always-visible rows, no show/hide).
        var perRow = _tabs.TabPages[2].Controls[0].PreferredSize.Height / 6.0;
        const int tallestRows = 8; // メール(SMTP): 差出人 宛先 種別 ホスト ポート ユーザー名 パスワード TLS
        var tabStrip = _tabs.DisplayRectangle.Top;            // tab strip + top border
        var pagePadding = _tabs.TabPages[0].Padding.Vertical; // the white inner padding of a page
        var minHeight = (int)System.Math.Round(tallestRows * perRow)
            + pagePadding + tabStrip + _header.PreferredSize.Height + Padding.Vertical;

        var height = (int)System.Math.Round(minHeight * 1.2);   // +20% margin
        var width = (int)System.Math.Round(height * 1.618);     // golden ratio
        ClientSize = new Size(width, height);
    }

    // ===== 全般 =====

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("全般");
        var grid = NewGrid();
        AddRow(grid, "会社名", Bound(new TextBox(), nameof(_vm.CompanyName)));
        AddRow(grid, "画像の読み取りフォルダ", Bound(new TextBox(), nameof(_vm.ScanFolderPath)));
        AddRow(grid, "", BoundCheck(new CheckBox { Text = "読み取り後にアーカイブへ移動して二重読み取りを防ぐ", AutoSize = true }, nameof(_vm.ArchiveAfterScan)));
        AddRow(grid, "アーカイブ先サブフォルダ名", Bound(new TextBox(), nameof(_vm.ArchiveSubfolderName)));
        page.Controls.Add(grid);
        return page;
    }

    // ===== メール =====

    private TabPage BuildMailTab()
    {
        var page = NewPage("メール");
        var grid = NewGrid();
        AddRow(grid, "差出人", BoundAscii(new TextBox(), nameof(_vm.MailFrom)));
        AddRow(grid, "宛先", BoundAscii(new TextBox(), nameof(_vm.MailTo)));

        var serverType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var option in _vm.MailServerOptions)
            serverType.Items.Add(option);
        BindCombo(serverType, () => _vm.MailServerType, v => _vm.MailServerType = v, nameof(_vm.MailServerType));
        AddRow(grid, "メールサーバー種別", serverType);

        // Gmail rows (shown when 種別 = Gmail) — same row style as the rest, not a boxed sub-panel.
        AddToggleRow(grid, "Gmail アドレス", BoundAscii(new TextBox(), nameof(_vm.GmailAddress)), nameof(_vm.IsGmail));
        AddToggleRow(grid, "アプリパスワード", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.GmailAppPassword)), nameof(_vm.IsGmail));

        // SMTP rows (shown when 種別 = SMTP).
        AddToggleRow(grid, "ホスト", BoundAscii(new TextBox(), nameof(_vm.SmtpHost)), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "ポート", BoundNumber(nameof(_vm.SmtpPort), 5), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "ユーザー名", BoundAscii(new TextBox(), nameof(_vm.SmtpUsername)), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "パスワード", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.SmtpPassword)), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "", BoundCheck(new CheckBox { Text = "TLS を使う", AutoSize = true }, nameof(_vm.SmtpUseTls)), nameof(_vm.IsSmtp));

        page.Controls.Add(grid);
        return page;
    }

    // ===== LLM =====

    private TabPage BuildLlmTab()
    {
        var page = NewPage("LLM");
        var grid = NewGrid();
        AddRow(grid, "API キー", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.ApiKey)));
        AddRow(grid, "エンドポイント", BoundAscii(new TextBox(), nameof(_vm.Endpoint)));
        AddRow(grid, "OCR モデル", ModelCombo(() => _vm.OcrModel, v => _vm.OcrModel = v, nameof(_vm.OcrModel)));
        AddRow(grid, "トピック モデル", ModelCombo(() => _vm.TopicModel, v => _vm.TopicModel = v, nameof(_vm.TopicModel)));
        AddRow(grid, "感情 モデル", ModelCombo(() => _vm.SentimentModel, v => _vm.SentimentModel = v, nameof(_vm.SentimentModel)));
        AddRow(grid, "レポート モデル", ModelCombo(() => _vm.ReportModel, v => _vm.ReportModel = v, nameof(_vm.ReportModel)));
        page.Controls.Add(grid);
        return page;
    }

    // ===== Helpers =====

    private static TabPage NewPage(string title) => new(title) { BackColor = Color.White, Padding = new Padding(18) };

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = Color.White };
        // AutoSize label column (not Absolute, which would not scale with DPI) so captions always fit.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    // Adds a right-aligned caption + the input control as one row. The controls are appended in order
    // and flow into the two-column grid (label, input) — letting the TableLayoutPanel assign cells, so
    // the rows stay correctly ordered.
    private static void AddRow(TableLayoutPanel grid, string caption, Control input)
    {
        grid.Controls.Add(MakeLabel(caption));
        grid.Controls.Add(StyleInput(input));
    }

    // Like AddRow, but the label + input show only while `visibleProperty` is true — the AutoSize row
    // collapses when both are hidden, so the Gmail / SMTP groups appear and disappear with the 種別
    // while looking identical to the always-visible rows.
    private void AddToggleRow(TableLayoutPanel grid, string caption, Control input, string visibleProperty)
    {
        var label = MakeLabel(caption);
        StyleInput(input);
        label.DataBindings.Add("Visible", _vm, visibleProperty);
        input.DataBindings.Add("Visible", _vm, visibleProperty);
        grid.Controls.Add(label);
        grid.Controls.Add(input);
    }

    // A right-aligned caption for the input beside it.
    private static Label MakeLabel(string caption) =>
        new() { Text = caption, AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 8, 8) };

    // Text inputs stretch to fill the input column; short-value combos keep their own width.
    private static Control StyleInput(Control input)
    {
        if (input is TextBox)
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(0, 6, 0, 6);
        return input;
    }

    // Draws a tab: the selected one bold + accent-coloured on a white (page-matching) background, the
    // rest regular + muted on the panel background — so the current tab reads clearly.
    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tabs = (TabControl)sender!;
        var selected = e.Index == tabs.SelectedIndex;
        using var back = new SolidBrush(selected ? Color.White : Theme.ContentBack);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics, tabs.TabPages[e.Index].Text,
            selected ? _tabFontBold : _tabFont, e.Bounds,
            selected ? Theme.Accent : Theme.BodyText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private ComboBox ModelCombo(System.Func<string> get, System.Action<string> set, string property)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var model in _vm.ModelOptions)
            combo.Items.Add(model);
        BindCombo(combo, get, set, property);
        return combo;
    }

    // Two-way binds a DropDownList combo to a string property. WinForms has no SelectedItemChanged
    // event for the DataBindings layer to push user picks through, so the wiring is explicit: the pick
    // writes the property, and the property's change writes the selection back (e.g. on reset).
    private void BindCombo(ComboBox combo, System.Func<string> get, System.Action<string> set, string property)
    {
        var syncing = false;
        combo.SelectedItem = get();
        combo.SelectedIndexChanged += (_, _) => { if (!syncing && combo.SelectedItem is string value) set(value); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property)
            {
                syncing = true;
                combo.SelectedItem = get();
                syncing = false;
            }
        };
    }

    private TextBox Bound(TextBox box, string property)
    {
        box.Font = Theme.Font(9.5f);
        box.DataBindings.Add("Text", _vm, property, false, DataSourceUpdateMode.OnPropertyChanged);
        return box;
    }

    // Like Bound, but with the IME disabled — for fields that never take Japanese (e-mail, host, port,
    // API key, endpoint, ...). Fields that may take Japanese (会社名, folder, archive name) use Bound.
    private TextBox BoundAscii(TextBox box, string property)
    {
        box.ImeMode = ImeMode.Disable;
        return Bound(box, property);
    }

    // A fixed-width numeric field (IME off), sized to (maxDigits + 2) characters and left-aligned —
    // wrapped in an auto-size holder so the row styling does not stretch it to the whole column.
    private Control BoundNumber(string property, int maxDigits)
    {
        var box = BoundAscii(new TextBox(), property);
        // Measure at 96 dpi (a default Bitmap) — this form renders unscaled, so that matches on screen.
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        box.Width = (int)System.Math.Ceiling(graphics.MeasureString(new string('0', maxDigits + 2), box.Font).Width) + 8;
        var holder = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        holder.Controls.Add(box);
        return holder;
    }

    private CheckBox BoundCheck(CheckBox box, string property)
    {
        box.Font = Theme.Font(9.5f);
        box.DataBindings.Add("Checked", _vm, property, false, DataSourceUpdateMode.OnPropertyChanged);
        return box;
    }
}
