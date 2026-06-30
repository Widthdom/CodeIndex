[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$Workspace = $env:GITHUB_WORKSPACE
)

$ErrorActionPreference = "Stop"

if (-not $env:RUNNER_TEMP) {
  throw "RUNNER_TEMP is required to configure the Windows test host."
}

$tempRoot = Join-Path $env:RUNNER_TEMP "cdidx-temp"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

if ($env:GITHUB_ENV) {
  "TMP=$tempRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "TEMP=$tempRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
}

$env:TMP = $tempRoot
$env:TEMP = $tempRoot
Write-Host "Pinned Windows TMP/TEMP to: $tempRoot"
Write-Host ".NET Path.GetTempPath() now resolves to: $([System.IO.Path]::GetTempPath())"

$candidates = @(
  [pscustomobject]@{
    Path = $Workspace
    Reason = "Repository checkout containing build outputs and temp-heavy test fixtures."
  },
  [pscustomobject]@{
    Path = $env:RUNNER_TEMP
    Reason = "GitHub-hosted runner temp root used by actions and pinned TMP/TEMP."
  },
  [pscustomobject]@{
    Path = $env:TEMP
    Reason = "Effective TEMP path used by PowerShell and child processes."
  },
  [pscustomobject]@{
    Path = $env:TMP
    Reason = "Effective TMP path preferred by .NET Path.GetTempPath()."
  },
  [pscustomobject]@{
    Path = [System.IO.Path]::GetTempPath()
    Reason = "Runtime-observed .NET temp path, which can differ from environment variables."
  },
  [pscustomobject]@{
    Path = $env:NUGET_PACKAGES
    Reason = "Explicit NuGet global package cache when configured."
  },
  [pscustomobject]@{
    Path = (Join-Path $env:USERPROFILE ".nuget\packages")
    Reason = "Default user NuGet global package cache touched by restore/build."
  },
  [pscustomobject]@{
    Path = (Join-Path $env:LOCALAPPDATA "NuGet\packages")
    Reason = "Windows local NuGet package cache fallback touched by restore/build."
  }
)

$exclusions = $candidates |
  Where-Object { $_.Path } |
  ForEach-Object {
    $path = $_.Path.TrimEnd('\','/')
    if ($path) {
      [pscustomobject]@{
        Path = $path
        Reason = $_.Reason
      }
    }
  } |
  Group-Object -Property Path |
  ForEach-Object {
    [pscustomobject]@{
      Path = $_.Name
      Reason = (($_.Group | ForEach-Object { $_.Reason }) | Select-Object -Unique) -join " "
    }
  } |
  Sort-Object -Property Path

Write-Host "Windows Defender exclusion audit:"
foreach ($entry in $exclusions) {
  Write-Host ("  {0} -- {1}" -f $entry.Path, $entry.Reason)
}

if ($env:GITHUB_STEP_SUMMARY) {
  "### Windows Defender exclusion audit" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
  "" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
  "| Path | Reason |" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
  "| --- | --- |" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
  foreach ($entry in $exclusions) {
    $safePath = $entry.Path.Replace("|", "\|")
    $safeReason = $entry.Reason.Replace("|", "\|")
    ('| `{0}` | {1} |' -f $safePath, $safeReason) |
      Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
  }
}

foreach ($entry in $exclusions) {
  Add-MpPreference -ExclusionPath $entry.Path -ErrorAction Stop
}

$prefs = Get-MpPreference
foreach ($entry in $exclusions) {
  if ($prefs.ExclusionPath -notcontains $entry.Path) {
    throw "Windows Defender exclusion was not applied: $($entry.Path)"
  }
}
