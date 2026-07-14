[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Framework,

  [Parameter(Mandatory = $true)]
  [ValidateSet("true", "false")]
  [string]$CollectCoverage
)

$ErrorActionPreference = "Stop"

$resultsDirectory = "./TestResults"

$testArgs = @(
  "test",
  "tests/CodeIndex.Tests/CodeIndex.Tests.csproj",
  "--configuration", "Release",
  "--framework", $Framework,
  "--no-build",
  "--no-restore",
  "--nologo",
  "--settings", "tests/CodeIndex.Tests/CodeIndex.Tests.runsettings",
  "--results-directory", $resultsDirectory,
  "--blame-hang",
  "--blame-hang-timeout", "5m"
)

$includeCoverage = $CollectCoverage -eq "true"
if (-not $includeCoverage) {
  Write-Host "Skipping XPlat Code Coverage outside ubuntu-24.04/net8.0 so platform/framework matrix lanes run only the test suite."
}

function Write-StepOutput {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$Value
  )

  if ($env:GITHUB_OUTPUT) {
    "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
  }
}

function Invoke-TestRun {
  param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [Parameter(Mandatory = $true)]
    [string]$ResultFileName,

    [Parameter(Mandatory = $true)]
    [bool]$IncludeCoverage,

    [Parameter(Mandatory = $true)]
    [bool]$IncludeCrashDiagnostics
  )

  $runArgs = @($testArgs)
  $runArgs += @("--logger", "trx;LogFileName=$ResultFileName")
  if ($IncludeCoverage) {
    $runArgs += @("--collect", "XPlat Code Coverage")
  }
  if ($IncludeCrashDiagnostics) {
    $runArgs += "--blame-crash"
  }

  $capturedOutput = [System.Collections.Generic.List[string]]::new()
  dotnet @runArgs 2>&1 | ForEach-Object {
    $line = [string]$_
    $capturedOutput.Add($line)
    Write-Host $line
  }

  $exitCode = $LASTEXITCODE
  if ($exitCode -ne 0) {
    $logDirectory = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    [System.IO.File]::WriteAllLines($LogPath, [string[]]$capturedOutput)
  }

  return [int]$exitCode
}

$firstLogPath = Join-Path $resultsDirectory "test-output-first.txt"
$firstExitCode = Invoke-TestRun -LogPath $firstLogPath -ResultFileName "test_results_first.trx" -IncludeCoverage $includeCoverage -IncludeCrashDiagnostics $true
if ($firstExitCode -eq 0) {
  exit 0
}

Write-StepOutput -Name "summarize" -Value "true"

if (Select-String -Path $firstLogPath -SimpleMatch "test run timeout" -Quiet) {
  Write-Warning "Initial test run hit TestSessionTimeout; skipping flaky retry to keep CI bounded. Inspect uploaded TRX/blame artifacts."
  exit $firstExitCode
}

Write-Warning "Initial test run failed with exit code $firstExitCode. Rerunning once to classify possible flakiness."
if ($includeCoverage) {
  Write-Host "Skipping XPlat Code Coverage on the flaky-classification retry."
}
Write-Host "Reusing initial crash diagnostics; the retry keeps hang collection but skips the crash collector."
$retryLogPath = Join-Path $resultsDirectory "test-output-retry.txt"
$retryExitCode = Invoke-TestRun -LogPath $retryLogPath -ResultFileName "test_results_retry.trx" -IncludeCoverage $false -IncludeCrashDiagnostics $false
if ($retryExitCode -eq 0) {
  "Initial test run failed, but the single retry passed. Treat this run as flaky and inspect TRX/blame artifacts." |
    Set-Content -Encoding UTF8 -Path (Join-Path $resultsDirectory "flaky-retry.txt")
  Write-Warning "Tests passed on retry; uploaded TestResults include flaky-retry.txt."
  exit 0
}

exit $retryExitCode
