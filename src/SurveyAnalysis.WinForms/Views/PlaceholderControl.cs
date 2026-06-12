using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// Shown for a page whose WinForms view has not been migrated yet. Names the screen so navigation is
// still legible during the port; replaced by the real control as each phase lands.
internal sealed class PlaceholderControl : UserControl
{
    public PlaceholderControl(ViewModelBase page)
    {
        BackColor = Theme.ContentBack;
        var screen = page.GetType().Name.Replace("ViewModel", "");
        Controls.Add(new Label
        {
            Text = $"「{screen}」画面は WinForms 版に移植中です。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.BodyText,
            Font = Theme.Font(12f),
        });
    }
}
