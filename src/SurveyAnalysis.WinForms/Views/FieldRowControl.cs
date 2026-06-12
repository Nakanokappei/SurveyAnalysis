using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// One survey-field row in the project design dialog — the WinForms counterpart of the field card in
// ProjectDesignView.axaml. Holds 項目名 / データ型 / 分析方法, the 🔒 PII badge, the date-aggregation
// checkboxes, and (for sentiment) the alert toggle and threshold. It edits its DataField live and
// re-lays out when the field's computed flags change (type → PII/date, analysis → sentiment), so the
// row grows and shrinks with the options on show.
internal sealed class FieldRowControl : Panel
{
    private readonly DataField _field;
    private readonly TextBox _name = new() { Font = Theme.Font(9.5f) };
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
    };
    private readonly CheckBox _useForAggregation = new() { Text = "月次集計に使う", AutoSize = true, Font = Theme.Font(8.5f) };
    private readonly CheckBox _useLoadDate = new() { Text = "読み込み日をデフォルトにする", AutoSize = true, Font = Theme.Font(8.5f) };
    private readonly CheckBox _enableAlert = new() { Text = "アラートを発報する", AutoSize = true, Font = Theme.Font(9.5f) };
    private readonly Panel _thresholdPanel = new() { Height = 28 };
    private readonly TrackBar _threshold = new() { Minimum = -9, Maximum = 5, TickFrequency = 1, Width = 220, Height = 28 };
    private readonly Label _thresholdValue = new() { AutoSize = true, ForeColor = Theme.Danger, Font = Theme.Font(9.5f, FontStyle.Bold) };
    private readonly Button _remove = new() { Text = "削除", Width = 76, Height = 28, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Danger, BackColor = Color.White, Font = Theme.Font(9f), Cursor = Cursors.Hand };

    private bool _syncing;

    public FieldRowControl(DataField field, Action<DataField> onRemove)
    {
        _field = field;
        BackColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;
        Margin = new Padding(0, 0, 0, 12);

        _type = new EnumCombo<FieldType>(FieldTypeInfo.Label, v => _field.FieldType = v);
        _analysis = new EnumCombo<AnalysisMethod>(FieldTypeInfo.Label, v => _field.Analysis = v);

        var nameLabel = Caption("項目名");
        var typeLabel = Caption("データ型");
        var analysisLabel = Caption("分析方法");

        // Threshold row: label + slider + value + trailing note.
        var thLabel = new Label { Text = "アラート閾値", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(0, 6) };
        _threshold.Location = new Point(74, 0);
        _thresholdValue.Location = new Point(300, 6);
        var thNote = new Label { Text = "を下回ると担当者へ通知", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Location = new Point(340, 6) };
        _thresholdPanel.Controls.AddRange(new Control[] { thLabel, _threshold, _thresholdValue, thNote });

        Controls.AddRange(new Control[]
        {
            nameLabel, _name, typeLabel, _type, analysisLabel, _analysis,
            _pii, _useForAggregation, _useLoadDate, _enableAlert, _thresholdPanel, _remove,
        });

        // Tag the captions so RelayoutRow can position them with their inputs.
        nameLabel.Tag = _name; typeLabel.Tag = _type; analysisLabel.Tag = _analysis;
        _captionForName = nameLabel; _captionForType = typeLabel; _captionForAnalysis = analysisLabel;

        // Initial values.
        _name.Text = _field.Name;
        _type.SelectValue(_field.FieldType);
        _analysis.SelectValue(_field.Analysis);
        SyncCheckboxes();
        SyncThreshold();

        // Edits flow back to the field.
        _name.TextChanged += (_, _) => { if (!_syncing) _field.Name = _name.Text; };
        _useForAggregation.CheckedChanged += (_, _) => { if (!_syncing) _field.UseForAggregation = _useForAggregation.Checked; };
        _useLoadDate.CheckedChanged += (_, _) => { if (!_syncing) _field.UseLoadDateAsDefault = _useLoadDate.Checked; };
        _enableAlert.CheckedChanged += (_, _) => { if (!_syncing) _field.EnableAlert = _enableAlert.Checked; };
        _threshold.Scroll += (_, _) => { if (!_syncing) _field.AlertThreshold = _threshold.Value / 10.0; };
        _remove.Click += (_, _) => onRemove(_field);
        _remove.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FCA5A5");

        _field.PropertyChanged += OnFieldChanged;
        Resize += (_, _) => RelayoutRow();
        RelayoutRow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _field.PropertyChanged -= OnFieldChanged;
        base.Dispose(disposing);
    }

    private readonly Label _captionForName;
    private readonly Label _captionForType;
    private readonly Label _captionForAnalysis;

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.Font(8.5f),
    };

    // Reflects field-driven flags onto the optional controls' visibility/enabled state, then re-lays
    // out (which also resizes this row so the parent list re-flows).
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
        RelayoutRow();
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

    // Positions the controls for the current width and visible options, and sets this row's height.
    private void RelayoutRow()
    {
        const int pad = 14, gap = 12, ctrlH = 26, top = 10, deleteW = 76;
        var usable = Math.Max(360, Width - pad * 2 - deleteW - gap);
        var nameW = (int)(usable * 0.40);
        var typeW = (int)(usable * 0.29);
        var analysisW = usable - nameW - typeW - gap * 2;
        var nameX = pad;
        var typeX = nameX + nameW + gap;
        var analysisX = typeX + typeW + gap;
        var inputY = top + 18;

        Place(_captionForName, nameX, top);
        _name.SetBounds(nameX, inputY, nameW, ctrlH);
        Place(_captionForType, typeX, top);
        _type.SetBounds(typeX, inputY, typeW, ctrlH);
        Place(_captionForAnalysis, analysisX, top);
        _analysis.SetBounds(analysisX, inputY, analysisW, ctrlH);
        _remove.Location = new Point(Width - pad - deleteW, inputY);

        // Optional block under データ型.
        var pii = _field.IsPersonalInformation;
        var date = _field.IsDate;
        var yc = inputY + ctrlH + 6;
        _pii.Visible = pii;
        if (pii) { _pii.Location = new Point(typeX, yc); yc += _pii.Height + 4; }
        _useForAggregation.Visible = date;
        _useLoadDate.Visible = date;
        if (date)
        {
            _useForAggregation.Location = new Point(typeX, yc); yc += 22;
            _useLoadDate.Location = new Point(typeX, yc); yc += 22;
        }

        // Sentiment alert block (full width, below everything else).
        var sentiment = _field.IsSentimentSelected;
        var ys = yc;
        _enableAlert.Visible = sentiment;
        _thresholdPanel.Visible = sentiment && _field.ShowThreshold;
        if (sentiment)
        {
            _enableAlert.Location = new Point(nameX, ys); ys += 26;
            if (_field.ShowThreshold)
            {
                _thresholdPanel.SetBounds(nameX, ys, usable, 28); ys += 30;
            }
        }

        Height = Math.Max(inputY + ctrlH + 12, ys) + 8;
    }

    private static void Place(Label caption, int x, int y) => caption.Location = new Point(x, y);
}
