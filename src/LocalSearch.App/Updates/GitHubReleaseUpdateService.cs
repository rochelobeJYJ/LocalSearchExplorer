using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalSearch.App.Updates;

public sealed class GitHubReleaseUpdateService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly UpdateSettings _settings;

    public GitHubReleaseUpdateService(UpdateSettings settings)
    {
        _settings = settings;
    }

    public string CurrentVersion => _settings.Version;
    public bool IsConfigured => _settings.IsConfigured;

    public async Task<UpdateCheckResult> CheckLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.TryGetGithubRepository(out var owner, out var repository))
        {
            return UpdateCheckResult.NotConfigured(CurrentVersion);
        }

        var endpoint = new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        try
        {
            using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var tag = GetString(root, "tag_name");
            var latestVersion = ExtractVersion(tag);
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                latestVersion = ExtractVersion(GetString(root, "name"));
            }

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                throw new InvalidOperationException("GitHub 최신 릴리즈에서 버전 정보를 읽을 수 없습니다.");
            }

            if (!IsNewerVersion(latestVersion, CurrentVersion, tag, _settings.ReleaseTag))
            {
                return UpdateCheckResult.UpToDate(CurrentVersion);
            }

            var asset = SelectInstallerAsset(root, latestVersion);
            if (asset is null)
            {
                throw new InvalidOperationException($"최신 릴리즈에서 예상 설치 파일을 찾을 수 없습니다: {_settings.InstallerBaseName}-{latestVersion}.exe");
            }

            var sha256Asset = SelectSha256Asset(root, asset.Value.Name);
            if (sha256Asset is null)
            {
                throw new InvalidOperationException($"최신 릴리즈에서 설치 파일 해시(.sha256)를 찾을 수 없습니다: {asset.Value.Name}.sha256");
            }

            var update = new UpdateInfo(
                latestVersion,
                tag,
                new Uri(GetString(root, "html_url")),
                asset.Value.Name,
                new Uri(asset.Value.DownloadUrl),
                sha256Asset.Value.Name,
                new Uri(sha256Asset.Value.DownloadUrl),
                GetDateTimeOffset(root, "published_at"));

            return new UpdateCheckResult(
                true,
                true,
                CurrentVersion,
                update,
                $"새 버전 {latestVersion}을 설치할 수 있습니다.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "업데이트 확인 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "업데이트 서버에 연결할 수 없습니다. 네트워크 상태나 GitHub 릴리즈 상태를 확인해 주세요.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "GitHub 릴리즈 정보를 읽을 수 없습니다. 잠시 후 다시 시도해 주세요.");
        }
        catch (UriFormatException)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "GitHub 릴리즈의 다운로드 주소 형식이 올바르지 않습니다.");
        }
        catch (InvalidOperationException ex)
        {
            return UpdateCheckResult.Failed(CurrentVersion, ex.Message);
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalSearchExplorer",
            "updates");
        Directory.CreateDirectory(updateDirectory);

        var safeName = MakeSafeFileName(update.AssetName);
        var targetPath = Path.Combine(updateDirectory, safeName);
        var tempPath = targetPath + ".part";

        CleanupUpdateDirectory(updateDirectory, safeName);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        try
        {
            using (var response = await HttpClient.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(tempPath);

                var buffer = new byte[81920];
                long receivedBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    receivedBytes += read;
                    if (totalBytes is > 0)
                    {
                        progress?.Report(receivedBytes / (double)totalBytes.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("업데이트 설치 파일을 내려받을 수 없습니다. 네트워크 상태나 GitHub 릴리즈 자산을 확인해 주세요.", ex);
        }

        try
        {
            var expectedHash = await DownloadExpectedSha256Async(update, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException("업데이트 설치 파일의 SHA256 해시가 릴리즈 해시와 일치하지 않습니다.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            File.Delete(tempPath);
            throw new InvalidOperationException("업데이트 해시 파일을 내려받을 수 없습니다. GitHub 릴리즈 자산을 확인해 주세요.", ex);
        }

        File.Move(tempPath, targetPath, overwrite: true);
        progress?.Report(1);
        return targetPath;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalSearchExplorer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private (string Name, string DownloadUrl)? SelectInstallerAsset(JsonElement release, string latestVersion)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var expectedName = $"{_settings.InstallerBaseName}-{latestVersion}.exe";
        var candidates = assets.EnumerateArray()
            .Select(asset => (
                Name: GetString(asset, "name"),
                DownloadUrl: GetString(asset, "browser_download_url")))
            .Where(asset =>
                !string.IsNullOrWhiteSpace(asset.Name) &&
                !string.IsNullOrWhiteSpace(asset.DownloadUrl) &&
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var first = candidates.FirstOrDefault();
        return string.IsNullOrWhiteSpace(first.Name) ? null : first;
    }

    private static (string Name, string DownloadUrl)? SelectSha256Asset(JsonElement release, string installerName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var expectedName = installerName + ".sha256";
        var match = assets.EnumerateArray()
            .Select(asset => (
                Name: GetString(asset, "name"),
                DownloadUrl: GetString(asset, "browser_download_url")))
            .FirstOrDefault(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(asset.DownloadUrl));

        return string.IsNullOrWhiteSpace(match.Name) ? null : match;
    }

    private static async Task<string> DownloadExpectedSha256Async(UpdateInfo update, CancellationToken cancellationToken)
    {
        var text = await HttpClient.GetStringAsync(update.Sha256DownloadUrl, cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(text, @"\b[0-9a-fA-F]{64}\b", RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success)
        {
            throw new InvalidOperationException($"업데이트 해시 파일을 읽을 수 없습니다: {update.Sha256AssetName}");
        }

        return match.Value.ToUpperInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion, string latestTag, string currentTag)
    {
        if (TryParseVersion(latestVersion, out var latest) && TryParseVersion(currentVersion, out var current))
        {
            return latest > current;
        }

        if (!string.IsNullOrWhiteSpace(currentTag))
        {
            return !string.Equals(latestTag, currentTag, StringComparison.OrdinalIgnoreCase);
        }

        return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = Regex.Match(text, @"\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success)
        {
            return false;
        }

        var parts = match.Value.Split('.');
        while (parts.Length < 3)
        {
            parts = [.. parts, "0"];
        }

        if (!Version.TryParse(string.Join('.', parts), out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static string ExtractVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(text, @"\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success ? match.Value : string.Empty;
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string name)
    {
        var text = GetString(root, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    private static string MakeSafeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "LocalSearchExplorer-Setup.exe" : cleaned;
    }

    private static void CleanupUpdateDirectory(string updateDirectory, string currentInstallerName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(updateDirectory))
            {
                var extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".sha256", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(file), currentInstallerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(file);
            }
        }
        catch
        {
            // Cleanup is best-effort; a stale installer must not block an update.
        }
    }
}
