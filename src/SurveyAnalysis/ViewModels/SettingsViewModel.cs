using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SurveyAnalysis.ViewModels;

// Backing model for the tabbed settings dialog (全般 / メール / LLM). Modern-Windows style:
// changes apply live (this prototype keeps them in memory only), there is no save/cancel,
// and the single "デフォルトに戻す" action resets every field.
public partial class SettingsViewModel : ViewModelBase
{
    // ===== 全般 (General) =====

    // 会社名（月次レポートのヘッダー等に表示する）
    [ObservableProperty]
    private string _companyName = DefaultCompanyName;

    // 画像の読み取りフォルダのパス
    [ObservableProperty]
    private string _scanFolderPath = DefaultScanFolderPath;

    // 読み取り後にサブフォルダ「アーカイブ」へ移動して二重読み取りを防ぐ
    [ObservableProperty]
    private bool _archiveAfterScan = true;

    // アーカイブ先サブフォルダ名
    [ObservableProperty]
    private string _archiveSubfolderName = DefaultArchiveSubfolderName;

    // ===== メール (Email) =====

    // 差出人
    [ObservableProperty]
    private string _mailFrom = DefaultMailFrom;

    // 宛先
    [ObservableProperty]
    private string _mailTo = DefaultMailTo;

    // メールサーバー種別（Gmail / SMTP）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGmail))]
    [NotifyPropertyChangedFor(nameof(IsSmtp))]
    private string _mailServerType = DefaultMailServerType;

    public ObservableCollection<string> MailServerOptions { get; } = new() { "Gmail", "SMTP" };

    public bool IsGmail => MailServerType == "Gmail";
    public bool IsSmtp => MailServerType == "SMTP";

    // Gmail 用
    [ObservableProperty]
    private string _gmailAddress = "";

    [ObservableProperty]
    private string _gmailAppPassword = "";

    // SMTP 用
    [ObservableProperty]
    private string _smtpHost = DefaultSmtpHost;

    [ObservableProperty]
    private string _smtpPort = DefaultSmtpPort;

    [ObservableProperty]
    private string _smtpUsername = "";

    [ObservableProperty]
    private string _smtpPassword = "";

    [ObservableProperty]
    private bool _smtpUseTls = true;

    // ===== LLM =====

    // APIキー（OpenAI）
    [ObservableProperty]
    private string _apiKey = "";

    // エンドポイント
    [ObservableProperty]
    private string _endpoint = DefaultEndpoint;

    // 用途別モデル（OpenAI）
    public ObservableCollection<string> ModelOptions { get; } = new()
    {
        "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "o4-mini"
    };

    [ObservableProperty]
    private string _ocrModel = DefaultOcrModel;

    [ObservableProperty]
    private string _topicModel = DefaultTopicModel;

    [ObservableProperty]
    private string _sentimentModel = DefaultSentimentModel;

    [ObservableProperty]
    private string _reportModel = DefaultReportModel;

    // 読み取りフォルダを参照（プロトタイプでは未実装）
    [RelayCommand]
    private void BrowseFolder()
    {
        // Folder picker is not wired in this prototype.
    }

    // デフォルトに戻す（全タブの値を初期値へ）
    [RelayCommand]
    private void ResetToDefaults()
    {
        CompanyName = DefaultCompanyName;
        ScanFolderPath = DefaultScanFolderPath;
        ArchiveAfterScan = true;
        ArchiveSubfolderName = DefaultArchiveSubfolderName;

        MailFrom = DefaultMailFrom;
        MailTo = DefaultMailTo;
        MailServerType = DefaultMailServerType;
        GmailAddress = "";
        GmailAppPassword = "";
        SmtpHost = DefaultSmtpHost;
        SmtpPort = DefaultSmtpPort;
        SmtpUsername = "";
        SmtpPassword = "";
        SmtpUseTls = true;

        ApiKey = "";
        Endpoint = DefaultEndpoint;
        OcrModel = DefaultOcrModel;
        TopicModel = DefaultTopicModel;
        SentimentModel = DefaultSentimentModel;
        ReportModel = DefaultReportModel;
    }

    // Default values, kept in one place so reset and field initializers stay in sync.
    private const string DefaultCompanyName = "○○ケーブル株式会社";
    // 既定はユーザーの書類フォルダ（Windows は「ドキュメント」、Mac は ~/Documents）。
    private static readonly string DefaultScanFolderPath =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
    private const string DefaultArchiveSubfolderName = "アーカイブ";
    private const string DefaultMailFrom = "no-reply@example.co.jp";
    private const string DefaultMailTo = "support@example.co.jp";
    private const string DefaultMailServerType = "Gmail";
    private const string DefaultSmtpHost = "smtp.example.co.jp";
    private const string DefaultSmtpPort = "587";
    private const string DefaultEndpoint = "https://api.openai.com/v1";
    private const string DefaultOcrModel = "gpt-4o";
    private const string DefaultTopicModel = "gpt-4o-mini";
    private const string DefaultSentimentModel = "gpt-4o-mini";
    private const string DefaultReportModel = "gpt-4o";
}
