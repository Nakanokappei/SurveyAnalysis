using System;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The welcome screen shown when no project is open — the WinForms counterpart of WelcomeView.axaml.
// A centered card lists previously saved projects (each reopenable) and offers the three entry points:
// create a project, create one from a CSV, or open the bundled sample. Buttons invoke the existing
// WelcomeViewModel commands, so all behaviour is shared with the (retiring) Avalonia front end.
internal sealed class WelcomeControl : UserControl
{
    private const int CardWidth = 400;

    public WelcomeControl(WelcomeViewModel vm)
    {
        // Created at runtime, so scale to the monitor DPI from a 96-dpi baseline (the buttons' fixed
        // widths/heights then grow with the font instead of clipping).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BackColor = Theme.ContentBack;

        // A single-cell outer panel centers the card; the card stacks its rows in one 400px column.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = Theme.ContentBack };
        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            BackColor = Theme.ContentBack,
        };
        // AutoSize column (not Absolute, which would not scale with DPI) so the card hugs the buttons
        // even after the DPI auto-scale widens them.
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        card.Controls.Add(new Label
        {
            Text = "プロジェクトを開始しましょう",
            Font = Theme.Font(20f, FontStyle.Bold),
            ForeColor = Theme.TitleText,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        card.Controls.Add(new Label
        {
            Text = "プロジェクトを作成すると、スキャン画像の読み込み・トピック割り当て・感情分析・月次レポートを管理できます。",
            Font = Theme.Font(9.5f),
            ForeColor = Theme.BodyText,
            AutoSize = true,
            MaximumSize = new Size(CardWidth, 0),
            Margin = new Padding(0, 0, 0, 14),
        });

        // 最近のプロジェクト: shown only when there are saved projects. Populated once, so a plain
        // per-item button is enough (no live collection binding needed here).
        if (vm.HasRecentProjects)
        {
            card.Controls.Add(new Label
            {
                Text = "最近のプロジェクト",
                Font = Theme.Font(8.5f, FontStyle.Bold),
                ForeColor = Theme.SectionHeader,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 4),
            });
            foreach (var summary in vm.RecentProjects)
                card.Controls.Add(RecentButton(vm, summary));
        }

        card.Controls.Add(PrimaryButton("＋ プロジェクトを作る", () => vm.CreateProjectCommand.Execute(null)));
        card.Controls.Add(OutlineButton("CSV からプロジェクトを作る", () => vm.CreateFromCsvCommand.Execute(null)));
        card.Controls.Add(LinkButton("サンプルプロジェクトを開く", () => vm.OpenSampleCommand.Execute(null)));

        root.Controls.Add(card, 0, 0);
        Controls.Add(root);
    }

    // Shared flat-button base; the variants below differ only in colour and height.
    private static Button BaseButton(string text, Action onClick, int height)
    {
        var button = new Button
        {
            Text = text,
            Width = CardWidth,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Font(10.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 0),
            TabStop = false,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // The primary call to action: solid accent fill.
    private static Button PrimaryButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick, 44);
        button.BackColor = Theme.Accent;
        button.ForeColor = Theme.AccentText;
        button.FlatAppearance.BorderSize = 0;
        button.Margin = new Padding(0, 10, 0, 0);
        return button;
    }

    // The secondary action: outlined in the accent colour.
    private static Button OutlineButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick, 40);
        button.BackColor = Color.White;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    // The tertiary action: link-like, no border or fill.
    private static Button LinkButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick, 32);
        button.BackColor = Theme.ContentBack;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 0;
        button.Font = Theme.Font(9.5f);
        return button;
    }

    // One saved project: name on top, field count and last-updated below, reopened on click.
    private static Button RecentButton(WelcomeViewModel vm, ProjectSummary summary)
    {
        var button = new Button
        {
            Text = $"{summary.Name}\n{summary.FieldCountDisplay}    {summary.UpdatedDisplay}",
            Width = CardWidth,
            Height = 56,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Theme.TitleText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(9.5f),
            Padding = new Padding(12, 0, 0, 0),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 6),
            TabStop = false,
        };
        button.FlatAppearance.BorderColor = Theme.CardBorder;
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => vm.OpenProjectCommand.Execute(summary);
        return button;
    }
}
