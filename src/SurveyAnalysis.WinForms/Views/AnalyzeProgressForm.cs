using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SurveyAnalysis.WinForms;

// A small modal that runs an asynchronous, per-item analysis job (sentiment / topic on import) while
// showing live n/total progress and a Cancel button. The work runs on the UI thread's async context so
// the message loop stays responsive and cancellation is immediate; LLM calls await off-thread. The
// dialog closes itself when the work finishes, is cancelled, or errors (a message is shown on error).
internal sealed class AnalyzeProgressForm : Form
{
    private readonly Func<IProgress<(int Done, int Total)>, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(10f), ForeColor = Theme.TitleText };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 1 };

    public AnalyzeProgressForm(Func<IProgress<(int Done, int Total)>, CancellationToken, Task> work)
    {
        _work = work;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "解析中";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;   // close only via Cancel or completion
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(LogicalToDeviceUnits(360), LogicalToDeviceUnits(108));
        Font = Theme.Font();
        BackColor = Theme.ContentBack;
        Padding = new Padding(LogicalToDeviceUnits(16));

        _status.Text = "解析の準備をしています…";

        var cancel = new Button
        {
            Text = "キャンセル", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White,
            ForeColor = Theme.BodyText, Font = Theme.Font(9.5f), Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right, Padding = new Padding(12, 4, 12, 4),
        };
        cancel.FlatAppearance.BorderColor = Theme.CardBorder;
        cancel.Click += (_, _) => { cancel.Enabled = false; _status.Text = "キャンセルしています…"; _cts.Cancel(); };
        var cancelRow = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(34), BackColor = Theme.ContentBack };
        cancelRow.Controls.Add(cancel);

        Controls.Add(new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(6), BackColor = Theme.ContentBack });
        Controls.Add(_bar);
        Controls.Add(_status);
        Controls.Add(cancelRow);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _bar.Maximum = Math.Max(1, p.Total);
            _bar.Value = Math.Min(p.Done, _bar.Maximum);
            _status.Text = $"解析中… {p.Done} / {p.Total}";
        });

        try
        {
            await _work(progress, _cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "解析中にエラーが発生しました。\n" + ex.Message, "解析", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Abort;
        }
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _cts.Dispose();
    }
}
