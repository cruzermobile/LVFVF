param(
    [string]$Message = "",
    [string]$Branch = "",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"

$repoRoot = git rev-parse --show-toplevel 2>$null
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "This folder is not a Git repository yet. Run git init first."
}

Set-Location $repoRoot

git add -A
$staged = git diff --cached --name-only
if ([string]::IsNullOrWhiteSpace($staged)) {
    Write-Host "No source changes to sync."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "Auto sync $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}

git commit -m $Message

if ($NoPush) {
    Write-Host "Committed locally. Push skipped."
    exit 0
}

$remote = git remote get-url origin 2>$null
if ([string]::IsNullOrWhiteSpace($remote)) {
    Write-Host "Committed locally, but no GitHub remote is configured yet."
    Write-Host "Create an empty GitHub repo, then run:"
    Write-Host "  git remote add origin <repo-url>"
    Write-Host "  git push -u origin main"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = git branch --show-current
}

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = "main"
}

git push -u origin $Branch
