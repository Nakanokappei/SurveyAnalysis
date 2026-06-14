using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// The dashboard's 対象期間 picker popup — a Google-Analytics-style dropdown: presets on the left (当日 /
// 昨日 / 直近7日 / 直近30日 / 直近60日 / カスタム期間) and a two-month calendar on the right for a custom
// [from, to] range (the end is capped at today). Picking a preset fills the calendar; editing the
// calendar switches the selection to カスタム. 適用 commits the choice via the Applied event; clicking
// outside the popup or キャンセル closes it with no change (so it behaves like a normal dropdown).
internal sealed class DateRangePopup : Form
{
    public event Action<DateRangePreset, DateTime, DateTime>? Applied;

    private DateRangePreset _preset;
    private DateTime _from;
    private DateTime _to;
    private bool _syncing;

    private readonly MonthCalendar _calendar = new()
    {
        CalendarDimensions = new Size(2, 1),
        MaxSelectionCount = 1000,
        MaxDate = DateTime.Today,
        ShowToday = false,
    };
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
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // presets
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // calendar
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Presets (left): one flat button per preset; clicking one fills the calendar and highlights it.
        var presets = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = new Padding(0, 0, 10, 0) };
        foreach (var preset in DateRangePresetInfo.All)
        {
            var button = new Button
            {
                Text = DateRangePresetInfo.Label(preset),
                AutoSize = false,
                Width = 130,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Theme.TitleText,
                Font = Theme.Font(9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
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

        _calendar.Margin = new Padding(0);
        _calendar.DateSelected += (_, _) => OnCalendarChanged();

        // Action bar (bottom, spans both columns): 適用 (commit) and キャンセル, right-aligned.
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 10, 0, 0) };
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
    // keeps whatever the calendar currently shows.
    private void OnPresetClicked(DateRangePreset preset)
    {
        _preset = preset;
        if (DateRangePresetInfo.Range(preset, DateTime.Today) is { } range)
        {
            _from = range.From;
            _to = range.To;
            SyncCalendar();
        }
        Highlight(preset);
    }

    // Editing the calendar switches the selection to カスタム.
    private void OnCalendarChanged()
    {
        if (_syncing)
            return;
        _from = _calendar.SelectionStart.Date;
        _to = _calendar.SelectionEnd.Date;
        _preset = DateRangePreset.Custom;
        Highlight(_preset);
    }

    // Reflects [_from, _to] onto the calendar without re-triggering OnCalendarChanged.
    private void SyncCalendar()
    {
        _syncing = true;
        var from = _from > DateTime.Today ? DateTime.Today : _from;
        var to = _to > DateTime.Today ? DateTime.Today : _to;
        _calendar.SelectionRange = new SelectionRange(from, to);
        _syncing = false;
    }

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

    // Shows the popup just below the anchor, left-aligned to it and clamped to the screen work area.
    public void ShowBelow(Control anchor)
    {
        var below = anchor.PointToScreen(new Point(0, anchor.Height + 2));
        var size = PreferredSize;
        var screen = Screen.FromControl(anchor).WorkingArea;
        var x = Math.Max(screen.Left + 4, Math.Min(below.X, screen.Right - size.Width - 4));
        var y = Math.Max(screen.Top + 4, Math.Min(below.Y, screen.Bottom - size.Height - 4));
        Location = new Point(x, y);
        Show(anchor.FindForm());
        Activate();
    }
}
