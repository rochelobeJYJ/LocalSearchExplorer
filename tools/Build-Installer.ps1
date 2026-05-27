param(
    [string]$Root = (Join-Path $PSScriptRoot ".."),
    [string]$CompilerPath = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = [System.IO.Path]::GetFullPath($Root)
$versionPath = Join-Path $rootPath "version.json"
$projectPath = Join-Path $rootPath "src\LocalSearch.App\LocalSearch.App.csproj"
$publishDir = Join-Path $rootPath "artifacts\publish\win-x64"
$installerScript = Join-Path $rootPath "installer\LocalSearchExplorer.iss"
$issIncludePath = Join-Path $rootPath "installer\version.iss.inc"
$installerOutputDir = Join-Path $rootPath "artifacts\installer"

function Get-StringValue {
    param(
        [object]$Value,
        [string]$Default = ""
    )

    if ($null -eq $Value) {
        return $Default
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $Default
    }

    return $text.Trim()
}

function Escape-InnoValue {
    param([string]$Value)
    return ($Value -replace '"', '""')
}

if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "version.json not found: $versionPath"
}
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "App project not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $installerScript)) {
    throw "Installer script not found: $installerScript"
}

$versionInfo = Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$version = Get-StringValue -Value $versionInfo.version
$releaseTag = Get-StringValue -Value $versionInfo.releaseTag -Default "v$version"
$appId = Get-StringValue -Value $versionInfo.appId -Default "localsearchexplorer"
$githubRepo = Get-StringValue -Value $versionInfo.githubRepo
$installerBaseName = Get-StringValue -Value $versionInfo.installerBaseName -Default "LocalSearchExplorer-Setup"

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "version.json must contain version."
}

$installerOutputBaseName = "$installerBaseName-$version"
$issIncludeContent = @"
#define MyAppVersion "$(Escape-InnoValue $version)"
#define MyReleaseTag "$(Escape-InnoValue $releaseTag)"
#define MyAppId "$(Escape-InnoValue $appId)"
#define MyGithubRepo "$(Escape-InnoValue $githubRepo)"
#define MyInstallerBaseName "$(Escape-InnoValue $installerBaseName)"
#define MyInstallerOutputBaseName "$(Escape-InnoValue $installerOutputBaseName)"
"@
[System.IO.File]::WriteAllText($issIncludePath, $issIncludeContent, [System.Text.UTF8Encoding]::new($false))

if (-not $SkipPublish) {
    $env:DOTNET_CLI_HOME = Join-Path $rootPath ".dotnet_home"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:MSBUILDDISABLENODEREUSE = "1"

    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir -m:1 /nr:false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}

Copy-Item -LiteralPath $versionPath -Destination (Join-Path $publishDir "version.json") -Force

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates = @($command.Source) + $candidates
    }

    $CompilerPath = $candidates | Select-Object -First 1
}

if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw "ISCC.exe not found. Install Inno Setup 6 or pass -CompilerPath."
}

New-Item -ItemType Directory -Path $installerOutputDir -Force | Out-Null
& $CompilerPath "/Qp" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed."
}

$installerPath = Join-Path $installerOutputDir "$installerOutputBaseName.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Expected installer artifact was not found: $installerPath"
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$sha256Path = "$installerPath.sha256"
$sha256Content = "{0}  {1}" -f $hash.ToLowerInvariant(), ([System.IO.Path]::GetFileName($installerPath))
[System.IO.File]::WriteAllText($sha256Path, $sha256Content + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
[ordered]@{
    ok = $true
    version = $version
    installerPath = $installerPath
    sha256Path = $sha256Path
    sha256 = $hash
} | ConvertTo-Json -Depth 3
