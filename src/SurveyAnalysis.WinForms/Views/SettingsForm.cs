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

    public SettingsForm(SettingsViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Text = "設定";
        MaximizeBox = false;  // dialogs are not maximizable
        ClientSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font();
        BackColor = Theme.ContentBack;
        MinimumSize = new Size(560, 480);

        // Header with the reset action floated to the right (a spacer column pushes it over).
        var header = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Padding = new Padding(0, 8, 16, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var reset = new Button { Text = "デフォルトに戻す", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.ContentBack, ForeColor = Theme.Muted, Font = Theme.Font(9f), Cursor = Cursors.Hand, Anchor = AnchorStyles.Right, Padding = new Padding(10, 5, 10, 5) };
        reset.FlatAppearance.BorderColor = Theme.CardBorder;
        reset.FlatAppearance.BorderSize = 1;
        reset.Click += (_, _) => _vm.ResetToDefaultsCommand.Execute(null);
        header.Controls.Add(reset, 1, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Font(10f) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildMailTab());
        tabs.TabPages.Add(BuildLlmTab());

        Controls.Add(tabs);
        Controls.Add(header);
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
        AddRow(grid, "差出人", Bound(new TextBox(), nameof(_vm.MailFrom)));
        AddRow(grid, "宛先", Bound(new TextBox(), nameof(_vm.MailTo)));

        var serverType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var option in _vm.MailServerOptions)
            serverType.Items.Add(option);
        BindCombo(serverType, () => _vm.MailServerType, v => _vm.MailServerType = v, nameof(_vm.MailServerType));
        AddRow(grid, "メールサーバー種別", serverType);

        // Gmail section (visible when 種別 = Gmail).
        var gmail = SubSection(
            ("Gmail アドレス", Bound(new TextBox(), nameof(_vm.GmailAddress))),
            ("アプリパスワード", Bound(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.GmailAppPassword))));
        gmail.DataBindings.Add("Visible", _vm, nameof(_vm.IsGmail));
        AddRow(grid, "", gmail);

        // SMTP section (visible when 種別 = SMTP).
        var smtp = SubSection(
            ("ホスト", Bound(new TextBox(), nameof(_vm.SmtpHost))),
            ("ポート", Bound(new TextBox(), nameof(_vm.SmtpPort))),
            ("ユーザー名", Bound(new TextBox(), nameof(_vm.SmtpUsername))),
            ("パスワード", Bound(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.SmtpPassword))));
        var tls = BoundCheck(new CheckBox { Text = "TLS を使う", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) }, nameof(_vm.SmtpUseTls));
        AddStacked(smtp, tls);
        smtp.DataBindings.Add("Visible", _vm, nameof(_vm.IsSmtp));
        AddRow(grid, "", smtp);

        page.Controls.Add(grid);
        return page;
    }

    // ===== LLM =====

    private TabPage BuildLlmTab()
    {
        var page = NewPage("LLM");
        var grid = NewGrid();
        AddRow(grid, "API キー", Bound(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.ApiKey)));
        AddRow(grid, "エンドポイント", Bound(new TextBox(), nameof(_vm.Endpoint)));
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
        var label = new Label { Text = caption, AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 8, 8) };
        // Text inputs (and the SMTP/Gmail sub-panels) stretch to fill the input column; the short-value
        // combos keep their own width and stay left-aligned.
        if (input is TextBox)
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(0, 6, 0, 6);
        grid.Controls.Add(label);
        grid.Controls.Add(input);
    }

    // A bordered sub-panel stacking a few labeled inputs (for the Gmail / SMTP groups). A 1-column
    // TableLayoutPanel so the inputs stretch to the panel width via Anchor (and the panel itself fills
    // the grid's input column).
    private TableLayoutPanel SubSection(params (string Caption, Control Input)[] rows)
    {
        // AutoSize panel sized to its inputs' width (a fixed, DPI-scaled-at-construction width — an
        // AutoSize panel in a Percent column does NOT stretch via Anchor, so a fill would collapse it).
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = ColorTranslator.FromHtml("#F8FAFC"),
            Padding = new Padding(10),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (caption, input) in rows)
        {
            input.Width = 300;
            input.Anchor = AnchorStyles.Left;
            input.Margin = new Padding(0, 0, 0, 6);
            AddStacked(panel, new Label { Text = caption, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
            AddStacked(panel, input);
        }
        return panel;
    }

    // Appends a control as a new AutoSize row in a single-column stack.
    private static void AddStacked(TableLayoutPanel panel, Control c)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(c, 0, panel.RowCount);
        panel.RowCount++;
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

    private CheckBox BoundCheck(CheckBox box, string property)
    {
        box.Font = Theme.Font(9.5f);
        box.DataBindings.Add("Checked", _vm, property, false, DataSourceUpdateMode.OnPropertyChanged);
        return box;
    }
}
