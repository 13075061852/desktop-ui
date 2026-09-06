; 栖格 DeskNest —— 用户级安装包（Inno Setup 6）
; 由 scripts/make-setup.ps1 调用；需先有发布产物 dist\DeskNest-win-x64\DeskNest.App.exe

#define MyAppName "栖格 DeskNest"
#define MyAppExeName "DeskNest.App.exe"
#define MyAppVersionRaw GetVersionNumbersString(AddBackslash(SourcePath) + "..\..\dist\DeskNest-win-x64\DeskNest.App.exe")
#if MyAppVersionRaw == ""
#define MyAppVersion "1.0.0"
#else
#define MyAppVersion MyAppVersionRaw
#endif

[Setup]
AppId={{7E3A1C46-9B2D-4F5E-8A7C-6D1B0E4F2A93}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
; 用户级安装：不需要管理员权限，与 install-user.ps1 的行为一致
DefaultDirName={localappdata}\Programs\DeskNest
PrivilegesRequired=lowest
; 以 64 位模式运行安装进程：32 位进程里 {pf} 会被重定向到
; Program Files (x86)，导致 .NET 8 运行时检测误判（实际装在 64 位目录）
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=DeskNest-Setup
SetupIconFile=..\..\assets\DeskNest.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\DeskNest.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
ShowLanguageDialog=no

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "..\..\dist\DeskNest-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\栖格 DeskNest"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\栖格 DeskNest"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行栖格(&R)"; Flags: nowait postinstall skipifsilent

[Code]
// 安装 / 卸载前先结束正在运行的栖格
function KillRunningApp(): Boolean;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /f /im DeskNest.App.exe >nul 2>&1', '',
       SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;

// 框架依赖发布要求 .NET 8，默认不跨主版本使用 .NET 9/10。
function HasDesktopRuntime8(): Boolean;
var
  base, name: String;
  find: TFindRec;
  major: Integer;
begin
  Result := False;
  // 机器级与用户级两种 dotnet 安装位置都查
  base := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(base) then
    base := ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(base) then Exit;

  if FindFirst(base + '\*', find) then
  try
    repeat
      if (find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        name := find.Name;
        major := StrToIntDef(Copy(name, 1, Pos('.', name + '.') - 1), 0);
        if major = 8 then
          Result := True;
      end;
    until not FindNext(find) or Result;
  finally
    FindClose(find);
  end;
end;

function InitializeSetup(): Boolean;
var
  Code: Integer;
begin
  if not HasDesktopRuntime8() then
  begin
    if MsgBox('栖格需要 Microsoft .NET 8 Desktop Runtime (x64)，当前电脑未安装。' #13#10 #13#10
              '点击"是"打开官网下载页；安装运行时后请重新运行本安装包。',
              mbCriticalError, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '',
                SW_SHOW, ewNoWait, Code);
    Result := False;
  end
  else
    Result := True;
end;

// 用户确认安装并通过依赖检查后才结束旧版，取消向导不影响运行中的软件。
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningApp();
  Result := True;
end;
