using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SurveyAnalysis.Data;

namespace SurveyAnalysis.ViewModels;

// Backing model for the tabbed settings dialog (全般 / メール / LLM). Modern-Windows style:
// changes apply live, there is no save/cancel, and the single "デフォルトに戻す" action resets
// every field. Values persist to the settings table: they are loaded when the dialog opens and
// written back when it closes (the dialog host calls Save).
public partial class SettingsViewModel : ViewModelBase
{
    // Storage backing this dialog. Null at design time, where the dialog shows defaults only.
    private readonly SettingsRepository? _store;

    // Parameterless overload for the XAML design-time DataContext (no persistence).
    public SettingsViewModel() { }

    public SettingsViewModel(SettingsRepository store)
    {
        _store = store;
        Load();
    }

    // ===== 全般 (General) =====

    // 会社名（月次レポートのヘッダー等に表示する）
    [ObservableProperty]
    private string _companyName = DefaultCompanyName;

    // 画像の読み取りフォルダのパス（画像取り込み時に前回の場所を既定として記憶する）
    [ObservableProperty]
    private string _scanFolderPath = DefaultScanFolderPath;

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

    // ===== 埋め込み =====

    // 埋め込みモデル（接続はチャットと共通の Endpoint / ApiKey を使う）
    [ObservableProperty]
    private string _embeddingModel = DefaultEmbeddingModel;

    public ObservableCollection<string> EmbeddingModelOptions { get; } = new()
    {
        "text-embedding-3-small", "text-embedding-3-large", "text-embedding-ada-002"
    };

    // 並列実行数（同時リクエスト上限）
    [ObservableProperty]
    private string _llmConcurrency = DefaultLlmConcurrency;

    // 埋め込み1リクエストあたりの件数
    [ObservableProperty]
    private string _llmEmbeddingBatchSize = DefaultLlmEmbeddingBatchSize;

    // 1リクエストのタイムアウト（秒）
    [ObservableProperty]
    private string _llmRequestTimeoutSeconds = DefaultLlmRequestTimeoutSeconds;

    // ===== データベース =====

    // 起動時にデータベースを自動で最適化する（既定オン）
    [ObservableProperty]
    private bool _autoOptimizeOnStartup = DefaultAutoOptimizeOnStartup;

    // バックアップを残す世代（保持ポリシー）。値は BackupRetention の名前（Off/Few/Standard/Many）。
    [ObservableProperty]
    private string _backupRetention = DefaultBackupRetention;

    // デフォルトに戻す（全タブの値を初期値へ）
    [RelayCommand]
    private void ResetToDefaults()
    {
        // Only fields that have a real default are restored. Fields with no initial value — 会社名,
        // 差出人, 宛先, API キー, and the Gmail / SMTP credentials — are user-supplied, so reset must
        // leave whatever the user entered untouched.
        ScanFolderPath = DefaultScanFolderPath;

        MailServerType = DefaultMailServerType;
        SmtpHost = DefaultSmtpHost;
        SmtpPort = DefaultSmtpPort;
        SmtpUseTls = true;

        Endpoint = DefaultEndpoint;
        OcrModel = DefaultOcrModel;
        TopicModel = DefaultTopicModel;
        SentimentModel = DefaultSentimentModel;
        ReportModel = DefaultReportModel;

        EmbeddingModel = DefaultEmbeddingModel;
        LlmConcurrency = DefaultLlmConcurrency;
        LlmEmbeddingBatchSize = DefaultLlmEmbeddingBatchSize;
        LlmRequestTimeoutSeconds = DefaultLlmRequestTimeoutSeconds;

        AutoOptimizeOnStartup = DefaultAutoOptimizeOnStartup;
        BackupRetention = DefaultBackupRetention;
    }

    // Loads persisted values over the field defaults. Secret fields are stored protected, so they
    // are unprotected here. Missing keys keep their default (set by the field initializers).
    private void Load()
    {
        var values = _store!.LoadAll();

        CompanyName = Get(values, KeyCompanyName, DefaultCompanyName);
        ScanFolderPath = Get(values, KeyScanFolderPath, DefaultScanFolderPath);

        MailFrom = Get(values, KeyMailFrom, DefaultMailFrom);
        MailTo = Get(values, KeyMailTo, DefaultMailTo);
        MailServerType = Get(values, KeyMailServerType, DefaultMailServerType);
        GmailAddress = Get(values, KeyGmailAddress, "");
        GmailAppPassword = SecretProtector.Unprotect(Get(values, KeyGmailAppPassword, ""));
        SmtpHost = Get(values, KeySmtpHost, DefaultSmtpHost);
        SmtpPort = Get(values, KeySmtpPort, DefaultSmtpPort);
        SmtpUsername = Get(values, KeySmtpUsername, "");
        SmtpPassword = SecretProtector.Unprotect(Get(values, KeySmtpPassword, ""));
        SmtpUseTls = GetBool(values, KeySmtpUseTls, true);

        ApiKey = SecretProtector.Unprotect(Get(values, KeyApiKey, ""));
        Endpoint = Get(values, KeyEndpoint, DefaultEndpoint);
        OcrModel = Get(values, KeyOcrModel, DefaultOcrModel);
        TopicModel = Get(values, KeyTopicModel, DefaultTopicModel);
        SentimentModel = Get(values, KeySentimentModel, DefaultSentimentModel);
        ReportModel = Get(values, KeyReportModel, DefaultReportModel);

        EmbeddingModel = Get(values, KeyEmbeddingModel, DefaultEmbeddingModel);
        LlmConcurrency = Get(values, KeyLlmConcurrency, DefaultLlmConcurrency);
        LlmEmbeddingBatchSize = Get(values, KeyLlmEmbeddingBatchSize, DefaultLlmEmbeddingBatchSize);
        LlmRequestTimeoutSeconds = Get(values, KeyLlmRequestTimeoutSeconds, DefaultLlmRequestTimeoutSeconds);

        AutoOptimizeOnStartup = GetBool(values, KeyAutoOptimizeOnStartup, DefaultAutoOptimizeOnStartup);
        BackupRetention = Get(values, KeyBackupRetention, DefaultBackupRetention);
    }

    // Persists every field. Called by the dialog host when the settings window closes. Secret
    // fields are protected before storage. No-op at design time (no store).
    public void Save()
    {
        if (_store is null)
            return;

        _store.Save(new Dictionary<string, string>
        {
            [KeyCompanyName] = CompanyName,
            [KeyScanFolderPath] = ScanFolderPath,

            [KeyMailFrom] = MailFrom,
            [KeyMailTo] = MailTo,
            [KeyMailServerType] = MailServerType,
            [KeyGmailAddress] = GmailAddress,
            [KeyGmailAppPassword] = SecretProtector.Protect(GmailAppPassword),
            [KeySmtpHost] = SmtpHost,
            [KeySmtpPort] = SmtpPort,
            [KeySmtpUsername] = SmtpUsername,
            [KeySmtpPassword] = SecretProtector.Protect(SmtpPassword),
            [KeySmtpUseTls] = Bool(SmtpUseTls),

            [KeyApiKey] = SecretProtector.Protect(ApiKey),
            [KeyEndpoint] = Endpoint,
            [KeyOcrModel] = OcrModel,
            [KeyTopicModel] = TopicModel,
            [KeySentimentModel] = SentimentModel,
            [KeyReportModel] = ReportModel,

            [KeyEmbeddingModel] = EmbeddingModel,
            [KeyLlmConcurrency] = LlmConcurrency,
            [KeyLlmEmbeddingBatchSize] = LlmEmbeddingBatchSize,
            [KeyLlmRequestTimeoutSeconds] = LlmRequestTimeoutSeconds,

            [KeyAutoOptimizeOnStartup] = Bool(AutoOptimizeOnStartup),
            [KeyBackupRetention] = BackupRetention,
        });
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) ? value : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) ? value == "true" : fallback;

    private static string Bool(bool value) => value ? "true" : "false";

    // Settings storage keys (one per field).
    private const string KeyCompanyName = "CompanyName";
    private const string KeyScanFolderPath = "ScanFolderPath";
    private const string KeyMailFrom = "MailFrom";
    private const string KeyMailTo = "MailTo";
    private const string KeyMailServerType = "MailServerType";
    private const string KeyGmailAddress = "GmailAddress";
    private const string KeyGmailAppPassword = "GmailAppPassword";
    private const string KeySmtpHost = "SmtpHost";
    private const string KeySmtpPort = "SmtpPort";
    private const string KeySmtpUsername = "SmtpUsername";
    private const string KeySmtpPassword = "SmtpPassword";
    private const string KeySmtpUseTls = "SmtpUseTls";
    private const string KeyApiKey = "ApiKey";
    private const string KeyEndpoint = "Endpoint";
    private const string KeyOcrModel = "OcrModel";
    private const string KeyTopicModel = "TopicModel";
    private const string KeySentimentModel = "SentimentModel";
    private const string KeyReportModel = "ReportModel";
    private const string KeyEmbeddingModel = "EmbeddingModel";
    private const string KeyLlmConcurrency = "LlmConcurrency";
    private const string KeyLlmEmbeddingBatchSize = "LlmEmbeddingBatchSize";
    private const string KeyLlmRequestTimeoutSeconds = "LlmRequestTimeoutSeconds";
    private const string KeyAutoOptimizeOnStartup = "AutoOptimizeOnStartup";
    private const string KeyBackupRetention = "BackupRetention";

    // Default values, kept in one place so reset and field initializers stay in sync.
    private const string DefaultCompanyName = "";
    // 既定はユーザーの書類フォルダ（Windows は「ドキュメント」、Mac は ~/Documents）。
    private static readonly string DefaultScanFolderPath =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
    private const string DefaultMailFrom = "";
    private const string DefaultMailTo = "";
    private const string DefaultMailServerType = "Gmail";
    private const string DefaultSmtpHost = "smtp.example.co.jp";
    private const string DefaultSmtpPort = "587";
    private const string DefaultEndpoint = "https://api.openai.com/v1";
    private const string DefaultOcrModel = "gpt-4o";
    private const string DefaultTopicModel = "gpt-4o-mini";
    private const string DefaultSentimentModel = "gpt-4o-mini";
    private const string DefaultReportModel = "gpt-4o";
    private const string DefaultEmbeddingModel = "text-embedding-3-small";
    private const string DefaultLlmConcurrency = "4";
    private const string DefaultLlmEmbeddingBatchSize = "64";
    private const string DefaultLlmRequestTimeoutSeconds = "100";
    private const bool DefaultAutoOptimizeOnStartup = true;
    private const string DefaultBackupRetention = "Standard";
}
