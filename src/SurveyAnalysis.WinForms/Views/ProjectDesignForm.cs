using System;
using System.Collections.Specialized;
using System.Drawing;
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
    private readonly TableLayoutPanel _fields = NewStack();
    private readonly Button _addField = new() { Text = "＋ 項目を追加", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.Accent, Font = Theme.Font(10f), Padding = new Padding(0, 11, 0, 11), Margin = Padding.Empty, Cursor = Cursors.Hand };
    private readonly Button _confirm = new() { FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(10f, FontStyle.Bold), Padding = new Padding(18, 6, 18, 6), AutoSize = true, Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = new Padding(12, 0, 0, 0) };
    private readonly Label _intro = new() { Text = "アンケート用紙の各項目に、データ型と分析方法を割り当てます。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 14) };
    private readonly Label _header = new() { Text = "データ項目", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(13f, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 8) };

    // The confirmed project, or null if cancelled / closed.
    public Project? ResultProject { get; private set; }

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
        _confirm.Text = _vm.ConfirmLabel;
        _projectName.TextChanged += (_, _) => _vm.ProjectName = _projectName.Text;
        _addField.Click += (_, _) => _vm.AddFieldCommand.Execute(null);
        _confirm.Click += (_, _) => _vm.CreateProjectCommand.Execute(null);

        _vm.Fields.CollectionChanged += OnFieldsChanged;
        _vm.CreateProjectCommand.CanExecuteChanged += (_, _) => UpdateConfirmEnabled();
        _vm.Completed += OnCompleted;
        _vm.Cancelled += OnCancelled;

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
        }
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        _addField.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");

        // Project-name soft card: a caption over the input, both stretching to the card width.
        var nameCard = SoftCard();
        var nameInner = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14), BackColor = Color.White };
        nameInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameInner.Controls.Add(new Label { Text = "プロジェクト名", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = Padding.Empty });
        nameInner.Controls.Add(_projectName);
        nameCard.Controls.Add(nameInner);

        // Vertical stack of sections; each section is anchored to fill the content width.
        var stack = NewStack();
        AddSection(stack, _intro);
        AddSection(stack, nameCard);
        AddSection(stack, _header);
        AddSection(stack, BuildColumnHeader());
        AddSection(stack, _fields);
        AddSection(stack, _addField);
        _content.Controls.Add(stack);

        // Bottom action bar: a spacer column pushes キャンセル / 確定 to the right, vertically centred.
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Padding = new Padding(28, 10, 28, 10) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var cancel = new Button { Text = "キャンセル", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml("#F8FAFC"), ForeColor = Theme.BodyText, Font = Theme.Font(10f), Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Padding = new Padding(12, 6, 12, 6) };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => _vm.CancelCommand.Execute(null);
        _confirm.FlatAppearance.BorderSize = 0;
        bar.Controls.Add(cancel, 1, 0);
        bar.Controls.Add(_confirm, 2, 0);

        // Fill added first so it yields the bottom edge to the docked action bar.
        Controls.Add(_content);
        Controls.Add(bar);
    }

    // The table's column header: the 10 captions over the field rows, using the same shared columns so
    // they align. Captions wrap within their fixed-width columns; the long boolean headers therefore
    // stack onto a few lines.
    private static Control BuildColumnHeader()
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
            "読み込み日をデフォルトにする", "アラートを発報する", "アラート閾値", "暗号化・非表示", "削除",
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
}
