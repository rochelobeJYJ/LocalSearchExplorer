namespace LocalSearch.Core.Defaults;

public static class LocalSearchPaths
{
    public static string GetDefaultDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "LocalSearchExplorer");
        return Path.Combine(directory, "index.db");
    }
}
