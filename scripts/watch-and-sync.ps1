param(
    [int]$QuietSeconds = 20,
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

Write-Host "Watching source changes in $repoRoot"
Write-Host "Ignored files from .gitignore will not be uploaded."
Write-Host "Press Ctrl+C to stop."

$lastStatus = ""
$lastChange = Get-Date

while ($true) {
    $status = (git status --porcelain) -join "`n"

    if ([string]::IsNullOrWhiteSpace($status)) {
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
        $lastStatus = ""
        $lastChange = Get-Date
    }

    Start-Sleep -Seconds 2
}
