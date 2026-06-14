using System;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// The reusable 対象期間 picker: a calendar-icon trigger button that opens DateRangePopup. Used by the
// dashboard and every 切り口. The host adds Trigger to its header, seeds it with SetCurrent, and handles
// RangeChanged (raised when the user applies a new range). The trigger's label tracks the current range.
internal sealed class DateRangePicker
{
    public event Action<DateRangePreset, DateTime, DateTime>? RangeChanged;

    public IconButton Trigger { get; }

    private DateRangePreset _preset = DateRangePreset.Last30Days;
    private DateTime _from = DateTime.Today;
    private DateTime _to = DateTime.Today;

    public DateRangePicker()
    {
        Trigger = new IconButton
        {
            Glyph = Icons.Calendar.Glyph,
            IconFontName = Icons.Calendar.Font,
            IconSize = 9.5f,
            AutoSize = true,
            BackColor = Color.White,
            ForeColor = Theme.TitleText,
            Font = Theme.Font(10f),
            Padding = new Padding(10, 6, 10, 6),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.None,
        };
        Trigger.FlatAppearance.BorderColor = Theme.CardBorder;
        Trigger.FlatAppearance.BorderSize = 1;
        Trigger.Click += (_, _) => Open();
    }

    // Seeds the trigger's range and label (no event raised) — called on initial layout from the VM.
    public void SetCurrent(DateRangePreset preset, DateTime from, DateTime to)
    {
        _preset = preset;
        _from = from;
        _to = to;
        Trigger.Text = Label();
    }

    private void Open()
    {
        var popup = new DateRangePopup(_preset, _from, _to);
        popup.Applied += (preset, from, to) =>
        {
            _preset = preset;
            _from = from;
            _to = to;
            Trigger.Text = Label();
            RangeChanged?.Invoke(preset, from, to);
        };
        popup.ShowBelow(Trigger);
    }

    private string Label() => _preset == DateRangePreset.Custom
        ? $"{_from:yyyy/MM/dd} 〜 {_to:yyyy/MM/dd}"
        : DateRangePresetInfo.Label(_preset);
}
