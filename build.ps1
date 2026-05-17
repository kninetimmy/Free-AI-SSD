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
$macHostProject = Join-Path $repoRoot "mac-runner-host/FreeAiSsd.MacRunnerHost.csproj"
$publishDir = Join-Path $repoRoot "runner/bin/$Configuration/net8.0-windows/$Runtime/publish"
$cliPublishDir = Join-Path $repoRoot "runner-cli/bin/$Configuration/net8.0/$Runtime/publish"
$macHostPublishDir = Join-Path $repoRoot "mac-runner-host/bin/$Configuration/net8.0/osx-arm64/publish"
$stagedRunnerDir = Join-Path $repoRoot "prep-app/bin/$Configuration/net8.0-windows/runner-publish"
$stagedMacHostDir = Join-Path $repoRoot "out/mac/runner-host"

Write-Host "[1/5] Building solution ($Configuration)..."
dotnet build $solutionPath -c $Configuration /p:EnableWindowsTargeting=true

Write-Host "[2/5] Publishing runner (self-contained single-file $Runtime)..."
dotnet publish $runnerProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableWindowsTargeting=true

if (!(Test-Path $publishDir)) {
    throw "Publish output not found at $publishDir"
}

Write-Host "[3/5] Publishing runner-cli (self-contained single-file $Runtime)..."
dotnet publish $runnerCliProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

if (!(Test-Path $cliPublishDir)) {
    throw "CLI publish output not found at $cliPublishDir"
}

# MAC6: cross-publish the Mac sidecar host from the Windows build so the cross-
# platform release ZIP can ship under payload/mac/runner-host/. PublishTrimmed
# is intentionally OFF (RunnerCore minimal-API uses reflection); single-file
# is fine. Self-contained so the Mac end users don't need a system .NET 8
# install — the SSD is the source of truth.
Write-Host "[4/5] Publishing mac-runner-host (osx-arm64 self-contained single-file)..."
dotnet publish $macHostProject -c $Configuration -r osx-arm64 --self-contained true /p:PublishSingleFile=true

if (!(Test-Path $macHostPublishDir)) {
    throw "Mac host publish output not found at $macHostPublishDir"
}

if (Test-Path $stagedMacHostDir) { Remove-Item $stagedMacHostDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagedMacHostDir -Force | Out-Null
Copy-Item (Join-Path $macHostPublishDir "*") $stagedMacHostDir -Recurse -Force

Write-Host "[5/5] Syncing runner + CLI publish output to prep-app runner-publish..."
if (Test-Path $stagedRunnerDir) { Remove-Item $stagedRunnerDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagedRunnerDir -Force | Out-Null
Copy-Item (Join-Path $publishDir "*") $stagedRunnerDir -Recurse -Force
Copy-Item (Join-Path $cliPublishDir "FreeAiSsd.RunnerCli*") $stagedRunnerDir -Force

Write-Host "Done. Runner + CLI artifacts staged at: $stagedRunnerDir"
Write-Host "Mac sidecar host staged at: $stagedMacHostDir"
