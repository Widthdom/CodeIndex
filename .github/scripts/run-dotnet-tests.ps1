[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Framework,

  [Parameter(Mandatory = $true)]
  [ValidateSet("true", "false")]
  [string]$CollectCoverage,

  [string]$BaseFilter = ""
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
    [bool]$IncludeCrashDiagnostics,

    [string]$TestFilter = ""
  )

  $runArgs = @($testArgs)
  $runArgs += @("--logger", "trx;LogFileName=$ResultFileName")
  if ($IncludeCoverage) {
    $runArgs += @("--collect", "XPlat Code Coverage")
  }
  if ($IncludeCrashDiagnostics) {
    $runArgs += "--blame-crash"
  }
  if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $runArgs += @("--filter", $TestFilter)
  }

  [int]$failureLogTailLineLimit = 2000
  $retainedOutputTail = [System.Collections.Generic.Queue[string]]::new($failureLogTailLineLimit)
  [long]$totalOutputLineCount = 0
  $testSessionTimedOut = $false
  dotnet @runArgs 2>&1 | ForEach-Object {
    $line = [string]$_
    $totalOutputLineCount++
    if ($line.IndexOf("test run timeout", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
      $testSessionTimedOut = $true
    }
    if ($retainedOutputTail.Count -ge $failureLogTailLineLimit) {
      [void]$retainedOutputTail.Dequeue()
    }
    [void]$retainedOutputTail.Enqueue($line)
    Write-Host $line
  }

  $exitCode = $LASTEXITCODE
  if ($exitCode -ne 0) {
    $logDirectory = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $failureLogLines = [System.Collections.Generic.List[string]]::new($retainedOutputTail.Count + 1)
    $omittedOutputLineCount = $totalOutputLineCount - $retainedOutputTail.Count
    if ($omittedOutputLineCount -gt 0) {
      [void]$failureLogLines.Add(
        "[ci] Test output truncated: retained final $($retainedOutputTail.Count) of $totalOutputLineCount lines; $omittedOutputLineCount earlier line(s) were streamed live and omitted from this artifact.")
    }
    [void]$failureLogLines.AddRange($retainedOutputTail.ToArray())
    [System.IO.File]::WriteAllLines($LogPath, [string[]]$failureLogLines)
  }

  return [pscustomobject]@{
    ExitCode = [int]$exitCode
    TestSessionTimedOut = [bool]$testSessionTimedOut
  }
}

function Merge-TestFilters {
  param(
    [string]$BaseFilter,
    [string]$FocusedFilter
  )

  if ([string]::IsNullOrWhiteSpace($BaseFilter)) {
    return $FocusedFilter
  }
  if ([string]::IsNullOrWhiteSpace($FocusedFilter)) {
    return $BaseFilter
  }

  return "($BaseFilter)&($FocusedFilter)"
}

function Get-RetryFilterDecision {
  param(
    [Parameter(Mandatory = $true)]
    [string]$TrxPath
  )

  $telemetryArgs = @(
    "tools/CodeIndex.TestTelemetry/bin/Release/net8.0/CodeIndex.TestTelemetry.dll",
    "retry-filter",
    "--trx-file", $TrxPath
  )
  $capturedOutput = [System.Collections.Generic.List[string]]::new()
  try {
    dotnet @telemetryArgs 2>&1 | ForEach-Object {
      $capturedOutput.Add([string]$_)
    }
  }
  catch {
    return [pscustomobject]@{
      useFocusedRetry = $false
      filter = $null
      failedResultCount = 0
      testMethodCount = 0
      reason = "telemetry_tool_failed"
    }
  }

  if ($LASTEXITCODE -ne 0) {
    return [pscustomobject]@{
      useFocusedRetry = $false
      filter = $null
      failedResultCount = 0
      testMethodCount = 0
      reason = "telemetry_tool_failed"
    }
  }

  try {
    $decision = ($capturedOutput -join "`n") | ConvertFrom-Json -ErrorAction Stop
    if ($decision.useFocusedRetry -isnot [bool] -or
        [string]::IsNullOrWhiteSpace([string]$decision.reason) -or
        ($decision.useFocusedRetry -eq $true -and [string]::IsNullOrWhiteSpace([string]$decision.filter))) {
      throw "Retry filter telemetry returned an incomplete decision."
    }

    return $decision
  }
  catch {
    return [pscustomobject]@{
      useFocusedRetry = $false
      filter = $null
      failedResultCount = 0
      testMethodCount = 0
      reason = "telemetry_output_invalid"
    }
  }
}

$firstLogPath = Join-Path $resultsDirectory "test-output-first.txt"
$firstRunResult = Invoke-TestRun -LogPath $firstLogPath -ResultFileName "test_results_first.trx" -IncludeCoverage $includeCoverage -IncludeCrashDiagnostics $true -TestFilter $BaseFilter
if ($firstRunResult.ExitCode -eq 0) {
  exit 0
}

Write-StepOutput -Name "summarize" -Value "true"

if ($firstRunResult.TestSessionTimedOut) {
  Write-Warning "Initial test run hit TestSessionTimeout; skipping flaky retry to keep CI bounded. Inspect uploaded TRX/blame artifacts."
  exit $firstRunResult.ExitCode
}

Write-Warning "Initial test run failed with exit code $($firstRunResult.ExitCode). Rerunning once to classify possible flakiness."
if ($includeCoverage) {
  Write-Host "Skipping XPlat Code Coverage on the flaky-classification retry."
}
Write-Host "Reusing crash evidence from the initial attempt; the flaky-classification retry skips duplicate crash collection."
$firstTrxPath = Join-Path $resultsDirectory "test_results_first.trx"
$retryFilterDecision = Get-RetryFilterDecision -TrxPath $firstTrxPath
$retryFilter = $BaseFilter
$retryScope = if ([string]::IsNullOrWhiteSpace($BaseFilter)) { "full suite" } else { "full shard: $BaseFilter" }
if ($retryFilterDecision.useFocusedRetry -eq $true -and
    -not [string]::IsNullOrWhiteSpace([string]$retryFilterDecision.filter)) {
  $retryFilter = Merge-TestFilters -BaseFilter $BaseFilter -FocusedFilter ([string]$retryFilterDecision.filter)
  $retryScope = "focused within lane: $($retryFilterDecision.failedResultCount) failed result(s) across $($retryFilterDecision.testMethodCount) test method(s)"
  Write-Host "Using a bounded focused retry for $($retryFilterDecision.testMethodCount) failed test method(s)."
}
else {
  $fallbackScope = if ([string]::IsNullOrWhiteSpace($BaseFilter)) { "full-suite" } else { "full-shard" }
  Write-Host "Focused retry is unavailable ($($retryFilterDecision.reason)); using the $fallbackScope retry fallback."
}
$retryLogPath = Join-Path $resultsDirectory "test-output-retry.txt"
$retryRunResult = Invoke-TestRun -LogPath $retryLogPath -ResultFileName "test_results_retry.trx" -IncludeCoverage $false -IncludeCrashDiagnostics $false -TestFilter $retryFilter
if ($retryRunResult.ExitCode -eq 0) {
  "Initial test run failed, but the single retry passed. Retry scope: $retryScope. Treat this run as flaky and inspect TRX/blame artifacts." |
    Set-Content -Encoding UTF8 -Path (Join-Path $resultsDirectory "flaky-retry.txt")
  Write-Warning "Tests passed on retry; uploaded TestResults include flaky-retry.txt."
  exit 0
}

exit $retryRunResult.ExitCode
