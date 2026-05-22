param(
    [int]$QuietSeconds = 20,
    [int]$PullIntervalSeconds = 60,
    [string]$Branch = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = git rev-parse --show-toplevel 2>$null
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "This folder is not a Git repository yet. Run git init first."
}

Set-Location $repoRoot

$syncScript = Join-Path $repoRoot "scripts\sync-to-github.ps1"
if (-not (Test-Path -LiteralPath $syncScript)) {
    throw "Missing sync script: $syncScript"
}

$pullScript = Join-Path $repoRoot "scripts\pull-from-github.ps1"
if (-not (Test-Path -LiteralPath $pullScript)) {
    throw "Missing pull script: $pullScript"
}

Write-Host "Watching source changes in $repoRoot"
Write-Host "Ignored files from .gitignore will not be uploaded."
Write-Host "When the folder is clean, GitHub changes are downloaded every $PullIntervalSeconds seconds."
Write-Host "Press Ctrl+C to stop."

$lastStatus = ""
$lastChange = Get-Date
$lastPull = (Get-Date).AddSeconds(-$PullIntervalSeconds)

while ($true) {
    $status = (git status --porcelain) -join "`n"

    if ([string]::IsNullOrWhiteSpace($status)) {
        $readyToPull = $PullIntervalSeconds -gt 0 -and ((Get-Date) - $lastPull).TotalSeconds -ge $PullIntervalSeconds
        if ($readyToPull) {
            & $pullScript -Branch $Branch
            $lastPull = Get-Date
        }

        $lastStatus = ""
        Start-Sleep -Seconds 2
        continue
    }

    if ($status -ne $lastStatus) {
        $lastStatus = $status
        $lastChange = Get-Date
        Start-Sleep -Seconds 2
        continue
    }

    $quietFor = ((Get-Date) - $lastChange).TotalSeconds
    if ($quietFor -ge $QuietSeconds) {
        & $syncScript -Branch $Branch
        $lastPull = Get-Date
        $lastStatus = ""
        $lastChange = Get-Date
    }

    Start-Sleep -Seconds 2
}
