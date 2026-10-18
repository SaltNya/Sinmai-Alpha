$ErrorActionPreference = 'Stop'
$taskPython = Get-Command python -ErrorAction SilentlyContinue
if (!$taskPython) { throw 'Python 3 is required to extract the current production methods.' }
& $taskPython.Source (Join-Path $PSScriptRoot 'prepare.py')
if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed; tests did not run.' }
dotnet run --project (Join-Path $PSScriptRoot 'Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Noise raster parity tests failed.' }
