using System.Drawing;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// A ComboBox whose mouse wheel never silently changes the value. Scrolling over a closed drop-down is an
// easy mis-edit, so when the list is closed the wheel is forwarded to the nearest auto-scroll parent
// (scrolling the dialog) instead of cycling the selection; when the list is open the wheel scrolls the
// list as usual. Shared by every drop-down that sits in a scrolling form (the data-items rows and the
// CSV import preview).
internal class ScrollSafeComboBox : ComboBox
{
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
}
