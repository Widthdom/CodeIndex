[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$Workspace = $env:GITHUB_WORKSPACE
)

$ErrorActionPreference = "Stop"

if (-not $env:USERPROFILE) {
  throw "USERPROFILE is required to configure the Windows test host."
}
if (-not $env:RUNNER_TEMP) {
  throw "RUNNER_TEMP is required to configure the Windows test host."
}

$tempRoot = Join-Path $env:RUNNER_TEMP "cdidx-temp"
$trustedTempRoot = Join-Path $env:USERPROFILE "cdidx-trusted-test-temp"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
New-Item -ItemType Directory -Force -Path $trustedTempRoot | Out-Null

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
  $currentUser = $currentIdentity.User
  if (-not $currentUser) {
    throw "The current Windows user SID is unavailable."
  }

  $trustedTempAcl = [System.Security.AccessControl.DirectorySecurity]::new()
  $trustedTempAcl.SetOwner($currentUser)
  $trustedTempAcl.SetAccessRuleProtection($true, $false)
  $inheritanceFlags =
    [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
  $trustedTempAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
    $currentUser,
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    $inheritanceFlags,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow))
  Set-Acl -LiteralPath $trustedTempRoot -AclObject $trustedTempAcl
  Write-Host "Protected Windows executable-extension test temp ACL for current user: $currentUser"
}
finally {
  $currentIdentity.Dispose()
}

if ($env:GITHUB_ENV) {
  "TMP=$tempRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "TEMP=$tempRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "CDIDX_TEST_TRUSTED_TEMP_ROOT=$trustedTempRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
}

$env:TMP = $tempRoot
$env:TEMP = $tempRoot
$env:CDIDX_TEST_TRUSTED_TEMP_ROOT = $trustedTempRoot
Write-Host "Pinned Windows TMP/TEMP to fast runner storage: $tempRoot"
Write-Host "Pinned executable-extension fixtures to protected storage: $trustedTempRoot"
Write-Host ".NET Path.GetTempPath() now resolves to: $([System.IO.Path]::GetTempPath())"

$candidates = @(
  [pscustomobject]@{
    Path = $Workspace
    Reason = "Repository checkout containing build outputs and temp-heavy test fixtures."
  },
  [pscustomobject]@{
    Path = $env:RUNNER_TEMP
    Reason = "GitHub-hosted runner temp root used by actions."
  },
  [pscustomobject]@{
    Path = $trustedTempRoot
    Reason = "Protected current-user root for executable plugin, hook, and Git fixtures."
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

[string[]]$exclusionPaths = @($exclusions | ForEach-Object { $_.Path })
if ($exclusionPaths.Count -gt 0) {
  Add-MpPreference -ExclusionPath $exclusionPaths -ErrorAction Stop
}

$prefs = Get-MpPreference
foreach ($entry in $exclusions) {
  if ($prefs.ExclusionPath -notcontains $entry.Path) {
    throw "Windows Defender exclusion was not applied: $($entry.Path)"
  }
}
