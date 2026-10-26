$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testExe = Join-Path $projectRoot 'dist\KeyboardRestartTests.exe'
& $compiler /nologo /target:exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$testExe" (Join-Path $projectRoot 'tests\KeyboardRestartTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe (Join-Path $projectRoot 'dist\toolbar\ScrcpyToolbar.exe')
if ($LASTEXITCODE -ne 0) { throw 'Keyboard restart tests failed.' }
