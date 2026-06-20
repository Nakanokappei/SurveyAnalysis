using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SurveyAnalysis.Llm.Consumers;
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
    private Panel _topicListHost = null!;        // scroll container for the topic rows
    private TableLayoutPanel _topicRows = null!; // one row per topic (label / ✏ / 削除)
    private Label _topicCaption = null!;
    private Button _topicAdd = null!;
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
        // 8 DIP gap between the window edges and the content (tab control / action bar), matching the
        // settings dialog. The tab control is docked, so its Margin is ignored — the form's Padding is
        // what insets it from the window frame.
        Padding = new Padding(LogicalToDeviceUnits(8));

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

    // Size the dialog so the 全般 tab shows about eight data-item rows by default (most surveys fit without
    // scrolling). Resizing still grows the scrolling list beyond that.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_fields.Controls.Count == 0)
            return;
        var row = _fields.Controls[0];
        var rowHeight = row.Height + row.Margin.Vertical;
        var desired = rowHeight * 8 + _content.Padding.Vertical;   // about eight rows visible (add button scrolls below)
        Height += desired - _content.ClientSize.Height;
    }

    private void BuildLayout()
    {
        _addField.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");

        // Two tabs over a shared action bar: 全般 (name / description / data items) and トピック (the
        // per-column topic dictionary). The bar (cancel / confirm / delete) stays below both tabs.
        // Tabs sized to their own labels (SizeMode.Normal) with left/right + top/bottom padding so the
        // width follows the 文字数 rather than a fixed block.
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.Font(10f),
            SizeMode = TabSizeMode.Normal,
            Padding = new Point(LogicalToDeviceUnits(16), LogicalToDeviceUnits(5)),
        };
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

    // Two panes that fill the tab, then the 再構築 button beneath the left pane. Left pane = the 自由記述
    // columns list; right pane = the selected column's topic dictionary as a row list (label / ✏ rename /
    // 削除) with a full-width 追加 button at its foot. Because both lists fill to the same pane bottom, the
    // 追加 button's bottom lines up with the 自由記述列 listbox's bottom; the 再構築 button sits below that,
    // under the left pane. Topics are managed live against the database and only for saved columns.
    private TabPage BuildTopicsTab()
    {
        var page = new TabPage("トピック") { BackColor = ColorTranslator.FromHtml("#F8FAFC"), UseVisualStyleBackColor = false, Padding = new Padding(16) };

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // the two panes
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 再構築 button row

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(250)));  // wide enough for the 再構築 label below
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Left pane: heading + the 自由記述 column list (the listbox fills to the pane bottom).
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Margin = new Padding(0, 0, 12, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(TopicPaneCaption("自由記述の列"), 0, 0);
        _topicColumns = new ListBox { Dock = DockStyle.Fill, Font = Theme.Font(9.5f), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        _topicColumns.SelectedIndexChanged += (_, _) => OnTopicColumnSelected();
        left.Controls.Add(_topicColumns, 0, 1);
        panes.Controls.Add(left, 0, 0);

        // Right pane: caption, the topic row list (scrolls), then a full-width 追加 button. The button is the
        // pane's bottom row, so its bottom edge lands level with the left listbox's bottom.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _topicCaption = TopicPaneCaption("列を選択してください");
        right.Controls.Add(_topicCaption, 0, 0);

        // The rows live in a Dock=Top stack inside an AutoScroll host, so their width tracks the host
        // (shrinking when the scrollbar appears) and a long dictionary scrolls without a horizontal bar.
        _topicListHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        _topicRows = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = Color.White };
        _topicRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _topicListHost.Controls.Add(_topicRows);
        right.Controls.Add(_topicListHost, 0, 1);

        _topicAdd = FullWidthButton("➕", "トピックを追加", Theme.Accent);
        _topicAdd.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
        _topicAdd.Click += (_, _) => AddTopic();
        right.Controls.Add(_topicAdd, 0, 2);

        panes.Controls.Add(right, 1, 0);
        outer.Controls.Add(panes, 0, 0);

        // 再構築 button under the left pane: a 2-column row so the button sits in the same 250-wide left band,
        // inset 12px on the right to line up with the listbox above it (the rest of the row is empty).
        var rebuildRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        rebuildRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(250)));
        rebuildRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rebuildRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _topicRebuild = FullWidthButton("✨", "既存データからトピックを再構築", Theme.Accent);
        _topicRebuild.Margin = new Padding(0, 8, 12, 0);
        _topicRebuild.FlatAppearance.BorderColor = Theme.Accent;
        _topicRebuild.Click += (_, _) => RebuildTopics();
        rebuildRow.Controls.Add(_topicRebuild, 0, 0);
        outer.Controls.Add(rebuildRow, 0, 1);

        page.Controls.Add(outer);
        return page;
    }

    // A pane heading for the トピック tab (left "自由記述の列" / right caption), sized identically so the two
    // list areas below them line up at the same height.
    private static Label TopicPaneCaption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f, FontStyle.Bold),
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6),
    };

    // A flat, full-width (stretches to its column) accent button matching the 全般 tab's 項目を追加 — used for
    // both 追加 and 再構築 so their rows are the same height (keeping the two list areas aligned).
    private static IconButton FullWidthButton(string glyph, string text, Color foreColor) => new()
    {
        Glyph = glyph, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.White, ForeColor = foreColor,
        Font = Theme.Font(10f), Padding = new Padding(0, 11, 0, 11), Margin = new Padding(0, 8, 0, 0), Cursor = Cursors.Hand,
    };

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
        var field = SelectedTopicField;
        if (field is null)
        {
            _topicCaption.Text = _vm.Fields.Any(f => f.FieldType == FieldType.FreeText) ? "列を選択してください" : "自由記述（テキスト（改行あり））の列がありません";
            RebuildTopicRows(Array.Empty<FieldTopic>());
            EnableTopicActions(false, false);
            return;
        }
        if (field.Id <= 0)
        {
            _topicCaption.Text = $"「{ColumnLabel(field)}」：保存後にトピックを管理できます";
            RebuildTopicRows(Array.Empty<FieldTopic>());
            EnableTopicActions(false, false);
            return;
        }

        _topicCaption.Text = $"「{ColumnLabel(field)}」のトピック";
        RebuildTopicRows(AppServices.Topics.ListTopics(field.Id));
        EnableTopicActions(true, true);
    }

    // The 追加 / 再構築 buttons enable with a saved column; rename / delete are per-row (shown only when
    // there are rows), so they need no separate toggle.
    private void EnableTopicActions(bool canAdd, bool canRebuild)
    {
        _topicAdd.Enabled = canAdd;
        _topicRebuild.Enabled = canRebuild;
    }

    private static string ColumnLabel(DataField field) => string.IsNullOrWhiteSpace(field.Name) ? "（未命名）" : field.Name;

    // Rebuilds the topic row list for the selected column: one row per topic (label / ✏ rename / 削除).
    private void RebuildTopicRows(System.Collections.Generic.IReadOnlyList<FieldTopic> topics)
    {
        _topicRows.SuspendLayout();
        foreach (Control old in _topicRows.Controls)
            old.Dispose();
        _topicRows.Controls.Clear();
        _topicRows.RowStyles.Clear();
        _topicRows.RowCount = 0;
        foreach (var topic in topics)
        {
            _topicRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _topicRows.Controls.Add(BuildTopicRow(topic), 0, _topicRows.RowCount);
            _topicRows.RowCount++;
        }
        _topicRows.ResumeLayout();
    }

    // One topic row: the label fills the width, then a ✏ rename button and a 削除 button at the right end.
    // The 削除 style mirrors the 全般 tab's data-item delete button (red text, soft red border).
    private Control BuildTopicRow(FieldTopic topic)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.White,
            Margin = Padding.Empty, Padding = new Padding(8, 3, 8, 3),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label { Text = topic.Label, AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };

        var rename = new IconButton { Glyph = Icons.Edit.Glyph, IconFontName = Icons.Edit.Font, Text = "トピック名の変更", IconSize = 9f, AutoSize = true, BackColor = Color.White, ForeColor = Theme.BodyText, Font = Theme.Font(9f), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 6, 0), Padding = new Padding(8, 3, 8, 3) };
        rename.FlatAppearance.BorderColor = Theme.CardBorder;
        rename.Click += (_, _) => RenameTopic(topic);

        var remove = new Button { Text = "削除", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Danger, BackColor = Color.White, Font = Theme.Font(9f), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = Padding.Empty, Padding = new Padding(8, 2, 8, 2) };
        remove.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FCA5A5");
        remove.Click += (_, _) => DeleteTopic(topic);

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(rename, 1, 0);
        row.Controls.Add(remove, 2, 0);
        return row;
    }

    private void AddTopic()
    {
        if (SelectedTopicField is not { Id: > 0 } field)
            return;
        var label = PromptForText("トピックを追加", "トピック名", "");
        if (label is null)
            return;
        if (AppServices.Topics.ListTopics(field.Id).Any(t => t.Label == label))
        {
            MessageBox.Show(this, "同じ名前のトピックが既にあります。", "トピック", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppServices.Topics.AddTopic(field.Id, label);
        OnTopicColumnSelected();
    }

    // Rename via a topic row's ✏ button. Topic names need only be unique within their column (the same
    // label may exist under a different 自由記述 column), so the clash check is scoped to this field.
    private void RenameTopic(FieldTopic topic)
    {
        if (SelectedTopicField is not { Id: > 0 } field)
            return;
        var label = PromptForText("トピック名を変更", "トピック名", topic.Label);
        if (label is null || label == topic.Label)
            return;
        if (AppServices.Topics.ListTopics(field.Id).Any(t => t.Id != topic.Id && t.Label == label))
        {
            MessageBox.Show(this, "同じ名前のトピックが既にあります。", "トピック", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppServices.Topics.RenameTopic(topic.Id, label);
        OnTopicColumnSelected();
    }

    // Delete via a topic row's 削除 button.
    private void DeleteTopic(FieldTopic topic)
    {
        var answer = MessageBox.Show(this, $"トピック「{topic.Label}」を削除します。よろしいですか？", "トピックの削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;
        AppServices.Topics.DeleteTopic(topic.Id);
        OnTopicColumnSelected();
    }

    // Auto-generates the column's topic dictionary from its existing 自由記述 answers: embed → cluster →
    // name (LLM), replace the dictionary, then offer to re-assign existing responses. Needs an API key and
    // a couple of distinct answers; the progress dialog runs the (cancellable) LLM work.
    private void RebuildTopics()
    {
        if (SelectedTopicField is not { Id: > 0 } field)
            return;

        var settings = new SettingsViewModel(AppServices.Settings);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            MessageBox.Show(this, "トピックの自動生成には LLM の API キーが必要です。\n設定 → LLM で設定してください。",
                "トピックの再構築", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // The column's stored answers are the clustering input; a couple of distinct ones are needed.
        var answers = AppServices.Responses.LoadValuesForField(field.Id);
        if (answers.Distinct().Count() < 2)
        {
            MessageBox.Show(this, "再構築するには、この列に取り込まれた自由記述の回答が複数必要です。",
                "トピックの再構築", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existingCount = AppServices.Topics.ListTopics(field.Id).Count;
        var confirm = MessageBox.Show(this,
            $"「{ColumnLabel(field)}」の回答 {answers.Count} 件からトピックを自動生成します。"
                + (existingCount > 0 ? $"\n現在のトピック {existingCount} 件は置き換えられます。" : "")
                + "\n\n続けますか？",
            "トピックの再構築", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
            return;

        // Cluster + name with the progress dialog; the work delegate writes the result back to `built`.
        IReadOnlyList<TopicClusterer.Topic> built = Array.Empty<TopicClusterer.Topic>();
        var clusterer = new TopicClusterer(AppServices.Llm, settings.TopicModel);
        using (var dialog = new AnalyzeProgressForm(async (progress, ct) =>
                   built = await clusterer.BuildTopicsAsync(answers, progress, ct)))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
        }
        if (built.Count == 0)
        {
            MessageBox.Show(this, "トピックを生成できませんでした。", "トピックの再構築", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Replace the column's dictionary with the new labels + centroids, then refresh the list.
        AppServices.Topics.ReplaceTopics(field.Id, built.Select(t => (t.Label, (float[]?)t.Centroid)).ToList());
        OnTopicColumnSelected();

        OfferReassignExistingResponses();
    }

    // After rebuilding a column's topics, offer to route every existing response to the new dictionary.
    // Re-runs the import analyzer over the saved project (sentiment is served from the LLM cache, so this
    // is effectively just topic assignment) and rebuilds the star so the トピック別 view reflects it.
    private void OfferReassignExistingResponses()
    {
        if (_vm.EditingProjectId is not { } projectId)
            return;
        var answer = MessageBox.Show(this,
            "既存の回答を、新しいトピックに割り当て直しますか？",
            "トピックの再構築", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        if (AppServices.Projects.Load(projectId) is not { } project)
            return;

        var settings = new SettingsViewModel(AppServices.Settings);
        var analyzer = new ImportAnalyzer(AppServices.Llm, AppServices.Responses, AppServices.Topics, AppServices.AnalysisResults, settings.SentimentModel);
        using var dialog = new AnalyzeProgressForm((progress, ct) => analyzer.AnalyzeAsync(project, progress, ct));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AppServices.Analytics.Rebuild(project);
    }

    // A minimal single-line text prompt (WinForms has no built-in InputBox). Returns the trimmed text, or
    // null on cancel / empty. Built from layout containers with AutoSize buttons (and the app's DPI auto-
    // scale), so the OK / キャンセル text is never clipped on a high-DPI display.
    private string? PromptForText(string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = Theme.Font(),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F),
            BackColor = ColorTranslator.FromHtml("#F8FAFC"),
            ClientSize = new Size(LogicalToDeviceUnits(360), LogicalToDeviceUnits(150)),
        };

        var box = new TextBox { Text = initial, Font = Theme.Font(10f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 16, 16, 0), BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.BodyText, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        content.Controls.Add(box, 0, 1);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(9.5f, FontStyle.Bold), Padding = new Padding(16, 6, 16, 6), Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0), Cursor = Cursors.Hand };
        cancel.FlatAppearance.BorderColor = Theme.CardBorder;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Padding = new Padding(16, 8, 16, 12) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        form.Controls.Add(content);  // Fill — add first so it yields the bottom strip
        form.Controls.Add(buttons);  // Bottom
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
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
