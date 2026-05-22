param(
    [Parameter(Mandatory = $true)]
    [string]$RepoUrl,
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

$repoRoot = git rev-parse --show-toplevel 2>$null
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "This folder is not a Git repository yet. Run git init first."
}

Set-Location $repoRoot

$existingRemote = git remote get-url origin 2>$null
if ([string]::IsNullOrWhiteSpace($existingRemote)) {
    git remote add origin $RepoUrl
} else {
    git remote set-url origin $RepoUrl
}

git branch -M $Branch
git push -u origin $Branch
