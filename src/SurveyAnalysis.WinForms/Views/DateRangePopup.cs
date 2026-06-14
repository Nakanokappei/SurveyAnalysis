using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// The 対象期間 picker popup — a Google-Analytics-style dropdown shared by the dashboard and the slices:
// presets on the left (当日 / 昨日 / 直近7日 / 直近30日 / 直近60日 / カスタム期間) with the current range shown
// beneath them, and a two-month calendar on the right for a custom [from, to] range (the end is capped at
// today). カスタム is picked GA-style: click the start day, then click the end day — the range highlights
// as you go (a drag also works). 適用 commits the choice via the Applied event; clicking outside or
// キャンセル closes it with no change, like a normal dropdown.
internal sealed class DateRangePopup : Form
{
    public event Action<DateRangePreset, DateTime, DateTime>? Applied;

    private DateRangePreset _preset;
    private DateTime _from;
    private DateTime _to;
    private bool _syncing;
    private DateTime? _pendingStart;  // the first click of a two-click custom range (null = next click starts one)

    private readonly MonthCalendar _calendar = new()
    {
        CalendarDimensions = new Size(2, 1),
        MaxSelectionCount = 4000,  // allow multi-year custom ranges
        MaxDate = DateTime.Today,
        ShowToday = false,
    };
    private readonly Label _rangeLabel = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9f), Margin = new Padding(2, 8, 0, 0) };
    private readonly Dictionary<DateRangePreset, Button> _presetButtons = new();

    public DateRangePopup(DateRangePreset preset, DateTime from, DateTime to)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _preset = preset;
        _from = from;
        _to = to;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();
        SyncCalendar();
        Highlight(_preset);
        UpdateRangeLabel();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // presets
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // calendar
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Presets (left): one flat button per preset, each at least 160 wide so the labels never clip,
        // then the current range as text beneath them.
        var presets = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 0, 12, 0) };
        foreach (var preset in DateRangePresetInfo.All)
        {
            var button = new Button
            {
                Text = DateRangePresetInfo.Label(preset),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(160, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Theme.TitleText,
                Font = Theme.Font(9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 2),
                TabStop = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.ContentBack;
            var captured = preset;
            button.Click += (_, _) => OnPresetClicked(captured);
            _presetButtons[preset] = button;
            presets.Controls.Add(button);
        }
        presets.Controls.Add(_rangeLabel);

        _calendar.Margin = new Padding(0);
        _calendar.DateSelected += (_, _) => OnCalendarChanged();

        // Action bar (bottom, spans both columns): 適用 (commit) and キャンセル, right-aligned.
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 12, 0, 0) };
        var apply = new Button { Text = "適用", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(9.5f, FontStyle.Bold), Padding = new Padding(16, 6, 16, 6), Cursor = Cursors.Hand, Margin = new Padding(6, 0, 0, 0) };
        apply.FlatAppearance.BorderSize = 0;
        apply.Click += (_, _) => { Applied?.Invoke(_preset, _from, _to); Close(); };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Padding = new Padding(12, 6, 12, 6), Cursor = Cursors.Hand, Margin = new Padding(0) };
        cancel.FlatAppearance.BorderColor = Theme.CardBorder;
        cancel.FlatAppearance.BorderSize = 1;
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);

        root.Controls.Add(presets, 0, 0);
        root.Controls.Add(_calendar, 1, 0);
        root.Controls.Add(actions, 0, 1);
        root.SetColumnSpan(actions, 2);
        Controls.Add(root);
    }

    // A preset click sets the range from the preset (anchored at today) and fills the calendar; カスタム
    // keeps the current range and waits for the user to pick start/end on the calendar.
    private void OnPresetClicked(DateRangePreset preset)
    {
        _pendingStart = null;
        _preset = preset;
        if (DateRangePresetInfo.Range(preset, DateTime.Today) is { } range)
        {
            _from = range.From;
            _to = range.To;
            SyncCalendar();
        }
        Highlight(preset);
        UpdateRangeLabel();
    }

    // Editing the calendar makes the selection カスタム. A drag yields the range directly; single clicks
    // build it GA-style — the first click is the start, the second click closes the range.
    private void OnCalendarChanged()
    {
        if (_syncing)
            return;

        var s = _calendar.SelectionStart.Date;
        var e = _calendar.SelectionEnd.Date;
        if (s != e)
        {
            _from = s;
            _to = e;
            _pendingStart = null;
        }
        else if (_pendingStart is null)
        {
            _pendingStart = s;
            _from = s;
            _to = s;
        }
        else
        {
            var start = _pendingStart.Value;
            _from = start <= s ? start : s;
            _to = start <= s ? s : start;
            _pendingStart = null;
        }

        _preset = DateRangePreset.Custom;
        SyncCalendar();
        Highlight(_preset);
        UpdateRangeLabel();
    }

    // Reflects [_from, _to] onto the calendar without re-triggering OnCalendarChanged.
    private void SyncCalendar()
    {
        _syncing = true;
        var from = _from.Date;
        var to = _to.Date;
        if (to > DateTime.Today)
            to = DateTime.Today;
        if (from > to)
            from = to;
        _calendar.SelectionRange = new SelectionRange(from, to);
        _syncing = false;
    }

    private void UpdateRangeLabel() => _rangeLabel.Text = $"{_from:yyyy/MM/dd} 〜 {_to:yyyy/MM/dd}";

    // Accent-fills the active preset button, resets the others.
    private void Highlight(DateRangePreset preset)
    {
        foreach (var (key, button) in _presetButtons)
        {
            var on = key == preset;
            button.BackColor = on ? Theme.Accent : Color.White;
            button.ForeColor = on ? Color.White : Theme.TitleText;
        }
    }

    // A 1px border around the otherwise-chromeless popup.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        e.Graphics.DrawRectangle(pen, r);
    }

    // Dismiss when focus leaves the popup (a click elsewhere), like a dropdown list.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    // Shows the popup just below the anchor, then clamps it to the screen using its real (laid-out) size.
    public void ShowBelow(Control anchor)
    {
        var below = anchor.PointToScreen(new Point(0, anchor.Height + 2));
        Location = below;
        Show(anchor.FindForm());
        var screen = Screen.FromControl(anchor).WorkingArea;
        var x = Math.Max(screen.Left + 4, Math.Min(below.X, screen.Right - Width - 4));
        var y = Math.Max(screen.Top + 4, Math.Min(below.Y, screen.Bottom - Height - 4));
        Location = new Point(x, y);
        Activate();
    }
}
