[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$dist = Join-Path $root 'dist\DeskNest-win-x64'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $root
try {
    & $dotnet restore '.\DeskNest.sln'
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    & $dotnet run --project '.\tests\DeskNest.Tests\DeskNest.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    & $dotnet publish '.\src\DeskNest.App\DeskNest.App.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $dist
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Copy-Item '.\scripts\install-user.ps1' $dist
    Copy-Item '.\scripts\安装栖格.cmd' $dist
    Copy-Item '.\docs\USER-GUIDE.md' (Join-Path $dist '使用说明.md')

    $zip = Join-Path $root 'dist\DeskNest-MVP-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal

    $size = (Get-ChildItem $dist -File -Recurse | Measure-Object Length -Sum).Sum
    Write-Host "`nDeskNest release is ready: $dist" -ForegroundColor Green
    Write-Host "Zip package: $zip"
    Write-Host ('Package size: {0:N2} MB' -f ($size / 1MB))
}
finally {
    Pop-Location
}
