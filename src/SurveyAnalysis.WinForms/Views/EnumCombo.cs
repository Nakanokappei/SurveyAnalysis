using System;
using System.Drawing;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// A drop-down of enum values that displays a Japanese label per value (via a label function) while
// carrying the underlying value. WinForms combo items show their ToString(), so each value is wrapped
// with its label. Selecting an item reports the value; SelectValue picks the matching item.
internal sealed class EnumCombo<TEnum> : ComboBox where TEnum : struct, Enum
{
    private readonly Action<TEnum> _onPick;
    private bool _syncing;

    public EnumCombo(Func<TEnum, string> label, Action<TEnum> onPick)
    {
        _onPick = onPick;
        DropDownStyle = ComboBoxStyle.DropDownList;
        Font = Theme.Font(9.5f);
        foreach (var value in Enum.GetValues<TEnum>())
            Items.Add(new Item(value, label(value)));
        SelectedIndexChanged += OnPicked;
    }

    // The mouse wheel over a closed drop-down should scroll the dialog, not silently change the value
    // (an easy mis-edit). When the list is open the wheel scrolls the list as usual; when it is closed
    // the wheel is forwarded to the nearest auto-scroll parent instead.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (DroppedDown)
        {
            base.OnMouseWheel(e);
            return;
        }
        if (e is HandledMouseEventArgs handled)
            handled.Handled = true;
        var parent = Parent;
        while (parent != null && parent is not ScrollableControl { AutoScroll: true })
            parent = parent.Parent;
        if (parent is ScrollableControl panel)
            panel.AutoScrollPosition = new Point(-panel.AutoScrollPosition.X, -panel.AutoScrollPosition.Y - e.Delta);
    }

    public void SelectValue(TEnum value)
    {
        _syncing = true;
        for (var i = 0; i < Items.Count; i++)
            if (((Item)Items[i]!).Value.Equals(value))
            {
                SelectedIndex = i;
                break;
            }
        _syncing = false;
    }

    private void OnPicked(object? sender, EventArgs e)
    {
        if (!_syncing && SelectedItem is Item item)
            _onPick(item.Value);
    }

    private sealed record Item(TEnum Value, string Label)
    {
        public override string ToString() => Label;
    }
}
