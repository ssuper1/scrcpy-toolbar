param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.5.2')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
& (Join-Path $PSScriptRoot 'build.ps1')
$packagePath = Join-Path $dist "scrcpy-toolbar-v$Version-windows.zip"
# Explicitly include only distributable files, even if dist contains old logs.
$files = @('ScrcpyToolbar.exe', 'README.md', 'LICENSE', 'create-desktop-shortcut.vbs')
$stage = Join-Path $dist ('stage-' + [Guid]::NewGuid().ToString('N'))
$stageToolbar = Join-Path $stage 'toolbar'
New-Item -ItemType Directory -Path $stageToolbar -Force | Out-Null
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $dist "toolbar\$file") -Destination $stageToolbar
}
Compress-Archive -LiteralPath $stageToolbar -DestinationPath $packagePath -Force
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$packagePath.sha256", "$hash  $([IO.Path]::GetFileName($packagePath))`n", [Text.Encoding]::ASCII)
# Retain the small staging directory for inspection; no recursive deletion.
Write-Host "Packaged: $packagePath"
Write-Host "SHA256: $hash"
