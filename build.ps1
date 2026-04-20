param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$solutionPath = Join-Path $repoRoot "FreeAiSsd.sln"
$runnerProject = Join-Path $repoRoot "runner/FreeAiSsd.Runner.csproj"
$runnerCliProject = Join-Path $repoRoot "runner-cli/FreeAiSsd.RunnerCli.csproj"
$publishDir = Join-Path $repoRoot "runner/bin/$Configuration/net8.0-windows/$Runtime/publish"
$cliPublishDir = Join-Path $repoRoot "runner-cli/bin/$Configuration/net8.0/$Runtime/publish"
$stagedRunnerDir = Join-Path $repoRoot "prep-app/bin/$Configuration/net8.0-windows/runner-publish"

Write-Host "[1/4] Building solution ($Configuration)..."
dotnet build $solutionPath -c $Configuration /p:EnableWindowsTargeting=true

Write-Host "[2/4] Publishing runner (self-contained single-file $Runtime)..."
dotnet publish $runnerProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true /p:EnableWindowsTargeting=true

if (!(Test-Path $publishDir)) {
    throw "Publish output not found at $publishDir"
}

Write-Host "[3/4] Publishing runner-cli (self-contained single-file $Runtime)..."
dotnet publish $runnerCliProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true

if (!(Test-Path $cliPublishDir)) {
    throw "CLI publish output not found at $cliPublishDir"
}

Write-Host "[4/4] Syncing runner + CLI publish output to prep-app runner-publish..."
if (Test-Path $stagedRunnerDir) { Remove-Item $stagedRunnerDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagedRunnerDir -Force | Out-Null
Copy-Item (Join-Path $publishDir "*") $stagedRunnerDir -Recurse -Force
Copy-Item (Join-Path $cliPublishDir "FreeAiSsd.RunnerCli*") $stagedRunnerDir -Force

Write-Host "Done. Runner + CLI artifacts staged at: $stagedRunnerDir"
