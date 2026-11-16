<#
.SYNOPSIS
  Stops Claude Taskbar, removes the autostart entry, the exe and the taskbar-usage plugin.
.PARAMETER Purge
  Also delete settings (taskbar.json), the usage snapshot and the log.
#>
param([switch]$Purge)
. (Join-Path $PSScriptRoot 'install-lib.ps1')
Uninstall-Taskbar -Purge:$Purge
