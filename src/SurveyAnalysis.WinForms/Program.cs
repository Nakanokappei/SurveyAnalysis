using System;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// Application entry point. Enables visual styles and per-monitor DPI awareness (so the UI stays crisp
// on the high-DPI Windows displays this app targets), then runs the shell form. ApplicationConfiguration
// .Initialize() is intentionally not used: that source generator only runs under UseWindowsForms, which
// is unavailable on the macOS build host, so the equivalent calls are made explicitly.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // QuestPDF (月次レポートの PDF 生成) is used under its free Community license.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }
}
