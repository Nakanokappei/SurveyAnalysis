using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using SurveyAnalysis.Llm;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The settings dialog — three tabs (全般 / メール / LLM) over SettingsViewModel, with "デフォルトに戻す"
// floated top-right. Inputs two-way bind to the view model (INotifyPropertyChanged), so edits flow
// back and reset refreshes every field. The host calls Save() after the dialog closes. The LLM tab is
// organised into three groups — 共通 (the shared endpoint / key / concurrency / timeout used by both
// chat and embeddings), LLM (the per-task chat models), and 埋め込み (embedding model / batch).
internal sealed class SettingsForm : Form
{
    private readonly SettingsViewModel _vm;
    private readonly Font _tabFont = Theme.Font(10f);
    private readonly Font _tabFontBold = Theme.Font(10f, FontStyle.Bold);
    private readonly TabControl _tabs;
    private readonly TableLayoutPanel _header;

    // Connection-check badge for the (single, shared) API key: a free GET /models call, debounced,
    // shows a green ✔ when valid or a red ✗ when invalid / unreachable.
    private readonly Label _keyStatus = new();
    private readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 700 };
    private System.Threading.CancellationTokenSource? _verifyCts;
    private readonly HttpClient _verifyHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ToolTip _statusTip = new();
    private static readonly Color StatusGreen = Color.FromArgb(0x1D, 0x9E, 0x75);
    private static readonly Color StatusRed = Color.FromArgb(0xCC, 0x30, 0x30);

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
        StartPosition = FormStartPosition.Manual;   // positioned in OnLoad (centred on the owner's screen)
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
        // SizeMode.Normal sizes each tab to its own text (so タブ幅 follows the 文字数, not the widest tab),
        // and Padding adds left/right + top/bottom room around the label.
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = _tabFont,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Normal,
            Padding = new Point(LogicalToDeviceUnits(16), LogicalToDeviceUnits(5)),
        };
        _tabs.DrawItem += DrawTab;
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildMailTab());
        _tabs.TabPages.Add(BuildLlmTab());
        _tabs.TabPages.Add(BuildDatabaseTab());

        Controls.Add(_tabs);
        Controls.Add(_header);

        // Re-check the connection (debounced) whenever the key or endpoint changes.
        _verifyTimer.Tick += (_, _) => { _verifyTimer.Stop(); VerifyConnection(); };
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(_vm.ApiKey) or nameof(_vm.Endpoint))
        {
            _verifyTimer.Stop();
            _verifyTimer.Start();
        }
    }

    // Size the dialog to a compact portrait (taller than wide) that fits its tallest tab (the LLM tab,
    // with its three groups), then centre it on the screen that holds the main window. Snug rather than a
    // strict golden ratio: the content is wider than tall, so φ would leave a large empty band under the
    // shorter tabs — instead the height just covers the content with a margin and is nudged past the
    // width so it still reads as a portrait. Measured at load — the form renders at AutoScaleFactor 1, so
    // the content size is taken from the live layout.
    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);

        var grid = _tabs.TabPages[2].Controls[0];             // the LLM tab — widest and tallest
        var pagePadding = _tabs.TabPages[0].Padding;
        var tabStrip = _tabs.DisplayRectangle.Top;            // tab strip + top border
        var contentHeight = grid.PreferredSize.Height + pagePadding.Vertical + tabStrip + _header.PreferredSize.Height + Padding.Vertical;
        var contentWidth = grid.PreferredSize.Width + pagePadding.Horizontal + Padding.Horizontal + LogicalToDeviceUnits(10);

        var width = RoundUpTo((int)System.Math.Round(contentWidth * 1.03), 10);
        var snugHeight = (int)System.Math.Round(contentHeight * 1.08);    // content + a small margin
        var portraitHeight = (int)System.Math.Round(width * 1.10);        // kept visibly taller than wide
        var height = RoundUpTo(System.Math.Max(snugHeight, portraitHeight), 10);
        ClientSize = new Size(width, height);

        // Centre on the screen that holds the main window (its owner), not just the primary screen.
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);

        VerifyConnection();   // check any key already loaded from settings
    }

    private static int RoundUpTo(int value, int step) => (value + step - 1) / step * step;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _verifyCts?.Cancel();
        _verifyTimer.Dispose();
        _verifyHttp.Dispose();
        _statusTip.Dispose();
    }

    // ===== 全般 =====

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("全般");
        var grid = NewGrid();
        AddRow(grid, "会社名", Bound(new TextBox(), nameof(_vm.CompanyName), 150));
        // The image-read folder is no longer pre-configured here: 「画像から取り込む」 asks for the folder
        // each time (defaulting to the last-used location), so there is nothing to set in advance.
        AddRow(grid, "", new Label { Text = "画像の読み取りフォルダは「画像から取り込む」の実行時に毎回選びます（前回の場所が既定）。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Margin = new Padding(0, 6, 0, 0) });
        page.Controls.Add(grid);
        return page;
    }

    // ===== メール =====

    private TabPage BuildMailTab()
    {
        var page = NewPage("メール");
        var grid = NewGrid();
        AddRow(grid, "差出人", BoundAscii(new TextBox(), nameof(_vm.MailFrom), 180));
        AddRow(grid, "宛先", BoundAscii(new TextBox(), nameof(_vm.MailTo), 180));

        var serverType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var option in _vm.MailServerOptions)
            serverType.Items.Add(option);
        BindCombo(serverType, () => _vm.MailServerType, v => _vm.MailServerType = v, nameof(_vm.MailServerType));
        AddRow(grid, "メールサーバー種別", serverType);

        // Gmail rows (shown when 種別 = Gmail) — same row style as the rest, not a boxed sub-panel.
        AddToggleRow(grid, "Gmail アドレス", BoundAscii(new TextBox(), nameof(_vm.GmailAddress), 180), nameof(_vm.IsGmail));
        AddToggleRow(grid, "アプリパスワード", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.GmailAppPassword), 150), nameof(_vm.IsGmail));

        // SMTP rows (shown when 種別 = SMTP).
        AddToggleRow(grid, "ホスト", BoundAscii(new TextBox(), nameof(_vm.SmtpHost), 170), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "ポート", BoundNumber(nameof(_vm.SmtpPort), 5, 1, 65535), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "ユーザー名", BoundAscii(new TextBox(), nameof(_vm.SmtpUsername), 150), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "パスワード", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.SmtpPassword), 150), nameof(_vm.IsSmtp));
        AddToggleRow(grid, "", BoundCheck(new CheckBox { Text = "TLS を使う", AutoSize = true }, nameof(_vm.SmtpUseTls)), nameof(_vm.IsSmtp));

        page.Controls.Add(grid);
        return page;
    }

    // ===== LLM (共通 / LLM / 埋め込み) =====

    private TabPage BuildLlmTab()
    {
        var page = NewPage("LLM");
        var grid = NewGrid();
        var row = 0;

        // 共通: one connection (endpoint + key) shared by chat and embeddings, plus the parallelism /
        // timeout that apply to every call.
        AddHeader(grid, "共通", ref row, first: true);
        AddFieldRow(grid, ref row, "エンドポイント", EndpointWithSuffix());
        AddKeyRow(grid, ref row, "API キー", BoundAscii(new TextBox { UseSystemPasswordChar = true }, nameof(_vm.ApiKey)));
        AddFieldRow(grid, ref row, "並列実行数", BoundNumber(nameof(_vm.LlmConcurrency), 3, 1, 64));
        AddFieldRow(grid, ref row, "タイムアウト（秒）", BoundNumber(nameof(_vm.LlmRequestTimeoutSeconds), 4, 1, 600));

        // LLM: the chat model chosen per task.
        AddHeader(grid, "LLM", ref row);
        AddFieldRow(grid, ref row, "OCR モデル", ModelCombo(() => _vm.OcrModel, v => _vm.OcrModel = v, nameof(_vm.OcrModel)));
        AddFieldRow(grid, ref row, "トピック モデル", ModelCombo(() => _vm.TopicModel, v => _vm.TopicModel = v, nameof(_vm.TopicModel)));
        AddFieldRow(grid, ref row, "感情 モデル", ModelCombo(() => _vm.SentimentModel, v => _vm.SentimentModel = v, nameof(_vm.SentimentModel)));
        AddFieldRow(grid, ref row, "レポート モデル", ModelCombo(() => _vm.ReportModel, v => _vm.ReportModel = v, nameof(_vm.ReportModel)));

        // 埋め込み: the embedding model + batch size (the connection is the shared one above).
        AddHeader(grid, "埋め込み", ref row);
        // Wider than the chat-model combos because the embedding model names are long, but still a fixed
        // content-appropriate width (not stretched).
        var embeddingModel = Combo(_vm.EmbeddingModelOptions, () => _vm.EmbeddingModel, v => _vm.EmbeddingModel = v, nameof(_vm.EmbeddingModel), LogicalToDeviceUnits(200));
        AddFieldRow(grid, ref row, "モデル", embeddingModel);
        AddFieldRow(grid, ref row, "バッチ件数", BoundNumber(nameof(_vm.LlmEmbeddingBatchSize), 4, 1, 2048));

        page.Controls.Add(grid);
        return page;
    }

    // ===== データベース (保存先 / 最適化 / バックアップ) =====

    private TabPage BuildDatabaseTab()
    {
        var page = NewPage("データベース");
        var grid = NewGrid();
        var row = 0;

        // 保存先: where the database file lives (read-only), with a button to reveal it in Explorer.
        AddHeader(grid, "保存先", ref row, first: true);
        AddFieldRow(grid, ref row, "DB ファイル", DatabasePathRow());

        // 最適化: VACUUM on demand, plus the startup auto-optimize toggle.
        AddHeader(grid, "最適化", ref row);
        AddFieldRow(grid, ref row, "", ActionRow("今すぐ最適化", OptimizeNow));
        AddFieldRow(grid, ref row, "", BoundCheck(new CheckBox { Text = "起動時に自動で最適化する", AutoSize = true }, nameof(_vm.AutoOptimizeOnStartup)));

        // バックアップ: how many generations to keep, and restore-from-backup.
        AddHeader(grid, "バックアップ", ref row);
        AddFieldRow(grid, ref row, "残す世代", RetentionCombo());
        AddFieldRow(grid, ref row, "", ActionRow("バックアップから復元...", RestoreFromBackup));

        page.Controls.Add(grid);
        return page;
    }

    // The database path as a read-only box (full path on hover) plus a "フォルダを開く" button.
    private Control DatabasePathRow()
    {
        var box = new TextBox
        {
            Text = AppServices.Maintenance.DatabasePath,
            ReadOnly = true,
            Font = Theme.Font(9.5f),
            Width = LogicalToDeviceUnits(250),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 6),
        };
        _statusTip.SetToolTip(box, AppServices.Maintenance.DatabasePath);
        var open = FlatButton("フォルダを開く");
        open.Click += (_, _) => RevealInExplorer(AppServices.Maintenance.DatabasePath);
        return FlowRow(box, open);
    }

    // A keyed retention combo: friendly Japanese labels mapped to the BackupRetention names persisted in
    // the view model (Off / Few / Standard / Many).
    private Control RetentionCombo()
    {
        string[] labels = { "標準（直近7日＋1〜3週前＝10）", "多め（直近14日＋1〜8週前）", "少なめ（直近3日＋1週前）", "バックアップしない" };
        string[] keys = { "Standard", "Many", "Few", "Off" };

        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(240) };
        combo.Items.AddRange(labels);

        var syncing = false;
        void SelectFromVm()
        {
            var index = Array.IndexOf(keys, _vm.BackupRetention);
            combo.SelectedIndex = index < 0 ? 0 : index;
        }
        SelectFromVm();
        combo.SelectedIndexChanged += (_, _) => { if (!syncing && combo.SelectedIndex >= 0) _vm.BackupRetention = keys[combo.SelectedIndex]; };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.BackupRetention))
            {
                syncing = true;
                SelectFromVm();
                syncing = false;
            }
        };
        return FlowRow(combo);
    }

    private void OptimizeNow()
    {
        try
        {
            RunWithWaitCursor(AppServices.Maintenance.Optimize);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "最適化に失敗しました。\n" + ex.Message, "最適化", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        MessageBox.Show(this, "データベースを最適化しました。", "最適化", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Lets the user pick a backup file and replace the live database with it, then restarts the app so
    // every connection reopens against the restored file. The current database is auto-backed-up first.
    private void RestoreFromBackup()
    {
        var maintenance = AppServices.Maintenance;
        if (maintenance.ListBackups().Count == 0)
        {
            MessageBox.Show(this, "バックアップがまだありません。", "復元", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var picker = new OpenFileDialog
        {
            Title = "復元するバックアップを選択",
            InitialDirectory = maintenance.BackupFolder,
            Filter = "バックアップ (*.db)|*.db",
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        var confirm = MessageBox.Show(this,
            "選択したバックアップで現在のデータベースを置き換えます。\n現在のデータベースは復元前に自動でバックアップされます。\n\n復元後、アプリを再起動します。よろしいですか？",
            "バックアップから復元", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
            return;

        try
        {
            RunWithWaitCursor(() => maintenance.Restore(picker.FileName, DateTime.Now));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "復元に失敗しました。\n" + ex.Message, "復元", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Application.Restart();
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch { /* opening the folder is best-effort */ }
    }

    // Runs a synchronous maintenance action with the busy cursor shown for its duration.
    private void RunWithWaitCursor(Action action)
    {
        var previous = Cursor.Current;
        UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;
        try { action(); }
        finally { UseWaitCursor = false; Cursor.Current = previous; }
    }

    // A flat, bordered action button matching the 全般 tab's "選択..." button.
    private Button FlatButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Theme.BodyText,
            Font = Theme.Font(9f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 5, 0, 5),
            Padding = new Padding(10, 4, 10, 4),
        };
        button.FlatAppearance.BorderColor = Theme.CardBorder;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    // A single-button row (the button does not stretch to fill the column).
    private Control ActionRow(string text, Action onClick)
    {
        var button = FlatButton(text);
        button.Click += (_, _) => onClick();
        return FlowRow(button);
    }

    // Wraps controls in a non-stretching, left-flowed holder (so row styling does not expand them).
    private static FlowLayoutPanel FlowRow(params Control[] controls)
    {
        var holder = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.White };
        holder.Controls.AddRange(controls);
        return holder;
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

    // Adds a right-aligned caption + the input control as one auto-flowed row (used by 全般 / メール).
    private static void AddRow(TableLayoutPanel grid, string caption, Control input)
    {
        grid.Controls.Add(MakeLabel(caption));
        grid.Controls.Add(StyleInput(input));
    }

    // A toggle row whose label + input show only while `visibleProperty` is true (Gmail / SMTP groups).
    private void AddToggleRow(TableLayoutPanel grid, string caption, Control input, string visibleProperty)
    {
        var label = MakeLabel(caption);
        StyleInput(input);
        label.DataBindings.Add("Visible", _vm, visibleProperty);
        input.DataBindings.Add("Visible", _vm, visibleProperty);
        grid.Controls.Add(label);
        grid.Controls.Add(input);
    }

    // A bold group header spanning both columns, with a 16 DIP gap above it (except the first group).
    private void AddHeader(TableLayoutPanel grid, string text, ref int row, bool first = false)
    {
        var header = new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Font(10f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, first ? 0 : LogicalToDeviceUnits(16), 0, 4),
        };
        grid.Controls.Add(header, 0, row);
        grid.SetColumnSpan(header, 2);
        row++;
    }

    // A caption + input placed at an explicit row (the LLM tab manages its own row index because of the
    // interspersed group headers).
    private void AddFieldRow(TableLayoutPanel grid, ref int row, string caption, Control input)
    {
        grid.Controls.Add(MakeLabel(caption), 0, row);
        grid.Controls.Add(StyleInput(input), 1, row);
        row++;
    }

    // An API-key row: a shortened key box plus the connection-status badge (✔/✗) to its right.
    private void AddKeyRow(TableLayoutPanel grid, ref int row, string caption, TextBox keyBox)
    {
        keyBox.Width = LogicalToDeviceUnits(300);   // shortened to leave room for the badge
        keyBox.Margin = new Padding(0, 6, 0, 6);

        _keyStatus.AutoSize = false;
        _keyStatus.Size = new Size(LogicalToDeviceUnits(22), keyBox.PreferredHeight);
        _keyStatus.TextAlign = ContentAlignment.MiddleCenter;
        _keyStatus.Font = Theme.Font(12f, FontStyle.Bold);
        _keyStatus.Margin = new Padding(4, 6, 0, 6);

        var holder = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.White };
        holder.Controls.Add(keyBox);
        holder.Controls.Add(_keyStatus);

        grid.Controls.Add(MakeLabel(caption), 0, row);
        grid.Controls.Add(holder, 1, row);
        row++;
    }

    // The endpoint text box with a muted "OpenAI API（互換含む）" hint to its right.
    private Control EndpointWithSuffix()
    {
        var box = BoundAscii(new TextBox(), nameof(_vm.Endpoint));
        box.Width = LogicalToDeviceUnits(230);
        box.Margin = new Padding(0, 6, 0, 6);

        var suffix = new Label
        {
            Text = "OpenAI API（互換含む）",
            AutoSize = false,
            Size = new Size(LogicalToDeviceUnits(150), box.PreferredHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Font(9f),
            Margin = new Padding(6, 6, 0, 6),
        };

        var holder = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.White };
        holder.Controls.Add(box);
        holder.Controls.Add(suffix);
        return holder;
    }

    // Runs the (debounced) connection check on the shared endpoint + key and updates the badge. async
    // void is fine here: it resumes on the UI thread (WinForms sync context). A blank key clears it.
    private async void VerifyConnection()
    {
        var endpoint = _vm.Endpoint;
        var apiKey = _vm.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            SetStatus("", Color.Empty, null);
            return;
        }

        _verifyCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _verifyCts = cts;

        SetStatus("…", Theme.Muted, "接続を確認しています…");

        LlmConnectionResult result;
        try
        {
            result = await LlmConnectionTester.VerifyAsync(endpoint, apiKey, _verifyHttp, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // superseded by a newer check
        }
        if (cts.IsCancellationRequested)
            return;

        switch (result)
        {
            case LlmConnectionResult.Ok:
                SetStatus("✔", StatusGreen, "接続できました");
                break;
            case LlmConnectionResult.Unauthorized:
                SetStatus("✗", StatusRed, "API キーが無効です");
                break;
            default:
                SetStatus("✗", StatusRed, "接続できませんでした");
                break;
        }
    }

    private void SetStatus(string glyph, Color color, string? tooltip)
    {
        _keyStatus.Text = glyph;
        if (color != Color.Empty)
            _keyStatus.ForeColor = color;
        _statusTip.SetToolTip(_keyStatus, tooltip ?? "");
    }

    // A right-aligned caption for the input beside it.
    private static Label MakeLabel(string caption) =>
        new() { Text = caption, AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 8, 8) };

    // Inputs keep their own content-appropriate width, left-aligned and vertically centred in the row
    // — not stretched to fill the column.
    private static Control StyleInput(Control input)
    {
        if (input is TextBox)
            input.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(0, 6, 0, 6);
        return input;
    }

    // Draws a tab: the selected one bold + accent-coloured on a white (page-matching) background.
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
        => Combo(_vm.ModelOptions, get, set, property);

    // A DropDownList combo over the given options, two-way bound to a string property.
    private ComboBox Combo(System.Collections.Generic.IEnumerable<string> options, System.Func<string> get, System.Action<string> set, string property, int width = 200)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        foreach (var option in options)
            combo.Items.Add(option);
        BindCombo(combo, get, set, property);
        return combo;
    }

    // Two-way binds a DropDownList combo to a string property (no SelectedItemChanged for DataBindings,
    // so the wiring is explicit: the pick writes the property, the property's change writes back).
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

    private TextBox Bound(TextBox box, string property, int widthDip = 150)
    {
        box.Font = Theme.Font(9.5f);
        box.Width = LogicalToDeviceUnits(widthDip);
        box.DataBindings.Add("Text", _vm, property, false, DataSourceUpdateMode.OnPropertyChanged);
        return box;
    }

    // Like Bound, but with the IME off — for fields that never take Japanese (差出人, 宛先, API キー,
    // エンドポイント, 数値欄, ...). Fields that may take Japanese (会社名, folder, archive) use Bound.
    private TextBox BoundAscii(TextBox box, string property, int widthDip = 150)
    {
        box.ImeMode = ImeMode.Disable;
        return Bound(box, property, widthDip);
    }

    // A digits-only integer field clamped to [min, max]: non-digits are blocked while typing, and on
    // leaving the field the value is sanitised (also covering paste) and clamped into range. Sized to
    // (maxDigits + 2) characters, in an auto-size holder so the row styling does not stretch it.
    private Control BoundNumber(string property, int maxDigits, int min, int max)
    {
        var box = BoundAscii(new TextBox(), property);
        box.MaxLength = maxDigits;
        box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        box.Leave += (_, _) =>
        {
            var digits = new string(box.Text.Where(char.IsDigit).ToArray());
            var value = System.Math.Clamp(int.TryParse(digits, out var n) ? n : min, min, max);
            var text = value.ToString();
            if (box.Text != text)
                box.Text = text;   // updates the bound property
        };
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
