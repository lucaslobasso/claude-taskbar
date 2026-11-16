# bootstrap.ps1: one-line install of Claude Taskbar from the latest release. No .NET SDK, no git.
#
#   irm https://raw.githubusercontent.com/lucaslobasso/claude-taskbar/main/bootstrap.ps1 | iex
#
# With options:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/lucaslobasso/claude-taskbar/main/bootstrap.ps1))) -NoAutostart
#
# Uninstall the same way (-Purge also deletes settings and the log):
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/lucaslobasso/claude-taskbar/main/bootstrap.ps1))) -Uninstall
#
# Downloads ClaudeTaskbar.exe from the latest GitHub release, checks it against the release's SHA-256, installs it to
# ~/.claude/taskbar with autostart, and installs the taskbar-usage plugin from this repo's marketplace.
param(
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoAutostart,
    [string]$Tag  # a specific release, e.g. v1.1.0 (default: the latest)
)

$ErrorActionPreference = 'Stop'
$repo = 'lucaslobasso/claude-taskbar'
$headers = @{ 'User-Agent' = 'claude-taskbar-bootstrap' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('claude-taskbar-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    # The shared install functions come from the same version of the repo as the exe.
    $api = if ($Tag) { "https://api.github.com/repos/$repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$repo/releases/latest" }
    $release = Invoke-RestMethod $api -Headers $headers
    $lib = Join-Path $work 'install-lib.ps1'
    Invoke-WebRequest "https://raw.githubusercontent.com/$repo/$($release.tag_name)/install-lib.ps1" -OutFile $lib -Headers $headers -UseBasicParsing
    . $lib

    if ($Uninstall) { Uninstall-Taskbar -Purge:$Purge; return }

    $asset = $release.assets | Where-Object name -EQ 'ClaudeTaskbar.exe'
    $sum = $release.assets | Where-Object name -EQ 'ClaudeTaskbar.exe.sha256'
    if (-not $asset) { throw "Release $($release.tag_name) has no ClaudeTaskbar.exe." }
    Write-Host "Downloading Claude Taskbar $($release.tag_name)..."
    $exe = Join-Path $work 'ClaudeTaskbar.exe'
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -Headers $headers -UseBasicParsing
    if ($sum) {
        $sumFile = Join-Path $work 'sum.txt'
        Invoke-WebRequest $sum.browser_download_url -OutFile $sumFile -Headers $headers -UseBasicParsing
        $want = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0]
        $got = (Get-FileHash $exe -Algorithm SHA256).Hash
        if ($got -ne $want) { throw "Checksum mismatch for ClaudeTaskbar.exe (expected $want, got $got). Nothing was installed." }
    }
    Unblock-File $exe
    Install-TaskbarExe $exe -NoAutostart:$NoAutostart
    Install-UsagePlugin
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
