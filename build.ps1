$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'FolderLens.cs'
$output = Join-Path $PSScriptRoot 'FolderLens.exe'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
$compilerCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $compiler) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:$manifest `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /out:$output $source

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built $output" -ForegroundColor Green
