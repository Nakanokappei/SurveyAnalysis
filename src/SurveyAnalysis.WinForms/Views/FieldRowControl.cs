using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// One survey-field row in the project design dialog — the WinForms counterpart of the field card in
// ProjectDesignView.axaml. Holds 項目名 / データ型 / 分析方法, the 🔒 PII badge, the date-aggregation
// checkboxes, and (for sentiment) the alert toggle and threshold. It edits its DataField live and
// reflows when the field's computed flags change (type → PII/date, analysis → sentiment): the optional
// sub-blocks toggle Visible and the AutoSize layout grows/shrinks the card. No manual coordinates —
// only layout containers (TableLayoutPanel / FlowLayoutPanel + Anchor) and a Paint-drawn soft border.
internal sealed class FieldRowControl : Panel
{
    private readonly DataField _field;
    private readonly TextBox _name = new() { Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };
    private readonly EnumCombo<FieldType> _type;
    private readonly EnumCombo<AnalysisMethod> _analysis;
    private readonly Label _pii = new()
    {
        Text = "🔒 暗号化・非表示",
        AutoSize = true,
        BackColor = ColorTranslator.FromHtml("#FEF3C7"),
        ForeColor = ColorTranslator.FromHtml("#92400E"),
        Font = Theme.Font(8.5f),
        Padding = new Padding(6, 3, 6, 3),
        Margin = new Padding(0, 0, 0, 6),
    };
    private readonly CheckBox _useForAggregation = new() { Text = "月次集計に使う", AutoSize = true, Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 0, 2) };
    private readonly CheckBox _useLoadDate = new() { Text = "読み込み日をデフォルトにする", AutoSize = true, Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 0, 2) };
    private readonly CheckBox _enableAlert = new() { Text = "アラートを発報する", AutoSize = true, Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 4) };
    private readonly TrackBar _threshold = new() { Minimum = -9, Maximum = 5, TickFrequency = 1, Width = 220, AutoSize = false, Height = 32, Anchor = AnchorStyles.Left };
    private readonly Label _thresholdValue = new() { AutoSize = true, ForeColor = Theme.Danger, Font = Theme.Font(9.5f, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 0) };

    // Optional sub-blocks, toggled by the field's computed flags; AutoSize lets the card reflow.
    private readonly FlowLayoutPanel _typeExtras;   // PII badge + date checkboxes, under データ型
    private readonly TableLayoutPanel _sentiment;   // alert toggle + threshold, full width
    private readonly TableLayoutPanel _thresholdRow;

    private bool _syncing;

    public FieldRowControl(DataField field, Action<DataField> onRemove)
    {
        _field = field;

        // The card stretches to its parent column (Anchor) and sizes its height to the visible content.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Color.White;
        Margin = new Padding(0, 0, 0, 12);
        ResizeRedraw = true;   // keep the painted border crisp as the card reflows

        _type = new EnumCombo<FieldType>(FieldTypeInfo.Label, v => _field.FieldType = v) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };
        _analysis = new EnumCombo<AnalysisMethod>(FieldTypeInfo.Label, v => _field.Analysis = v) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0) };

        // Top line: captions over their inputs in three percentage columns, 削除 in a trailing AutoSize
        // column (sized to the button first, so the inputs share the remaining width 40/30/30).
        var top = new TableLayoutPanel
        {
            ColumnCount = 4, RowCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Controls.Add(Caption("項目名"), 0, 0);
        top.Controls.Add(Caption("データ型"), 1, 0);
        top.Controls.Add(Caption("分析方法"), 2, 0);
        top.Controls.Add(_name, 0, 1);
        top.Controls.Add(_type, 1, 1);
        top.Controls.Add(_analysis, 2, 1);

        var remove = new Button
        {
            Text = "削除", AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Danger, BackColor = Color.White, Font = Theme.Font(9f),
            Cursor = Cursors.Hand, Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 2, 0, 0), Padding = new Padding(10, 3, 10, 3),
        };
        remove.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FCA5A5");
        remove.Click += (_, _) => onRemove(_field);
        top.Controls.Add(remove, 3, 1);

        // Optional block under データ型: PII badge then the two date-aggregation checkboxes.
        _typeExtras = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0),
        };
        _typeExtras.Controls.Add(_pii);
        _typeExtras.Controls.Add(_useForAggregation);
        _typeExtras.Controls.Add(_useLoadDate);

        // Threshold row: caption | slider | value | trailing note, each vertically centred (Anchor=Left
        // with no Top/Bottom centres within the slider-tall AutoSize row).
        _thresholdRow = new TableLayoutPanel
        {
            ColumnCount = 4, RowCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left, Margin = Padding.Empty,
        };
        for (var i = 0; i < 4; i++)
            _thresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _thresholdRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _thresholdRow.Controls.Add(new Label { Text = "アラート閾値", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        _thresholdRow.Controls.Add(_threshold, 1, 0);
        _thresholdRow.Controls.Add(_thresholdValue, 2, 0);
        _thresholdRow.Controls.Add(new Label { Text = "を下回ると担当者へ通知", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) }, 3, 0);

        // Sentiment alert block: the toggle, then (conditionally) the threshold row beneath it.
        _sentiment = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0),
        };
        _sentiment.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sentiment.Controls.Add(_enableAlert);
        _sentiment.Controls.Add(_thresholdRow);

        // Root stack inside the card: top line, then the two optional sub-blocks. Dock=Top + AutoSize
        // so the card (AutoSize) grows to the content and reflows when a sub-block toggles.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14), BackColor = Color.White,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(_typeExtras, 0, 1);
        root.Controls.Add(_sentiment, 0, 2);
        Controls.Add(root);

        // Initial values + visibility.
        _name.Text = _field.Name;
        _type.SelectValue(_field.FieldType);
        _analysis.SelectValue(_field.Analysis);
        SyncCheckboxes();
        SyncThreshold();
        SyncVisibility();

        // Edits flow back to the field.
        _name.TextChanged += (_, _) => { if (!_syncing) _field.Name = _name.Text; };
        _useForAggregation.CheckedChanged += (_, _) => { if (!_syncing) _field.UseForAggregation = _useForAggregation.Checked; };
        _useLoadDate.CheckedChanged += (_, _) => { if (!_syncing) _field.UseLoadDateAsDefault = _useLoadDate.Checked; };
        _enableAlert.CheckedChanged += (_, _) => { if (!_syncing) _field.EnableAlert = _enableAlert.Checked; };
        _threshold.Scroll += (_, _) => { if (!_syncing) _field.AlertThreshold = _threshold.Value / 10.0; };

        _field.PropertyChanged += OnFieldChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _field.PropertyChanged -= OnFieldChanged;
        base.Dispose(disposing);
    }

    // Soft card border (#E2E8F0) instead of BorderStyle.FixedSingle, matching the dashboard cards.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        var rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        e.Graphics.DrawRectangle(pen, rect);
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.Font(8.5f),
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 0, 0),
    };

    // Reflects field-driven flags onto the optional controls (combos re-select, checkboxes/threshold
    // resync), then updates which sub-blocks are visible so the AutoSize card reflows.
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
            case nameof(DataField.AlertThreshold):
                SyncThreshold();
                break;
        }
        SyncVisibility();
    }

    private void SyncCheckboxes()
    {
        _syncing = true;
        _useForAggregation.Checked = _field.UseForAggregation;
        _useLoadDate.Checked = _field.UseLoadDateAsDefault;
        _useLoadDate.Enabled = _field.UseLoadDateAsDefaultEnabled;
        _enableAlert.Checked = _field.EnableAlert;
        _syncing = false;
    }

    private void SyncThreshold()
    {
        _syncing = true;
        _threshold.Value = Math.Clamp((int)Math.Round(_field.AlertThreshold * 10), _threshold.Minimum, _threshold.Maximum);
        _thresholdValue.Text = _field.AlertThreshold.ToString("0.0");
        _syncing = false;
    }

    // Shows the PII / date block under データ型 and the sentiment block per the field's computed flags;
    // hidden sub-blocks collapse their AutoSize rows so the card shrinks to fit.
    private void SyncVisibility()
    {
        _pii.Visible = _field.IsPersonalInformation;
        _useForAggregation.Visible = _field.IsDate;
        _useLoadDate.Visible = _field.IsDate;
        _typeExtras.Visible = _field.IsPersonalInformation || _field.IsDate;

        _enableAlert.Visible = _field.IsSentimentSelected;
        _thresholdRow.Visible = _field.IsSentimentSelected && _field.ShowThreshold;
        _sentiment.Visible = _field.IsSentimentSelected;
    }
}
