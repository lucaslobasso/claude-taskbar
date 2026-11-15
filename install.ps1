<#
.SYNOPSIS
  Builds Claude Taskbar (Native AOT) and installs it to ~/.claude/taskbar, starting with Windows.
.PARAMETER NoAutostart
  Don't add the HKCU Run entry (you can still toggle it from the strip's right-click menu).
.NOTES
  Needs the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload (MSVC linker + Windows SDK)
  for Native AOT. Run from the repo root: .\install.ps1
#>
param([switch]$NoAutostart)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$dest = Join-Path $claudeDir 'taskbar'
$exe = Join-Path $dest 'ClaudeTaskbar.exe'
$out = Join-Path ([IO.Path]::GetTempPath()) 'claude-taskbar-publish'

Write-Host 'Building (Native AOT)...'
dotnet publish (Join-Path $root 'src\ClaudeTaskbar.csproj') -c Release -o $out --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Native AOT needs the VS "Desktop development with C++" workload.' }

Get-Process ClaudeTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $out 'ClaudeTaskbar.exe') $exe -Force
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoAutostart) { Set-ItemProperty $run -Name ClaudeTaskbar -Value "`"$exe`"" }

Start-Process $exe
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Installed $exe ($size MB) and started it." -ForegroundColor Green
if (-not $NoAutostart) { Write-Host 'It starts with Windows (toggle in the right-click menu).' }
Write-Host 'Usage data: add the snippet in statusline\usage-snapshot.mjs to your status line (see README).'
