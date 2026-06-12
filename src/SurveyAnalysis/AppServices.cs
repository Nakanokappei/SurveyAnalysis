using SurveyAnalysis.Data;

namespace SurveyAnalysis;

// The composition root. A desktop app this small does not need a DI container; this single static
// wiring point builds the database and repositories once and hands them out. The shell and the
// settings dialog both reach their repositories through here (the running app via App.axaml.cs, the
// XAML design-time DataContext via MainWindowViewModel's parameterless constructor).
public static class AppServices
{
    private static readonly AppDatabase Database = CreateDatabase();

    public static readonly ProjectRepository Projects = new(Database);
    public static readonly SettingsRepository Settings = new(Database);
    public static readonly ResponseRepository Responses = new(Database);
    public static readonly AnalyticsRepository Analytics = new(Database);

    private static AppDatabase CreateDatabase()
    {
        var database = AppDatabase.Default();
        database.EnsureSchema();
        return database;
    }
}
