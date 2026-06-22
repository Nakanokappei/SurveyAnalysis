using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SurveyAnalysis.Data;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;

namespace SurveyAnalysis.WinForms;

// The image-OCR proofreading screen. Each scanned image was OCR'd into the staging table; here the user
// steps through them one at a time — the picture on the left, the read values (editable) on the right —
// and either 取り込む (build a response from the corrected values, insert it, drop the staging row) or
// 破棄 (drop the staging row without importing). Nothing reaches the responses table until 取り込む is
// pressed; whatever is left unreviewed stays staged. CommittedCount tells the host whether to run the
// import analysis afterwards.
internal sealed class ImageReviewForm : Form
{
    private readonly Project _project;
    private readonly IReadOnlyList<DataField> _fields;
    private readonly ResponseRepository _responses;
    private readonly ImageStagingRepository _staging;

    // The records still under review (committed/discarded ones are removed as we go), each with an
    // editable copy of its OCR values.
    private readonly List<ReviewItem> _items;
    private int _index;
    private Image? _currentImage;

    // How many records the user confirmed into the responses table (the host runs analysis if > 0).
    public int CommittedCount { get; private set; }

    private readonly Label _position = new() { AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(9.5f, FontStyle.Bold), Anchor = AnchorStyles.None, Margin = new Padding(8, 0, 8, 0) };
    private readonly Label _sourceName = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9f), Anchor = AnchorStyles.Left, Margin = new Padding(2, 5, 2, 6) };
    private readonly ZoomImageView _viewer = new() { Dock = DockStyle.Fill };
    private readonly Panel _fieldsHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly Label _zoomLabel = new() { AutoSize = true, Text = "100%", ForeColor = Theme.BodyText, Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.None, Margin = new Padding(6, 0, 6, 0), Cursor = Cursors.Hand };
    private readonly Button _prev;
    private readonly Button _next;
    private readonly Button _discard;
    private readonly Button _commit;
    private readonly Button _zoomIn;
    private readonly Button _zoomOut;
    private SplitContainer? _split;

    // Signatures (sorted 項目名→値) of the project's existing responses, so a 取り込む whose values exactly
    // match an existing row can warn the user (無視 / 追加). Grows as records are committed this session.
    private readonly HashSet<string> _existingSignatures = new();

    public ImageReviewForm(Project project, IReadOnlyList<StagedImage> staged, ResponseRepository responses, ImageStagingRepository staging)
    {
        _project = project;
        _fields = project.Fields.ToList();
        _responses = responses;
        _staging = staging;
        _items = staged.Select(s => new ReviewItem(s)).ToList();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ファイルから取り込む — 校正";
        MaximizeBox = true;
        // Golden-ratio-ish working area; never smaller than fits the split.
        ClientSize = new Size(LogicalToDeviceUnits(960), LogicalToDeviceUnits(620));
        MinimumSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(460));
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font();
        BackColor = Theme.ContentBack;

        _prev = ToolButton("← 前", () => Navigate(-1));
        _next = ToolButton("次 →", () => Navigate(+1));
        _discard = ActionButton("破棄", Color.White, Theme.BodyText, bold: false, onClick: DiscardCurrent);
        _discard.FlatAppearance.BorderColor = Theme.CardBorder;
        _commit = ActionButton("この内容で取り込む", Theme.Accent, Color.White, bold: true, onClick: CommitCurrent);

        _zoomOut = ToolButton("－", () => _viewer.ZoomOut());
        _zoomIn = ToolButton("＋", () => _viewer.ZoomIn());
        _zoomLabel.Click += (_, _) => _viewer.ResetZoom();
        _viewer.ZoomChanged += (_, _) => _zoomLabel.Text = _viewer.ZoomPercent + "%";

        // Existing responses' value signatures, for the duplicate warning on 取り込む.
        foreach (var values in responses.LoadForProject(project.Id))
            _existingSignatures.Add(SignatureOf(values));

        BuildLayout();
        RenderCurrent();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _currentImage?.Dispose();
    }

    // Positions the splitter and applies panel minimums now that the container has its real width (doing
    // this in the constructor throws, since the panel is not yet sized). Distance is set within the valid
    // range first, then the minimums are tightened only if the width can hold them.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_split is not { Width: > 0 } split)
            return;

        var width = split.Width;
        var distance = Math.Max(1, Math.Min((int)(width * 0.46), width - split.SplitterWidth - 1));
        split.SplitterDistance = distance;

        var min1 = LogicalToDeviceUnits(240);
        var min2 = LogicalToDeviceUnits(260);
        if (width > min1 + min2 + split.SplitterWidth)
        {
            split.Panel1MinSize = min1;
            split.Panel2MinSize = min2;
            split.SplitterDistance = Math.Clamp(distance, min1, width - min2 - split.SplitterWidth);
        }
    }

    private void BuildLayout()
    {
        // Top bar: title on the left, the record pager (前 / n of M / 次) on the right.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(12), LogicalToDeviceUnits(16), LogicalToDeviceUnits(8)), BackColor = Theme.ContentBack };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "画像と読み取り結果を見比べて校正してください", AutoSize = true, ForeColor = Theme.TitleText, Font = Theme.Font(11f, FontStyle.Bold), Anchor = AnchorStyles.Left }, 0, 0);
        var pager = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, BackColor = Theme.ContentBack };
        pager.Controls.AddRange(new Control[] { _prev, _position, _next });
        top.Controls.Add(pager, 1, 0);

        // Center: image (left) | editable values (right). Panel min sizes and the splitter position are
        // set in OnLoad once the panel has its real width — setting them in the constructor (before docking
        // sizes the container) throws "SplitterDistance must be between ...".
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = LogicalToDeviceUnits(6), BackColor = Theme.ContentBack };
        _split = split;
        split.Panel1.Padding = new Padding(LogicalToDeviceUnits(16), 0, LogicalToDeviceUnits(8), 0);
        split.Panel1.BackColor = Theme.ContentBack;
        var imageCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(LogicalToDeviceUnits(8)) };
        // Image header: source name (left) + zoom controls (－ [zoom%] ＋) on the right.
        var imgHeader = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White };
        imgHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        imgHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        imgHeader.Controls.Add(_sourceName, 0, 0);
        var zoomBar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, BackColor = Color.White };
        zoomBar.Controls.Add(_zoomOut);
        zoomBar.Controls.Add(_zoomLabel);
        zoomBar.Controls.Add(_zoomIn);
        imgHeader.Controls.Add(zoomBar, 1, 0);
        imageCard.Controls.Add(_viewer);     // Fill (behind)
        imageCard.Controls.Add(imgHeader);   // Top
        split.Panel1.Controls.Add(imageCard);

        split.Panel2.Padding = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(16), 0);
        split.Panel2.BackColor = Theme.ContentBack;
        var fieldsCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(LogicalToDeviceUnits(14)) };
        fieldsCard.Controls.Add(_fieldsHost);
        fieldsCard.Controls.Add(new Label { Text = "読み取り結果（編集して校正できます）", Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Padding = new Padding(2, 0, 2, 8) });
        split.Panel2.Controls.Add(fieldsCard);

        // Bottom bar: 破棄 (left) and 取り込む (right).
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(10), LogicalToDeviceUnits(16), LogicalToDeviceUnits(14)), BackColor = Theme.ContentBack };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, BackColor = Theme.ContentBack };
        left.Controls.Add(_discard);
        bottom.Controls.Add(left, 0, 0);
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, BackColor = Theme.ContentBack };
        right.Controls.Add(_commit);
        bottom.Controls.Add(right, 1, 0);

        // Docking add order: Fill first (behind), then the docked bars.
        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    // Renders the current record: pager text, source name, the image, and the editable field rows. Called
    // on construction and after every navigation / commit / discard.
    private void RenderCurrent()
    {
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Clamp(_index, 0, _items.Count - 1);
        var item = _items[_index];

        _position.Text = $"{_index + 1} / {_items.Count}";
        _sourceName.Text = item.Staged.SourceName;
        _prev.Enabled = _index > 0;
        _next.Enabled = _index < _items.Count - 1;

        // Swap the image (decoupled from the stream so the bytes can be freed).
        _currentImage?.Dispose();
        _currentImage = LoadImage(item.Staged.ImageBytes);
        _viewer.Image = _currentImage;

        RenderFields(item);
    }

    // Builds one editable row per project field (label + text box; 自由記述 gets a taller multiline box).
    // Edits write straight back into the item's value map, so navigating away and back keeps them.
    private void RenderFields(ReviewItem item)
    {
        _fieldsHost.SuspendLayout();
        foreach (Control old in _fieldsHost.Controls)
            old.Dispose();
        _fieldsHost.Controls.Clear();

        // Stack rows top-down; the host scrolls. Added in reverse so the first field ends up on top.
        foreach (var field in _fields.AsEnumerable().Reverse())
        {
            var multiline = field.FieldType == FieldType.FreeText;
            var row = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, LogicalToDeviceUnits(10)), BackColor = Color.White };

            var box = new TextBox
            {
                Dock = DockStyle.Top,
                Text = item.Values.TryGetValue(field.Name, out var v) ? v : "",
                Font = Theme.Font(10f),
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                Height = multiline ? LogicalToDeviceUnits(60) : LogicalToDeviceUnits(26),
            };
            var capturedName = field.Name;
            box.TextChanged += (_, _) => item.Values[capturedName] = box.Text;

            // The 🔒 marks a field whose value is personal information — a hint about how it is handled after
            // it is saved (display / analysis), not about OCR: every field, PII included, is read and shown.
            var caption = new Label
            {
                Text = field.Name + (FieldTypeInfo.IsPersonalInformation(field.FieldType) ? "　🔒" : ""),
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Theme.BarTrackText,
                Font = Theme.Font(9f, FontStyle.Bold),
                Padding = new Padding(0, 0, 0, 2),
            };

            row.Controls.Add(box);       // Top (lowest)
            // 選択肢型は複数選択を「;」区切りで 1 セルに持つ規約（ChoiceValues）だが、校正者は項目の
            // データ型を画面上で意識できない。そこで選択肢の欄に限り、入力規則を行内にそっと添える。
            if (field.FieldType == FieldType.Choice)
            {
                row.Controls.Add(new Label
                {
                    Text = $"複数選んでいる場合は「{ChoiceValues.Separator}」（半角）で区切ってください",
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    ForeColor = Theme.Muted,
                    Font = Theme.Font(8.5f),
                    Padding = new Padding(0, 2, 0, 0),
                });                      // Top (between box and caption)
            }
            row.Controls.Add(caption);   // Top (upper)
            _fieldsHost.Controls.Add(row);
            row.Dock = DockStyle.Top;
        }
        _fieldsHost.ResumeLayout();
        _fieldsHost.ScrollControlIntoView(_fieldsHost);
        _fieldsHost.AutoScrollPosition = new Point(0, 0);
    }

    // Navigates without committing; in-memory edits are retained on each item.
    private void Navigate(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, _items.Count - 1);
        RenderCurrent();
    }

    // 取り込む: build a response from the (edited) values and insert it, then drop the staging row.
    private void CommitCurrent()
    {
        var item = _items[_index];
        var response = OcrExtractor.BuildResponse(item.Values, _fields);
        if (response.Answers.Count == 0)
        {
            MessageBox.Show(this, "取り込む値がありません。不要な画像は「破棄」してください。", "校正",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Warn when this image's values exactly match a response already in the project.
        var signature = SignatureOf(response);
        if (_existingSignatures.Contains(signature))
        {
            switch (AskDuplicate(item.Staged.SourceName))
            {
                case DuplicateChoice.Cancel:
                    return;                            // stay on this record, decide later
                case DuplicateChoice.Skip:
                    _staging.Delete(item.Staged.Id);   // ignore the duplicate — do not import it
                    RemoveCurrentAndAdvance();
                    return;
            }
            // Add: fall through and import it anyway.
        }

        _responses.InsertResponses(_project.Id, "画像OCR", new[] { response });
        _existingSignatures.Add(signature);   // so a later identical scan in this batch also warns
        _staging.Delete(item.Staged.Id);
        CommittedCount++;
        RemoveCurrentAndAdvance();
    }

    // 取り込み済み回答との「まったく同じ値」判定キー: 空でない (項目名→値) を整列して連結したもの。
    private static string SignatureOf(SurveyResponse response) =>
        Signature(response.Answers.Where(a => !string.IsNullOrWhiteSpace(a.Value)).Select(a => (a.FieldName, a.Value)));

    private static string SignatureOf(IReadOnlyDictionary<string, string> values) =>
        Signature(values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => (kv.Key, kv.Value)));

    private static string Signature(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join("", pairs.Select(p => p.Name + "" + p.Value.Trim()).OrderBy(s => s, StringComparer.Ordinal));

    private enum DuplicateChoice { Add, Skip, Cancel }

    // Asks how to handle a record whose values duplicate an existing response: 追加 (import anyway),
    // 無視 (skip it), or キャンセル (go back). A small custom dialog so the buttons read clearly.
    private DuplicateChoice AskDuplicate(string sourceName)
    {
        var choice = DuplicateChoice.Cancel;
        using var dialog = new Form
        {
            Text = "重複の確認", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowIcon = false, StartPosition = FormStartPosition.CenterParent, Font = Theme.Font(), BackColor = Theme.ContentBack,
            ClientSize = new Size(LogicalToDeviceUnits(440), LogicalToDeviceUnits(148)), Padding = new Padding(LogicalToDeviceUnits(18)),
        };
        var message = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"「{sourceName}」と同じ内容の回答が、既にプロジェクト内にあります。\nどうしますか？",
            Font = Theme.Font(10f), ForeColor = Theme.BodyText, TextAlign = ContentAlignment.TopLeft,
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.ContentBack };
        Button Make(string text, DuplicateChoice value, bool accent)
        {
            var button = ActionButton(text, accent ? Theme.Accent : Color.White, accent ? Color.White : Theme.BodyText, accent,
                () => { choice = value; dialog.DialogResult = DialogResult.OK; dialog.Close(); });
            if (!accent) button.FlatAppearance.BorderColor = Theme.CardBorder;
            button.Margin = new Padding(6, 0, 0, 0);
            return button;
        }
        bar.Controls.Add(Make("追加で取り込む", DuplicateChoice.Add, accent: true));
        bar.Controls.Add(Make("無視（取り込まない）", DuplicateChoice.Skip, accent: false));
        bar.Controls.Add(Make("キャンセル", DuplicateChoice.Cancel, accent: false));
        dialog.Controls.Add(bar);       // Bottom
        dialog.Controls.Add(message);   // Fill (above the bar)
        dialog.ShowDialog(this);
        return choice;
    }

    // 破棄: drop the staging row without importing (after a confirm).
    private void DiscardCurrent()
    {
        var item = _items[_index];
        var confirm = MessageBox.Show(this, $"この画像（{item.Staged.SourceName}）の読み取り結果を破棄しますか？", "破棄",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
            return;
        _staging.Delete(item.Staged.Id);
        RemoveCurrentAndAdvance();
    }

    private void RemoveCurrentAndAdvance()
    {
        _items.RemoveAt(_index);
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        if (_index >= _items.Count)
            _index = _items.Count - 1;
        RenderCurrent();
    }

    // Decodes image bytes into a standalone Bitmap (copied off the stream so the source bytes / stream can
    // be released — Image.FromStream otherwise keeps the stream alive for the image's lifetime).
    private static Image LoadImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var decoded = Image.FromStream(ms);
            return new Bitmap(decoded);
        }
        catch
        {
            // Unsupported / corrupt image: a 1×1 placeholder keeps the review usable (values still edit).
            return new Bitmap(1, 1);
        }
    }

    private Button ToolButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardBorder,
            ForeColor = Theme.TitleText, Font = Theme.Font(9f), Cursor = Cursors.Hand,
            Padding = new Padding(8, 4, 8, 4), Margin = new Padding(2, 0, 2, 0), Anchor = AnchorStyles.None,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button ActionButton(string text, Color back, Color fore, bool bold, Action onClick)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Font = Theme.Font(10f, bold ? FontStyle.Bold : FontStyle.Regular), Cursor = Cursors.Hand,
            Padding = new Padding(18, 8, 18, 8), Anchor = AnchorStyles.None,
        };
        button.FlatAppearance.BorderSize = bold ? 0 : 1;
        button.Click += (_, _) => onClick();
        return button;
    }

    // One record under review: its staged row plus an editable copy of the OCR values.
    private sealed class ReviewItem
    {
        public StagedImage Staged { get; }
        public Dictionary<string, string> Values { get; }
        public ReviewItem(StagedImage staged)
        {
            Staged = staged;
            Values = new Dictionary<string, string>(staged.Values);
        }
    }
}
