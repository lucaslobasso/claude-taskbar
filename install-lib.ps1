# Shared by install.ps1 (build from source), bootstrap.ps1 (one-line install of a release) and uninstall.ps1.
# Works in Windows PowerShell 5.1 and PowerShell 7.

$TaskbarRepo = 'lucaslobasso/claude-taskbar'
$UsagePluginId = 'taskbar-usage@claude-taskbar'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-TaskbarDir {
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    Join-Path $claudeDir 'taskbar'
}

function Stop-Taskbar {
    Get-Process ClaudeTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

# Copies the exe into place, sets autostart and starts it.
function Install-TaskbarExe([string]$Source, [switch]$NoAutostart) {
    $dest = Get-TaskbarDir
    $exe = Join-Path $dest 'ClaudeTaskbar.exe'
    Stop-Taskbar
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $Source $exe -Force
    if ($NoAutostart) { Remove-ItemProperty $RunKey -Name ClaudeTaskbar -ErrorAction SilentlyContinue }
    else { Set-ItemProperty $RunKey -Name ClaudeTaskbar -Value "`"$exe`"" }
    Start-Process $exe
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Installed $exe ($size MB) and started it." -ForegroundColor Green
    if (-not $NoAutostart) { Write-Host 'It starts with Windows (toggle in the right-click menu).' }
}

# The usage plugin: every Claude Code session writes ~/.claude/taskbar/usage.json from its usage readings.
# $Marketplace is the GitHub repo by default, or a local clone (install.ps1 -LocalMod, for development).
function Install-UsagePlugin([string]$Marketplace = $TaskbarRepo) {
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Warning "claude not found: install the usage plugin later with: claude plugin marketplace add $TaskbarRepo; claude plugin install $UsagePluginId --scope user"
        return
    }
    Remove-LegacyUsagePlugin
    claude plugin marketplace add $Marketplace 2>&1 | Out-Null
    claude plugin marketplace update claude-taskbar 2>&1 | Out-Null
    $installed = @(claude plugin list --json 2>$null | ConvertFrom-Json) | Where-Object id -EQ $UsagePluginId
    if ($installed) { claude plugin update $UsagePluginId 2>&1 | Out-Host }
    else { claude plugin install $UsagePluginId --scope user 2>&1 | Out-Host }
    Write-Host 'Usage plugin installed (loads in new Claude Code sessions).' -ForegroundColor Green
}

# Before 1.1 the plugin came from a local copy in ~/.claude/taskbar/mod (marketplace "taskbar-usage").
function Remove-LegacyUsagePlugin {
    $plugins = @(claude plugin list --json 2>$null | ConvertFrom-Json)
    if ($plugins | Where-Object id -EQ 'taskbar-usage@taskbar-usage') {
        claude plugin uninstall taskbar-usage@taskbar-usage --scope user 2>&1 | Out-Null
        claude plugin marketplace remove taskbar-usage 2>&1 | Out-Null
    }
    Remove-Item (Join-Path (Get-TaskbarDir) 'mod') -Recurse -Force -ErrorAction SilentlyContinue
}

function Uninstall-Taskbar([switch]$Purge) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        Remove-LegacyUsagePlugin
        claude plugin uninstall $UsagePluginId --scope user 2>&1 | Out-Null
        claude plugin marketplace remove claude-taskbar 2>&1 | Out-Null
    }
    Stop-Taskbar
    Remove-ItemProperty $RunKey -Name ClaudeTaskbar -ErrorAction SilentlyContinue
    $dest = Get-TaskbarDir
    if ($Purge) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
    else { Remove-Item (Join-Path $dest 'ClaudeTaskbar.exe') -Force -ErrorAction SilentlyContinue }
    Write-Host 'Claude Taskbar removed.' -ForegroundColor Green
}
