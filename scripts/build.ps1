[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

Push-Location $root
try {
    & dotnet restore StealthEye.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    & dotnet build StealthEye.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    & dotnet test StealthEye.slnx -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & dotnet publish src/Eye/Eye.csproj -c $Configuration -r win-x64 --self-contained true --no-restore --output $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    & (Join-Path $publish 'eye.exe') --version
    if ($LASTEXITCODE -ne 0) { throw "published eye.exe failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}
