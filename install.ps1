<#
.SYNOPSIS
  Builds Claude Taskbar from source (Native AOT) and installs it to ~/.claude/taskbar, starting with Windows.
  Most people want the one-line install of a release instead (see the README); this is for contributors.
.PARAMETER NoAutostart
  Don't add the HKCU Run entry (you can still toggle it from the strip's right-click menu).
.PARAMETER ModOnly
  Only (re)install the taskbar-usage plugin, no build.
.PARAMETER LocalMod
  Install the usage plugin from this clone instead of GitHub (to try changes to mod/).
.NOTES
  Needs the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload (MSVC linker + Windows SDK)
  for Native AOT. Run from the repo root: .\install.ps1
#>
param([switch]$NoAutostart, [switch]$ModOnly, [switch]$LocalMod)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-lib.ps1')

$market = if ($LocalMod) { $PSScriptRoot } else { $TaskbarRepo }
if ($ModOnly) { Install-UsagePlugin $market; return }

$out = Join-Path ([IO.Path]::GetTempPath()) 'claude-taskbar-publish'
Write-Host 'Building (Native AOT)...'
dotnet publish (Join-Path $PSScriptRoot 'src\ClaudeTaskbar.csproj') -c Release -o $out --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Native AOT needs the VS "Desktop development with C++" workload.' }

Install-TaskbarExe (Join-Path $out 'ClaudeTaskbar.exe') -NoAutostart:$NoAutostart
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Install-UsagePlugin $market
