using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// One survey-field row, laid out as a single row of an 8-column table (the project design dialog shows
// the data items as a table instead of per-field cards). The columns are shared with the header row in
// ProjectDesignForm via DefineColumns, so everything lines up. 月次集計 / 読み込み日 only apply to 日付
// fields and are disabled otherwise; the 暗号化 cell shows 🔒 only for personal-information types. Edits
// flow straight to the DataField; the row re-syncs when the field's computed flags change.
internal sealed class FieldRowControl : TableLayoutPanel
{
    private readonly DataField _field;
    private readonly TextBox _name = new() { Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(2, 0, 2, 0) };
    private readonly EnumCombo<FieldType> _type;
    private readonly EnumCombo<AnalysisMethod> _analysis;
    private readonly CheckBox _useForAggregation = NewCheck();
    private readonly CheckBox _useLoadDate = NewCheck();
    private readonly Label _pii = new() { Text = "🔒", AutoSize = true, ForeColor = ColorTranslator.FromHtml("#92400E"), Font = Theme.Font(11f), Anchor = AnchorStyles.None, Margin = Padding.Empty };

    private bool _syncing;

    // The 8 shared columns: #, 項目名, データ型, 分析方法, 月次集計, 読み込み日, 暗号化, 削除. Width 0 = the
    // flexible (Percent) column (項目名); the rest are fixed pixel widths sized to their content (the
    // combos and the 削除 button). Used by both the header row (ProjectDesignForm) and every field row so
    // they line up. Pixel widths — this dialog renders unscaled, like the rest of the app.
    public static readonly int[] ColumnWidths = { OrdinalColumnWidth(), 0, 176, 200, 96, 120, 96, 88 };

    // Width for the # column: enough for 3 half-width digits ("999"). The font renders at the real screen
    // DPI even though this dialog's layout is otherwise unscaled, so a hardcoded px width clips wider
    // numbers — the width is measured in that same render context instead. A few px of breathing room
    // keeps the centered number off the column edges.
    private static int OrdinalColumnWidth()
    {
        using var font = Theme.Font(9.5f);
        return TextRenderer.MeasureText("999", font).Width + 8;
    }

    public static void DefineColumns(TableLayoutPanel t)
    {
        t.ColumnCount = ColumnWidths.Length;
        t.ColumnStyles.Clear();
        foreach (var width in ColumnWidths)
            t.ColumnStyles.Add(width == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width));
    }

    public FieldRowControl(DataField field, int ordinal, Action<DataField> onRemove)
    {
        _field = field;

        DefineColumns(this);
        RowCount = 1;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Color.White;
        Margin = Padding.Empty;            // rows sit flush; a separator line is painted at the bottom
        Padding = new Padding(0, 4, 0, 4);
        ResizeRedraw = true;

        _type = new EnumCombo<FieldType>(FieldTypeInfo.Label, v => _field.FieldType = v) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(2, 0, 2, 0) };
        _analysis = new EnumCombo<AnalysisMethod>(FieldTypeInfo.Label, v => _field.Analysis = v) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(2, 0, 2, 0) };

        var ordinalLabel = new Label { Text = ordinal.ToString(), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Anchor = AnchorStyles.None, Margin = Padding.Empty };

        var remove = new Button
        {
            Text = "削除", AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Danger, BackColor = Color.White, Font = Theme.Font(9f),
            Cursor = Cursors.Hand, Anchor = AnchorStyles.None, Margin = Padding.Empty, Padding = new Padding(8, 2, 8, 2),
        };
        remove.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FCA5A5");
        remove.Click += (_, _) => onRemove(_field);

        // One cell per column.
        Controls.Add(ordinalLabel, 0, 0);
        Controls.Add(_name, 1, 0);
        Controls.Add(_type, 2, 0);
        Controls.Add(_analysis, 3, 0);
        Controls.Add(_useForAggregation, 4, 0);
        Controls.Add(_useLoadDate, 5, 0);
        Controls.Add(_pii, 6, 0);
        Controls.Add(remove, 7, 0);

        // Initial values.
        _name.Text = _field.Name;
        _type.SelectValue(_field.FieldType);
        _analysis.SelectValue(_field.Analysis);
        SyncCheckboxes();
        SyncEnabled();

        // Edits flow back to the field.
        _name.TextChanged += (_, _) => { if (!_syncing) _field.Name = _name.Text; };
        _useForAggregation.CheckedChanged += (_, _) => { if (!_syncing) _field.UseForAggregation = _useForAggregation.Checked; };
        _useLoadDate.CheckedChanged += (_, _) => { if (!_syncing) _field.UseLoadDateAsDefault = _useLoadDate.Checked; };

        _field.PropertyChanged += OnFieldChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _field.PropertyChanged -= OnFieldChanged;
        base.Dispose(disposing);
    }

    // A centered, label-less checkbox (the column header names it).
    private static CheckBox NewCheck() => new() { Text = "", AutoSize = true, Anchor = AnchorStyles.None, Margin = Padding.Empty };

    // A faint row separator at the bottom, so the rows read as a table.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DataField.FieldType):
                _syncing = true; _type.SelectValue(_field.FieldType); _syncing = false;
                break;
            case nameof(DataField.Analysis):
                _syncing = true; _analysis.SelectValue(_field.Analysis); _syncing = false;
                break;
            case nameof(DataField.UseForAggregation):
            case nameof(DataField.UseLoadDateAsDefault):
                SyncCheckboxes();
                break;
        }
        SyncEnabled();
    }

    private void SyncCheckboxes()
    {
        _syncing = true;
        _useForAggregation.Checked = _field.UseForAggregation;
        _useLoadDate.Checked = _field.UseLoadDateAsDefault;
        _syncing = false;
    }

    // Greys out the date-only cells when the field is not 日付, and shows the 🔒 cell only for
    // personal-information types — so every row keeps all columns while signalling what's active.
    private void SyncEnabled()
    {
        _useForAggregation.Enabled = _field.IsDate;
        _useLoadDate.Enabled = _field.IsDate && _field.UseLoadDateAsDefaultEnabled;
        _pii.Visible = _field.IsPersonalInformation;
    }
}
