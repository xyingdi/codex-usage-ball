[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CodexUsageBall\CodexUsageBall.csproj"
$publishDirectory = Join-Path $repoRoot "artifacts\publish"
$releaseDirectory = Join-Path $repoRoot "artifacts\release"
$releaseExecutable = Join-Path $releaseDirectory "Codex 用量悬浮球.exe"
$checksumFile = Join-Path $releaseDirectory "SHA256.txt"

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $releaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDirectory, $releaseDirectory -Force | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $publishDirectory "CodexUsageBall.exe") -Destination $releaseExecutable
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseExecutable).Hash
Set-Content -LiteralPath $checksumFile -Encoding utf8 -Value "$hash  Codex 用量悬浮球.exe"

Write-Host "Build complete: $releaseExecutable"
Write-Host "SHA-256: $hash"

