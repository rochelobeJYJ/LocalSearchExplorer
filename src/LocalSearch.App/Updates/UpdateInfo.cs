namespace LocalSearch.App.Updates;

public sealed record UpdateInfo(
    string LatestVersion,
    string ReleaseTag,
    Uri ReleasePageUrl,
    string AssetName,
    Uri AssetDownloadUrl,
    string Sha256AssetName,
    Uri Sha256DownloadUrl,
    DateTimeOffset? PublishedAt);

public sealed record UpdateCheckResult(
    bool IsConfigured,
    bool IsAvailable,
    string CurrentVersion,
    UpdateInfo? Update,
    string Message,
    bool IsError = false)
{
    public static UpdateCheckResult NotConfigured(string currentVersion)
    {
        return new UpdateCheckResult(
            false,
            false,
            currentVersion,
            null,
            "GitHub 저장소가 아직 설정되지 않았습니다.");
    }

    public static UpdateCheckResult UpToDate(string currentVersion)
    {
        return new UpdateCheckResult(
            true,
            false,
            currentVersion,
            null,
            "현재 최신 버전을 사용 중입니다.");
    }

    public static UpdateCheckResult Failed(string currentVersion, string message)
    {
        return new UpdateCheckResult(
            true,
            false,
            currentVersion,
            null,
            message,
            IsError: true);
    }
}
