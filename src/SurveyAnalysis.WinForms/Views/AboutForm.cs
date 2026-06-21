using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// A small modal "バージョン情報" (About) dialog: the app name and version, the MIT-license notice, a clickable
// link to the public repository, and the copyright line. Opened from the sidebar. The repository link opens in
// the user's default browser.
internal sealed class AboutForm : Form
{
    // The public repository, in one place so the link and the docs stay in step.
    // TODO: replace with the real URL once the public repository is created.
    public const string RepositoryUrl = "https://github.com/nakanokappei/SurveyAnalysis";

    public AboutForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "バージョン情報";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font();
        BackColor = Theme.ContentBack;
        ClientSize = new Size(LogicalToDeviceUnits(440), LogicalToDeviceUnits(240));
        Padding = new Padding(LogicalToDeviceUnits(20));

        // Text block (top-down). AutoSize labels stack with their own margins.
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = false, BackColor = Theme.ContentBack,
        };

        stack.Controls.Add(new Label
        {
            Text = Application.ProductName, AutoSize = true, ForeColor = Theme.TitleText,
            Font = Theme.Font(14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2),
        });
        stack.Controls.Add(new Label
        {
            Text = "SurveyAnalysis — バージョン " + VersionText(), AutoSize = true, ForeColor = Theme.Muted,
            Font = Theme.Font(9.5f), Margin = new Padding(0, 0, 0, 14),
        });
        stack.Controls.Add(new Label
        {
            Text = "このソフトウェアは MIT License の下で公開されています。", AutoSize = true,
            ForeColor = Theme.BodyText, Font = Theme.Font(10f), Margin = new Padding(0, 0, 0, 10),
        });

        // Repository row: a static label followed by a clickable link that opens the browser.
        var repoRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack, Margin = new Padding(0, 0, 0, 12),
        };
        repoRow.Controls.Add(new Label
        {
            Text = "リポジトリ:", AutoSize = true, ForeColor = Theme.BodyText, Font = Theme.Font(10f),
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0),
        });
        var repoLink = new LinkLabel
        {
            Text = RepositoryUrl, AutoSize = true, Font = Theme.Font(10f), LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Accent, Anchor = AnchorStyles.Left,
            Margin = Padding.Empty, Cursor = Cursors.Hand,
        };
        repoLink.LinkClicked += (_, _) => OpenUrl(RepositoryUrl);
        repoRow.Controls.Add(repoLink);
        stack.Controls.Add(repoRow);

        stack.Controls.Add(new Label
        {
            Text = CopyrightText(), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9f),
            Margin = new Padding(0, 0, 0, 0),
        });

        // Close button, bottom-right.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack,
        };
        var close = new Button
        {
            Text = "閉じる", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent,
            ForeColor = Color.White, Font = Theme.Font(10f), Cursor = Cursors.Hand,
            Padding = new Padding(18, 7, 18, 7),
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        bottom.Controls.Add(close);

        Controls.Add(stack);    // Fill (added first, behind)
        Controls.Add(bottom);   // Bottom
        AcceptButton = close;
        CancelButton = close;
    }

    // The informational product version without any build-metadata ("+sha") suffix.
    private static string VersionText()
    {
        var version = Application.ProductVersion;
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    // The copyright from the assembly metadata (set in the .csproj), with a literal fallback.
    private static string CopyrightText() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? "Copyright (c) 2026 Nakano Kappei";

    // Opens a URL in the default browser. Best-effort — a missing browser must not throw into the UI.
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore — opening the browser is best-effort
        }
    }
}
