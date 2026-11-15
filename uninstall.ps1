<#
.SYNOPSIS
  Stops Claude Taskbar, removes the autostart entry and the exe. -Purge also deletes settings, usage snapshot and log.
#>
param([switch]$Purge)
$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$dest = Join-Path $claudeDir 'taskbar'

Get-Process ClaudeTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClaudeTaskbar -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
if ($Purge) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
else { Remove-Item (Join-Path $dest 'ClaudeTaskbar.exe') -Force -ErrorAction SilentlyContinue }
Write-Host 'Claude Taskbar removed.' -ForegroundColor Green
