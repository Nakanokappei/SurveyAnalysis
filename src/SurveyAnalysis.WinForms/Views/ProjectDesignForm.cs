using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The project design dialog — the WinForms counterpart of ProjectDesignView.axaml. Edits the survey
// schema (project name + a list of field rows) and confirms via the view model's command, which fires
// Completed with the resulting project. Drives create, edit, and CSV-seeded create modes off the same
// view model. Built entirely from layout containers (no explicit coordinates or width math): a scrolling
// content panel stacks the sections in a TableLayoutPanel, each section stretching to the column width
// via Anchor, and a docked action bar right-aligns its buttons in their own grid. (The dummy OCR "テスト"
// panel is not ported — it produced placeholder data only.)
internal sealed class ProjectDesignForm : Form
{
    private readonly ProjectDesignViewModel _vm;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Padding = new Padding(28) };
    private readonly TextBox _projectName = new() { Font = Theme.Font(10f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };
    private readonly TextBox _description = new() { Font = Theme.Font(10f), Multiline = true, ScrollBars = ScrollBars.Vertical, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };
    private readonly TableLayoutPanel _fields = NewStack();

    // トピックタブ: 左の自由記述列リスト＋右のトピック CRUD。
    private TabControl _tabs = null!;
    private ListBox _topicColumns = null!;
    private readonly System.Collections.Generic.List<DataField> _freeTextFields = new();
    private ListBox _topicList = null!;
    private Label _topicCaption = null!;
    private Button _topicAdd = null!;
    private Button _topicRename = null!;
    private Button _topicDelete = null!;
    private Button _topicRebuild = null!;
    private TableLayoutPanel _columnHeader = null!;
    private readonly Button _addField = new IconButton { Glyph = "➕", Text = "項目を追加", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.White, ForeColor = Theme.Accent, Font = Theme.Font(10f), Padding = new Padding(0, 11, 0, 11), Margin = Padding.Empty, Cursor = Cursors.Hand };
    private readonly Button _confirm = new() { FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(10f, FontStyle.Bold), Padding = new Padding(18, 6, 18, 6), AutoSize = true, Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = new Padding(12, 0, 0, 0) };
    private readonly Label _intro = new() { Text = "アンケート用紙の各項目に、データ型と分析方法を割り当てます。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 14) };
    private readonly Label _header = new() { Text = "データ項目", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(13f, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 8) };

    // The confirmed project, or null if cancelled / closed.
    public Project? ResultProject { get; private set; }

    // True when the user confirmed deleting the project (edit mode); the host then removes it.
    public bool DeleteConfirmed { get; private set; }

    public ProjectDesignForm(ProjectDesignViewModel vm)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _vm = vm;
        Text = vm.DialogTitle;
        // This dialog holds a wide table, so it is resizable and maximizable.
        MaximizeBox = true;
        ClientSize = new Size(LogicalToDeviceUnits(800), LogicalToDeviceUnits(500));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(320));
        Font = Theme.Font();
        BackColor = ColorTranslator.FromHtml("#F8FAFC");

        BuildLayout();

        _projectName.Text = _vm.ProjectName;
        _description.Text = _vm.ProjectDescription;
        _confirm.Text = _vm.ConfirmLabel;
        _projectName.TextChanged += (_, _) => _vm.ProjectName = _projectName.Text;
        _description.TextChanged += (_, _) => _vm.ProjectDescription = _description.Text;
        _addField.Click += (_, _) => _vm.AddFieldCommand.Execute(null);
        _confirm.Click += (_, _) => OnConfirm();

        _vm.Fields.CollectionChanged += OnFieldsChanged;
        _vm.CreateProjectCommand.CanExecuteChanged += (_, _) => UpdateConfirmEnabled();
        _vm.Completed += OnCompleted;
        _vm.Cancelled += OnCancelled;
        _vm.DeleteRequested += OnDeleteRequested;

        RebuildFields();
        UpdateConfirmEnabled();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vm.Fields.CollectionChanged -= OnFieldsChanged;
            _vm.Completed -= OnCompleted;
            _vm.Cancelled -= OnCancelled;
            _vm.DeleteRequested -= OnDeleteRequested;
        }
        base.Dispose(disposing);
    }

    // Size the dialog so the 全般 tab shows about six data-item rows by default — the description box took
    // the vertical space of the rows it replaced. Resizing still grows the scrolling list beyond six.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_fields.Controls.Count == 0)
            return;
        var row = _fields.Controls[0];
        var rowHeight = row.Height + row.Margin.Vertical;
        var desired = rowHeight * 6 + _content.Padding.Vertical;   // about six rows visible (add button scrolls below)
        Height += desired - _content.ClientSize.Height;
    }

    private void BuildLayout()
    {
        _addField.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");

        // Two tabs over a shared action bar: 全般 (name / description / data items) and トピック (the
        // per-column topic dictionary). The bar (cancel / confirm / delete) stays below both tabs.
        _tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Font(10f) };
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildTopicsTab());
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 1) RefreshTopicColumns(); };

        Controls.Add(_tabs);            // Fill — add first so the bar keeps the bottom
        Controls.Add(BuildActionBar());  // Bottom

        _content.Layout += (_, _) => SyncHeaderToScrollbar();
    }

    // ===== 全般 タブ =====

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("全般") { BackColor = ColorTranslator.FromHtml("#F8FAFC"), UseVisualStyleBackColor = false };

        // Fixed top area: intro, name, description, the データ項目 title and the table's column header —
        // it stays put while the field rows scroll below it.
        var topArea = NewStack();
        topArea.Dock = DockStyle.Top;
        topArea.Padding = new Padding(28, 20, 28, 0);
        AddSection(topArea, _intro);
        AddSection(topArea, LabelledCard("プロジェクト名", _projectName));
        AddSection(topArea, LabelledCard("説明（任意）— アンケートの内容。取り込み・OCR の精度を上げるヒントに使われます。", _description));
        AddSection(topArea, _header);
        _columnHeader = BuildColumnHeader();
        AddSection(topArea, _columnHeader);

        // Scrolling area (the existing AutoScroll panel): only the field rows and the add button.
        var rows = NewStack();
        AddSection(rows, _fields);
        AddSection(rows, _addField);
        _content.Padding = new Padding(28, 4, 28, 8);
        _content.Controls.Clear();
        _content.Controls.Add(rows);

        page.Controls.Add(_content);   // Fill
        page.Controls.Add(topArea);    // Top
        return page;
    }

    // A soft card with a caption above an input that stretches to the card width. The description box is
    // multiline (its own Height pre-set), the name box single-line.
    private Control LabelledCard(string caption, Control input)
    {
        var card = SoftCard();
        var inner = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14), BackColor = Color.White };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 2) });
        if (input is TextBox { Multiline: true } box)
            box.Height = box.Font.Height * 3 + 8;   // about three lines tall
        inner.Controls.Add(input);
        card.Controls.Add(inner);
        return card;
    }

    private Control BuildActionBar()
    {
        // A spacer column pushes キャンセル / 確定 to the right; an optional delete button sits at the left.
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 4, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Padding = new Padding(28, 10, 28, 10) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // delete (edit mode only; empty otherwise)
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // spacer
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // cancel
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // confirm
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (_vm.IsEditing)
        {
            var delete = new Button { Text = "このプロジェクトを削除する", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Danger, ForeColor = Color.White, Font = Theme.Font(10f), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Padding = new Padding(14, 6, 14, 6) };
            delete.FlatAppearance.BorderSize = 0;
            delete.Click += (_, _) => ConfirmAndDelete();
            bar.Controls.Add(delete, 0, 0);
        }

        var cancel = new Button { Text = "キャンセル", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml("#F8FAFC"), ForeColor = Theme.BodyText, Font = Theme.Font(10f), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Padding = new Padding(12, 6, 12, 6) };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => _vm.CancelCommand.Execute(null);
        _confirm.FlatAppearance.BorderSize = 0;
        bar.Controls.Add(cancel, 2, 0);
        bar.Controls.Add(_confirm, 3, 0);
        return bar;
    }

    // ===== トピック タブ =====

    // Left = the project's 自由記述 columns; right = the selected column's topic dictionary (add / rename
    // / delete) plus the "再構築" (clustering) button. Topics are managed live against the database and
    // only for saved columns (a column gains an id once the project is saved).
    private TabPage BuildTopicsTab()
    {
        var page = new TabPage("トピック") { BackColor = ColorTranslator.FromHtml("#F8FAFC"), UseVisualStyleBackColor = false, Padding = new Padding(16) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(190)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Left column: the 自由記述 column list.
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Margin = new Padding(0, 0, 12, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label { Text = "自由記述の列", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        _topicColumns = new ListBox { Dock = DockStyle.Fill, Font = Theme.Font(9.5f), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        _topicColumns.SelectedIndexChanged += (_, _) => OnTopicColumnSelected();
        left.Controls.Add(_topicColumns, 0, 1);
        layout.Controls.Add(left, 0, 0);

        // Right column: caption + actions, the topic list, and the rebuild button.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Margin = new Padding(0, 0, 0, 6) };
        _topicCaption = new Label { Text = "列を選択してください", AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f, FontStyle.Bold), Margin = new Padding(0, 6, 12, 0) };
        _topicAdd = SmallButton("追加");
        _topicRename = SmallButton("名前変更");
        _topicDelete = SmallButton("削除");
        _topicAdd.Click += (_, _) => AddTopic();
        _topicRename.Click += (_, _) => RenameTopic();
        _topicDelete.Click += (_, _) => DeleteTopic();
        head.Controls.Add(_topicCaption);
        head.Controls.Add(_topicAdd);
        head.Controls.Add(_topicRename);
        head.Controls.Add(_topicDelete);
        right.Controls.Add(head, 0, 0);

        _topicList = new ListBox { Dock = DockStyle.Fill, Font = Theme.Font(9.5f), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        right.Controls.Add(_topicList, 0, 1);

        _topicRebuild = new IconButton { Glyph = "✨", Text = "既存データからトピックを再構築", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, BackColor = Color.White, ForeColor = Theme.Accent, Font = Theme.Font(9.5f), Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 8, 0, 0), Cursor = Cursors.Hand };
        _topicRebuild.FlatAppearance.BorderColor = Theme.Accent;
        _topicRebuild.Click += (_, _) => RebuildTopics();
        right.Controls.Add(_topicRebuild, 0, 2);

        layout.Controls.Add(right, 1, 0);
        page.Controls.Add(layout);
        return page;
    }

    private Button SmallButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.BodyText, Font = Theme.Font(9f), Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 0), Padding = new Padding(8, 3, 8, 3) };
        button.FlatAppearance.BorderColor = Theme.CardBorder;
        return button;
    }

    // (Re)loads the 自由記述 column list from the current draft fields. Called when the トピック tab opens
    // so it reflects edits made on 全般.
    private void RefreshTopicColumns()
    {
        var previous = _topicColumns.SelectedIndex >= 0 ? _freeTextFields[_topicColumns.SelectedIndex] : null;
        _freeTextFields.Clear();
        _topicColumns.BeginUpdate();
        _topicColumns.Items.Clear();
        foreach (var field in _vm.Fields)
            if (field.FieldType == FieldType.FreeText)
            {
                _freeTextFields.Add(field);
                _topicColumns.Items.Add(string.IsNullOrWhiteSpace(field.Name) ? "（未命名）" : field.Name);
            }
        _topicColumns.EndUpdate();

        if (_topicColumns.Items.Count == 0)
        {
            OnTopicColumnSelected();
            return;
        }
        var index = previous is null ? 0 : _freeTextFields.IndexOf(previous);
        _topicColumns.SelectedIndex = index < 0 ? 0 : index;
    }

    private DataField? SelectedTopicField =>
        _topicColumns.SelectedIndex >= 0 && _topicColumns.SelectedIndex < _freeTextFields.Count
            ? _freeTextFields[_topicColumns.SelectedIndex]
            : null;

    // Reloads the right pane for the selected column. Topics are managed only for saved columns (id > 0);
    // an unsaved column (new project / just-added field) shows a hint until the project is saved.
    private void OnTopicColumnSelected()
    {
        _topicList.Items.Clear();
        var field = SelectedTopicField;
        if (field is null)
        {
            _topicCaption.Text = _vm.Fields.Any(f => f.FieldType == FieldType.FreeText) ? "列を選択してください" : "自由記述（テキスト（改行あり））の列がありません";
            EnableTopicActions(false, false);
            return;
        }
        if (field.Id <= 0)
        {
            _topicCaption.Text = $"「{ColumnLabel(field)}」：保存後にトピックを管理できます";
            EnableTopicActions(false, false);
            return;
        }

        _topicCaption.Text = $"「{ColumnLabel(field)}」のトピック";
        foreach (var topic in AppServices.Topics.ListTopics(field.Id))
            _topicList.Items.Add(new TopicItem(topic.Id, topic.Label));
        EnableTopicActions(true, true);
    }

    private void EnableTopicActions(bool canAdd, bool canRebuild)
    {
        _topicAdd.Enabled = canAdd;
        _topicRename.Enabled = canAdd;
        _topicDelete.Enabled = canAdd;
        _topicRebuild.Enabled = canRebuild;
    }

    private static string ColumnLabel(DataField field) => string.IsNullOrWhiteSpace(field.Name) ? "（未命名）" : field.Name;

    private void AddTopic()
    {
        if (SelectedTopicField is not { Id: > 0 } field)
            return;
        var label = PromptForText("トピックを追加", "トピック名", "");
        if (label is null)
            return;
        if (_topicList.Items.Cast<TopicItem>().Any(t => t.Label == label))
        {
            MessageBox.Show(this, "同じ名前のトピックが既にあります。", "トピック", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppServices.Topics.AddTopic(field.Id, label);
        OnTopicColumnSelected();
    }

    private void RenameTopic()
    {
        if (SelectedTopicField is not { Id: > 0 } || _topicList.SelectedItem is not TopicItem current)
            return;
        var label = PromptForText("トピック名を変更", "トピック名", current.Label);
        if (label is null || label == current.Label)
            return;
        if (_topicList.Items.Cast<TopicItem>().Any(t => t.Id != current.Id && t.Label == label))
        {
            MessageBox.Show(this, "同じ名前のトピックが既にあります。", "トピック", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppServices.Topics.RenameTopic(current.Id, label);
        OnTopicColumnSelected();
    }

    private void DeleteTopic()
    {
        if (_topicList.SelectedItem is not TopicItem current)
            return;
        var answer = MessageBox.Show(this, $"トピック「{current.Label}」を削除します。よろしいですか？", "トピックの削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;
        AppServices.Topics.DeleteTopic(current.Id);
        OnTopicColumnSelected();
    }

    // Phase 4 で実装：埋め込み→クラスタリング→命名でトピック辞書を自動生成する。
    private void RebuildTopics()
    {
        MessageBox.Show(this, "既存データからのトピック自動生成は次の更新で有効になります。", "トピックの再構築", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // A minimal single-line text prompt (WinForms has no built-in InputBox). Returns the trimmed text, or
    // null on cancel / empty.
    private string? PromptForText(string title, string label, string initial)
    {
        using var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(LogicalToDeviceUnits(360), LogicalToDeviceUnits(120)), Font = Theme.Font() };
        var caption = new Label { Text = label, AutoSize = true, Location = new Point(LogicalToDeviceUnits(14), LogicalToDeviceUnits(14)) };
        var box = new TextBox { Text = initial, Location = new Point(LogicalToDeviceUnits(14), LogicalToDeviceUnits(38)), Width = LogicalToDeviceUnits(332), Font = Theme.Font(10f) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(LogicalToDeviceUnits(190), LogicalToDeviceUnits(80)), Width = LogicalToDeviceUnits(75) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(LogicalToDeviceUnits(271), LogicalToDeviceUnits(80)), Width = LogicalToDeviceUnits(75) };
        form.Controls.AddRange(new Control[] { caption, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    // A topic row in the list: carries the id so rename / delete address the right row.
    private sealed record TopicItem(long Id, string Label)
    {
        public override string ToString() => Label;
    }

    // When the field list scrolls vertically its rows lose the scrollbar's width; inset the (fixed)
    // header by the same amount so the columns stay lined up.
    private void SyncHeaderToScrollbar()
    {
        var scrollbar = _content.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        if (_columnHeader.Margin.Right != scrollbar)
            _columnHeader.Margin = new Padding(_columnHeader.Margin.Left, _columnHeader.Margin.Top, scrollbar, _columnHeader.Margin.Bottom);
    }

    // The table's column header: the 10 captions over the field rows, using the same shared columns so
    // they align. Captions wrap within their fixed-width columns; the long boolean headers therefore
    // stack onto a few lines.
    private static TableLayoutPanel BuildColumnHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, RowCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ColorTranslator.FromHtml("#F1F5F9"),
            Margin = new Padding(0, 0, 0, 2), Padding = new Padding(0, 4, 0, 4),
        };
        FieldRowControl.DefineColumns(header);
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var captions = new[]
        {
            "#", "項目名", "データ型", "分析方法", "月次集計に使う",
            "読み込み日をデフォルトにする", "暗号化・非表示", "削除",
        };
        for (var i = 0; i < captions.Length; i++)
        {
            var caption = new Label
            {
                Text = captions[i], AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold),
                Anchor = AnchorStyles.None, Margin = new Padding(2, 0, 2, 0), TextAlign = ContentAlignment.MiddleCenter,
            };
            var width = FieldRowControl.ColumnWidths[i];
            if (width > 0)
                caption.MaximumSize = new Size(width - 4, 0);  // wrap inside the fixed column
            header.Controls.Add(caption, i, 0);
        }
        return header;
    }

    // A 1-column AutoSize stack whose children flow downward; each child anchors to fill the width.
    private static TableLayoutPanel NewStack()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            BackColor = ColorTranslator.FromHtml("#F8FAFC"),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    // Appends a section as a new AutoSize row. Sections left at the default Anchor stretch horizontally;
    // a section that set a left-only Anchor (captions) keeps it.
    private static void AddSection(TableLayoutPanel stack, Control section)
    {
        if (section.Anchor == (AnchorStyles.Top | AnchorStyles.Left))  // default → stretch horizontally
            section.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(section, 0, stack.RowCount);
        stack.RowCount++;
    }

    // A white panel with the soft #E2E8F0 border (drawn, not FixedSingle), sized to its content.
    private static Panel SoftCard() => new BorderedPanel
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        BackColor = Color.White,
    };

    // A panel that draws the soft card border itself (ResizeRedraw keeps it crisp as the card reflows).
    private sealed class BorderedPanel : Panel
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

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildFields();

    // Rebuilds the field rows from the view model. Simple and cheap (a handful of fields), and it keeps
    // the add/remove logic in the view model. Each row anchors to fill the column, so no width is set.
    private void RebuildFields()
    {
        _fields.SuspendLayout();
        foreach (Control old in _fields.Controls)
            old.Dispose();
        _fields.Controls.Clear();
        _fields.RowStyles.Clear();
        _fields.RowCount = 0;
        var ordinal = 1;
        foreach (var field in _vm.Fields)
        {
            var row = new FieldRowControl(field, ordinal++, f => _vm.RemoveFieldCommand.Execute(f));
            _fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _fields.Controls.Add(row, 0, _fields.RowCount);
            _fields.RowCount++;
        }
        _fields.ResumeLayout();
    }

    private void UpdateConfirmEnabled() => _confirm.Enabled = _vm.CreateProjectCommand.CanExecute(null);

    // On confirm, reject a duplicate project name (names are unique app-wide) before saving, keeping the
    // dialog open so the entered schema is not lost. Otherwise proceed with the create / save command.
    private void OnConfirm()
    {
        var name = _vm.ProjectName.Trim();
        if (_vm.IsNameAvailable is { } available && !available(name))
        {
            MessageBox.Show(this,
                $"「{name}」という名前のプロジェクトは既にあります。\n別の名前を入力してください。",
                "プロジェクト名の重複", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _vm.CreateProjectCommand.Execute(null);
    }

    private void OnCompleted(Project project)
    {
        ResultProject = project;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCancelled()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // Confirms (defaulting to "No"), then asks the view model to delete. Spells out that the project's
    // data items and imported responses go with it and that it cannot be undone.
    private void ConfirmAndDelete()
    {
        var answer = MessageBox.Show(this,
            $"プロジェクト「{_vm.ProjectName}」を削除します。\nデータ項目と取り込んだ回答もすべて削除され、元に戻せません。\n\n削除してよろしいですか？",
            "プロジェクトの削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
            _vm.DeleteProjectCommand.Execute(null);
    }

    private void OnDeleteRequested()
    {
        DeleteConfirmed = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
