param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\LocalSearch.App\bin\Debug\net8.0-windows\LocalSearch.App.exe'),
    [int]$StartupTimeoutSeconds = 45,
    [int]$SearchTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exeFullPath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exeFullPath)) {
    throw "Executable not found: $exeFullPath"
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("lse-smoke-" + [guid]::NewGuid().ToString('N'))
$fileName = 'regression_probe_unique.txt'
$searchTerm = 'regression_probe_unique'
New-Item -ItemType Directory -Path $root -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root $fileName) -Value 'LocalSearchExplorer regression smoke content' -Encoding UTF8

$process = Start-Process -FilePath $exeFullPath -ArgumentList @('--root', $root) -PassThru
try {
    $window = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)

    while ([DateTime]::UtcNow -lt $deadline) {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        if ($window) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $window) { throw 'Main window was not found.' }

    function Find-ByAutomationId {
        param([string]$AutomationId)
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)
        return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    $searchBox = Find-ByAutomationId 'SearchBox'
    $searchButton = Find-ByAutomationId 'SearchButton'
    $grid = Find-ByAutomationId 'ResultsGrid'
    if (-not $searchBox) { throw 'SearchBox not found.' }
    if (-not $searchButton) { throw 'SearchButton not found.' }
    if (-not $grid) { throw 'ResultsGrid not found.' }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $searchButton.Current.IsEnabled) {
        Start-Sleep -Milliseconds 500
    }
    if (-not $searchButton.Current.IsEnabled) {
        throw 'SearchButton did not become enabled after startup indexing.'
    }

    $valuePattern = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($searchTerm)
    $invokePattern = $searchButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()

    $found = $false
    $deadline = [DateTime]::UtcNow.AddSeconds($SearchTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $all = $grid.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            if (($all[$i].Current.Name -as [string]) -like "*$fileName*") {
                $found = $true
                break
            }
        }
        if ($found) { break }
        Start-Sleep -Milliseconds 500
    }

    $result = [ordered]@{
        ok = $found
        processId = $process.Id
        root = $root
        searched = $searchTerm
        expectedFile = $fileName
    }
    $result | ConvertTo-Json -Compress
    if (-not $found) {
        throw 'Expected search result was not visible in ResultsGrid.'
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}