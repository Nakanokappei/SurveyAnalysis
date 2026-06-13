using System;
using System.Drawing;
using System.Windows.Forms;
using SurveyAnalysis.Models;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.WinForms;

// The welcome screen shown when no project is open — the WinForms counterpart of WelcomeView.axaml.
// A centered card lists previously saved projects (each reopenable) and offers the three entry points.
// Built entirely from layout containers (no explicit coordinates or sizes): a TableLayoutPanel centers
// the card, the card stacks its rows, and the buttons stretch to a uniform width via Anchor while their
// height comes from AutoSize + Padding — so spacing and DPI scaling are handled by the framework.
internal sealed class WelcomeControl : UserControl
{
    // The card's content width: a wrap boundary for the body text that also sets the card/button width.
    // A logical value the form's font auto-scale converts for the current DPI.
    private const int CardWidth = 360;

    public WelcomeControl(WelcomeViewModel vm)
    {
        // Pure layout-container UI, so the only DPI-sensitive values are the spacings and the wrap
        // width; a 96-dpi baseline lets the form's auto-scale convert them for the current monitor.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Dock = DockStyle.Fill;
        BackColor = Theme.ContentBack;

        // Single cell that centers the card both ways.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = Theme.ContentBack };

        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Anchor = AnchorStyles.None,
            BackColor = Theme.ContentBack,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        card.Controls.Add(Title("プロジェクトを開始しましょう"));
        card.Controls.Add(Body("プロジェクトを作成すると、スキャン画像の読み込み・トピック割り当て・感情分析・月次レポートを管理できます。"));

        if (vm.HasRecentProjects)
        {
            card.Controls.Add(SectionCaption("最近のプロジェクト"));
            foreach (var summary in vm.RecentProjects)
                card.Controls.Add(RecentButton(vm, summary));
        }

        card.Controls.Add(PrimaryButton("＋ プロジェクトを作る", () => vm.CreateProjectCommand.Execute(null)));
        card.Controls.Add(OutlineButton("CSV からプロジェクトを作る", () => vm.CreateFromCsvCommand.Execute(null)));
        card.Controls.Add(LinkButton("サンプルプロジェクトを開く", () => vm.OpenSampleCommand.Execute(null)));

        root.Controls.Add(card, 0, 0);
        Controls.Add(root);
    }

    private static Label Title(string text) => new()
    {
        Text = text,
        Font = Theme.Font(20f, FontStyle.Bold),
        ForeColor = Theme.TitleText,
        AutoSize = true,
        MaximumSize = new Size(CardWidth, 0),
        Margin = new Padding(0, 0, 0, 8),
    };

    private static Label Body(string text) => new()
    {
        Text = text,
        Font = Theme.Font(9.5f),
        ForeColor = Theme.BodyText,
        AutoSize = true,
        MaximumSize = new Size(CardWidth, 0),
        Margin = new Padding(0, 0, 0, 16),
    };

    private static Label SectionCaption(string text) => new()
    {
        Text = text,
        Font = Theme.Font(8.5f, FontStyle.Bold),
        ForeColor = Theme.SectionHeader,
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4),
    };

    // Full-width button: Anchor stretches it to the card width; AutoSize + Padding set its height.
    private static Button BaseButton(string text, Action onClick) =>
        WithClick(new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Font(10.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12, 9, 12, 9),
            Margin = new Padding(0, 4, 0, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        }, onClick);

    private static Button PrimaryButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
        button.BackColor = Theme.Accent;
        button.ForeColor = Theme.AccentText;
        button.FlatAppearance.BorderSize = 0;
        button.Margin = new Padding(0, 10, 0, 0);
        return button;
    }

    private static Button OutlineButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
        button.BackColor = Color.White;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static Button LinkButton(string text, Action onClick)
    {
        var button = BaseButton(text, onClick);
        button.BackColor = Theme.ContentBack;
        button.ForeColor = Theme.Accent;
        button.FlatAppearance.BorderSize = 0;
        button.Font = Theme.Font(9.5f);
        return button;
    }

    // One saved project: name on top, field count and last-updated below, reopened on click.
    private static Button RecentButton(WelcomeViewModel vm, ProjectSummary summary)
    {
        var button = WithClick(new Button
        {
            Text = $"{summary.Name}\n{summary.FieldCountDisplay}    {summary.UpdatedDisplay}",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Theme.TitleText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(9.5f),
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            TabStop = false,
        }, () => vm.OpenProjectCommand.Execute(summary));
        button.FlatAppearance.BorderColor = Theme.CardBorder;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static Button WithClick(Button button, Action onClick)
    {
        button.Click += (_, _) => onClick();
        return button;
    }
}
