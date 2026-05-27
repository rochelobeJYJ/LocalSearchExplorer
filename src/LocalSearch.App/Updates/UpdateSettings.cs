using System.IO;
using System.Reflection;
using System.Text.Json;

namespace LocalSearch.App.Updates;

public sealed class UpdateSettings
{
    public string AppId { get; init; } = "localsearchexplorer";
    public string Version { get; init; } = GetAssemblyVersion();
    public string ReleaseTag { get; init; } = string.Empty;
    public string GithubRepo { get; init; } = string.Empty;
    public string InstallerBaseName { get; init; } = "LocalSearchExplorer-Setup";

    public bool IsConfigured => TryGetGithubRepository(out _, out _);

    public static UpdateSettings LoadDefault()
    {
        var versionPath = Path.Combine(AppContext.BaseDirectory, "version.json");
        if (!File.Exists(versionPath))
        {
            return new UpdateSettings();
        }

        try
        {
            using var stream = File.OpenRead(versionPath);
            var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var defaults = new UpdateSettings();

            return new UpdateSettings
            {
                AppId = GetString(root, "appId", defaults.AppId),
                Version = GetString(root, "version", defaults.Version),
                ReleaseTag = GetString(root, "releaseTag", defaults.ReleaseTag),
                GithubRepo = GetString(root, "githubRepo", defaults.GithubRepo),
                InstallerBaseName = GetString(root, "installerBaseName", defaults.InstallerBaseName)
            };
        }
        catch
        {
            return new UpdateSettings();
        }
    }

    public bool TryGetGithubRepository(out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;

        var repo = GithubRepo.Trim().Trim('/');
        var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        owner = parts[0];
        repository = parts[1];
        return true;
    }

    private static string GetString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string GetAssemblyVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
