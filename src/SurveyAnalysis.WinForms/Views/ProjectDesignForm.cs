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
// view model. (The dummy OCR "テスト" panel is not ported — it produced placeholder data only.)
internal sealed class ProjectDesignForm : Form
{
    private readonly ProjectDesignViewModel _vm;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ColorTranslator.FromHtml("#F8FAFC"), Padding = new Padding(28) };
    private readonly FlowLayoutPanel _stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
    private readonly TextBox _projectName = new() { Font = Theme.Font(10f) };
    private readonly FlowLayoutPanel _fields = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
    private readonly Button _addField = new() { Text = "＋ 項目を追加", Height = 44, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.Accent, Font = Theme.Font(10f), Cursor = Cursors.Hand };
    private readonly Button _confirm = new() { Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(10f, FontStyle.Bold), Padding = new Padding(18, 0, 18, 0), AutoSize = true, Cursor = Cursors.Hand };
    private readonly Label _intro = new() { Text = "アンケート用紙の各項目に、データ型と分析方法を割り当てます。", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f) };
    private readonly Label _header = new() { Text = "データ項目", AutoSize = false, Height = 26, ForeColor = Theme.TitleText, Font = Theme.Font(13f, FontStyle.Bold) };
    private readonly Panel _nameCard = new() { Height = 76, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };

    // The confirmed project, or null if cancelled / closed.
    public Project? ResultProject { get; private set; }

    public ProjectDesignForm(ProjectDesignViewModel vm)
    {
        _vm = vm;
        Text = vm.DialogTitle;
        ClientSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 480);
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
        // Bottom action bar (always visible).
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = ColorTranslator.FromHtml("#F8FAFC") };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml("#F8FAFC"), ForeColor = Theme.BodyText, Font = Theme.Font(10f), Cursor = Cursors.Hand };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => _vm.CancelCommand.Execute(null);
        _confirm.FlatAppearance.BorderSize = 0;
        _addField.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
        // Right-align the two buttons.
        bar.Controls.Add(_confirm);
        bar.Controls.Add(cancel);
        bar.Resize += (_, _) =>
        {
            _confirm.Location = new Point(bar.Width - _confirm.Width - 28, (bar.Height - _confirm.Height) / 2);
            cancel.Location = new Point(_confirm.Left - cancel.Width - 12, (bar.Height - cancel.Height) / 2);
        };

        // Project name card content.
        _nameCard.Controls.Add(new Label { Text = "プロジェクト名", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(14, 12) });
        _projectName.Location = new Point(14, 34);
        _projectName.Height = 26;
        _nameCard.Controls.Add(_projectName);
        _nameCard.Resize += (_, _) => _projectName.Width = _nameCard.Width - 28;

        _stack.Controls.Add(_intro);
        _stack.Controls.Add(_nameCard);
        _stack.Controls.Add(_header);
        _stack.Controls.Add(_fields);
        _stack.Controls.Add(_addField);
        _content.Controls.Add(_stack);

        Controls.Add(_content);
        Controls.Add(bar);
        _content.Resize += (_, _) => SyncWidths();
        SyncWidths();
    }

    // Stretches the stacked sections (and each field row) to the content width.
    private void SyncWidths()
    {
        var width = _content.ClientSize.Width - _content.Padding.Horizontal;
        if (_content.VerticalScroll.Visible)
            width -= SystemInformation.VerticalScrollBarWidth;
        width = Math.Max(360, width);

        _intro.Width = width;
        _nameCard.Width = width;
        _header.Width = width;
        _fields.Width = width;
        _addField.Width = width;
        foreach (Control row in _fields.Controls)
            row.Width = width;
        foreach (Control section in new Control[] { _intro, _nameCard, _header, _addField })
            section.Margin = new Padding(0, 0, 0, 14);
    }

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildFields();

    // Rebuilds the field rows from the view model. Simple and cheap (a handful of fields), and it
    // keeps the add/remove logic in the view model.
    private void RebuildFields()
    {
        _fields.SuspendLayout();
        foreach (Control old in _fields.Controls)
            old.Dispose();
        _fields.Controls.Clear();
        var width = Math.Max(360, _fields.Width);
        foreach (var field in _vm.Fields)
        {
            var row = new FieldRowControl(field, f => _vm.RemoveFieldCommand.Execute(f)) { Width = width };
            _fields.Controls.Add(row);
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
