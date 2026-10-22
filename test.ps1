$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The Windows .NET Framework C# compiler was not found.' }

$testExe = Join-Path $PSScriptRoot 'FolderLens.Tests.exe'
& $compiler /nologo /target:exe /optimize+ /platform:anycpu /main:FolderLens.Tests `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /out:$testExe `
    (Join-Path $PSScriptRoot 'FolderLens.cs') (Join-Path $PSScriptRoot 'FolderLens.Tests.cs')
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE." }

& $testExe
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
