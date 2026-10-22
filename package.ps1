$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'FolderLens build failed.' }

$distribution = Join-Path $PSScriptRoot 'dist'
$staging = Join-Path $distribution 'FolderLens-Windows'
$archive = Join-Path $distribution 'FolderLens-Windows.zip'

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FolderLens.exe') -Destination $staging
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $staging

@'
FOLDERLENS QUICK START

1. Extract this ZIP file.
2. Double-click FolderLens.exe.
3. Choose a folder, or click All drives.

No installer is required. FolderLens stores the latest completed scan in your
Windows local app-data folder so it is restored the next time you open the app.

Windows may show a SmartScreen warning because this community build is not
code-signed. Choose "More info" and "Run anyway" only if you downloaded the
file from the official FolderLens GitHub release.
'@ | Set-Content -LiteralPath (Join-Path $staging 'QUICK-START.txt') -Encoding UTF8

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  FolderLens-Windows.zip" | Set-Content -LiteralPath (Join-Path $distribution 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "Packaged $archive" -ForegroundColor Green
Write-Host "SHA-256: $hash"
