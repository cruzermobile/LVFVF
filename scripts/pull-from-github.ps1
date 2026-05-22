param(
    [string]$Branch = ""
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
if ([string]::IsNullOrWhiteSpace($remote)) {
    throw "No GitHub remote is configured yet. Run .\scripts\connect-github.ps1 -RepoUrl <repo-url> first."
}

Invoke-Git fetch origin

if (Test-RemoteBranch -RemoteBranch $Branch) {
    Write-Host "Downloading newest changes from origin/$Branch..."
    Invoke-Git pull --rebase --autostash origin $Branch
} else {
    Write-Host "No origin/$Branch branch exists yet. Nothing to download."
}
