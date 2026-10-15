# Builds the ready-to-run app into .\dist (the app installs dist\Agent\RdpShadow.Agent.exe on remote machines),
# then the installer into .\installer (only if the build succeeded, so it never packs a stale dist).
$ErrorActionPreference = 'Stop'
$dist = "$PSScriptRoot\dist"

# A copy running from dist locks RdpShadow.App.dll and publish fails. Ask it to close, then force.
$running = Get-Process RdpShadow.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist\*" }
foreach ($p in $running) {
    Write-Host "Closing RdpShadow.App ($($p.Id)) running from dist"
    $null = $p.CloseMainWindow()
    if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
}

dotnet publish "$PSScriptRoot\src\RdpShadow.Agent" -c Release -o "$dist\Agent" -p:DebugType=none
if ($LASTEXITCODE) { throw "Agent build failed" }
dotnet publish "$PSScriptRoot\src\RdpShadow.App" -c Release -o $dist -p:DebugType=none
if ($LASTEXITCODE) { throw "App build failed" }
Write-Host "Ready: $dist\RdpShadow.App.exe"

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { Write-Warning "Inno Setup 6 not found, installer skipped"; return }
& $iscc /Q "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE) { throw "Installer build failed (is installer\RdpShadow-Setup-*.exe open or running?)" }
Write-Host "Installer: $PSScriptRoot\installer"
