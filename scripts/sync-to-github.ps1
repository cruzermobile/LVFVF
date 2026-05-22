param(
    [string]$Message = "",
    [string]$Branch = "",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    $gitArgs = $args
    & git @gitArgs
    if ($LASTEXITCODE -ne 0) {
        throw "git $($gitArgs -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-RemoteBranch {
    param([string]$RemoteBranch)

    & git rev-parse --verify --quiet "refs/remotes/origin/$RemoteBranch" *> $null
    return $LASTEXITCODE -eq 0
}

$repoRoot = git rev-parse --show-toplevel 2>$null
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "This folder is not a Git repository yet. Run git init first."
}

Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = git branch --show-current
}

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = "main"
}

$remote = git remote get-url origin 2>$null
if (-not [string]::IsNullOrWhiteSpace($remote)) {
    Invoke-Git fetch origin
}

Invoke-Git add -A
$staged = git diff --cached --name-only

if (-not [string]::IsNullOrWhiteSpace($staged)) {
    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = "Auto sync $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    }

    Invoke-Git commit -m $Message
} else {
    Write-Host "No local source changes to commit."
}

if ([string]::IsNullOrWhiteSpace($remote)) {
    Write-Host "No GitHub remote is configured yet."
    Write-Host "Run:"
    Write-Host "  .\scripts\connect-github.ps1 -RepoUrl <repo-url>"
    exit 0
}

if (Test-RemoteBranch -RemoteBranch $Branch) {
    Write-Host "Downloading newest changes from origin/$Branch..."
    Invoke-Git pull --rebase --autostash origin $Branch
} else {
    Write-Host "No origin/$Branch branch exists yet; the next push will create it."
}

if ($NoPush) {
    Write-Host "Push skipped."
    exit 0
}

Invoke-Git push -u origin $Branch
