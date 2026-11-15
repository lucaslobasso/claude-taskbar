<#
.SYNOPSIS
  Builds Claude Taskbar (Native AOT) and installs it to ~/.claude/taskbar, starting with Windows.
.PARAMETER NoAutostart
  Don't add the HKCU Run entry (you can still toggle it from the strip's right-click menu).
.NOTES
  Needs the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload (MSVC linker + Windows SDK)
  for Native AOT. Run from the repo root: .\install.ps1
#>
param([switch]$NoAutostart, [switch]$ModOnly)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$dest = Join-Path $claudeDir 'taskbar'
$exe = Join-Path $dest 'ClaudeTaskbar.exe'
$out = Join-Path ([IO.Path]::GetTempPath()) 'claude-taskbar-publish'

# The usage mod: copies mod/ to ~/.claude/taskbar/mod and installs it as a user-scope plugin, so every
# Claude Code session writes ~/.claude/taskbar/usage.json (no status line needed).
function Install-UsageMod {
  $modDest = Join-Path $dest 'mod'
  New-Item -ItemType Directory -Force $modDest | Out-Null
  Copy-Item (Join-Path $root 'mod\*') $modDest -Recurse -Force
  Remove-Item (Join-Path $modDest 'test') -Recurse -Force -ErrorAction SilentlyContinue
  if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { Write-Warning 'claude not found: install the mod later with .\install.ps1 -ModOnly'; return }
  claude plugin marketplace add $modDest 2>&1 | Out-Null
  claude plugin install taskbar-usage@taskbar-usage --scope user 2>&1 | Out-Host
  Write-Host 'Usage mod installed (loads in new Claude Code sessions).' -ForegroundColor Green
}

if ($ModOnly) { New-Item -ItemType Directory -Force $dest | Out-Null; Install-UsageMod; return }

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
Install-UsageMod
