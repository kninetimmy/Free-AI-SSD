param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$solutionPath = Join-Path $repoRoot "FreeAiSsd.sln"
$runnerProject = Join-Path $repoRoot "runner/FreeAiSsd.Runner.csproj"
$publishDir = Join-Path $repoRoot "runner/bin/$Configuration/net8.0-windows/$Runtime/publish"
$stagedRunnerDir = Join-Path $repoRoot "prep-app/bin/$Configuration/net8.0-windows/runner-publish"

Write-Host "[1/3] Building solution ($Configuration)..."
dotnet build $solutionPath -c $Configuration

Write-Host "[2/3] Publishing runner (self-contained single-file $Runtime)..."
dotnet publish $runnerProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true

if (!(Test-Path $publishDir)) {
    throw "Publish output not found at $publishDir"
}

Write-Host "[3/3] Syncing runner publish output to prep-app runner-publish..."
New-Item -ItemType Directory -Path $stagedRunnerDir -Force | Out-Null
Copy-Item (Join-Path $publishDir "*") $stagedRunnerDir -Recurse -Force

Write-Host "Done. Runner artifacts staged at: $stagedRunnerDir"
