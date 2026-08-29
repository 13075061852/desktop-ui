[CmdletBinding()]
param()

# 生成双击安装的 Setup 安装包（Inno Setup 6）。
# 前置条件：dist\DeskNest-win-x64 已有发布产物（npm run build 会先生成）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning '未找到 Inno Setup 6（ISCC.exe），跳过 Setup 打包。安装：winget install JRSoftware.InnoSetup'
    exit 0
}

$publish = Join-Path $root 'dist\DeskNest-win-x64'
if (-not (Test-Path (Join-Path $publish 'DeskNest.App.exe'))) {
    throw '发布产物缺失（dist\DeskNest-win-x64\DeskNest.App.exe）。请先运行 npm run build。'
}

& $iscc (Join-Path $PSScriptRoot 'setup\DeskNest.iss') '/Qp'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败。' }

$setup = Join-Path $root 'dist\DeskNest-Setup.exe'
if (-not (Test-Path $setup)) { throw '未找到生成的 DeskNest-Setup.exe。' }

$size = (Get-Item $setup).Length
Write-Host ''
Write-Host "Setup installer is ready: $setup" -ForegroundColor Green
Write-Host ('Setup size: {0:N2} MB' -f ($size / 1MB))
