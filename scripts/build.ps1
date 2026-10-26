param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist\toolbar' }
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'C# compiler not found. Install .NET Framework development tools.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binary = Join-Path $OutputDirectory 'ScrcpyToolbar.exe'
& $compiler /nologo /target:winexe /platform:anycpu /codepage:65001 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" "/out:$binary" (Join-Path $projectRoot 'src\ScrcpyToolbar.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'packaging\create-desktop-shortcut.vbs') -Destination $OutputDirectory -Force
Write-Host "Built: $binary"
