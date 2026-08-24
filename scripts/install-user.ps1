[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\DeskNest'
$runtimeHost = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

if (-not (Test-Path $runtimeHost)) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        '运行栖格需要 Microsoft .NET 8 Desktop Runtime (x64)。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 Windows Desktop Runtime。',
        '缺少运行环境') | Out-Null
    exit 2
}

$runtimes = & $runtimeHost --list-runtimes
if (-not ($runtimes -match 'Microsoft\.WindowsDesktop\.App 8\.')) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        '运行栖格需要 Microsoft .NET 8 Desktop Runtime (x64)。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 Windows Desktop Runtime。',
        '缺少运行环境') | Out-Null
    exit 2
}

Get-Process 'DeskNest.App' -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem $source -File | Where-Object Name -ne 'install-user.ps1' | Copy-Item -Destination $target -Force
Copy-Item (Join-Path $source 'install-user.ps1') $target -Force

$wsh = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
foreach ($shortcutPath in @((Join-Path $desktop '栖格 DeskNest.lnk'), (Join-Path $programs '栖格 DeskNest.lnk'))) {
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $target 'DeskNest.App.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = '轻量桌面图标分区工具'
    $shortcut.IconLocation = (Join-Path $target 'DeskNest.App.exe') + ',0'
    $shortcut.Save()
}

Start-Process (Join-Path $target 'DeskNest.App.exe')
Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show('栖格已安装并启动。可通过桌面快捷方式或系统托盘使用。', '安装完成') | Out-Null
