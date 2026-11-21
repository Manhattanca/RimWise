; Build with scripts/Build-Installer.ps1. No user data is part of this payload.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppIdValue
  #define AppIdValue "RimWise.Desktop"
#endif
#ifndef AppNameValue
  #define AppNameValue "RimWise"
#endif
#ifndef ActiveMutexValue
  #define ActiveMutexValue "Local\RimWise.Installation.Active"
#endif
#ifdef LegacyTestLayout
  #define ProgramDir "{app}\versions\" + AppVersion
#else
  #define ProgramDir "{app}"
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppNameValue}
AppVersion={#AppVersion}
AppPublisher=Manhattanca
AppPublisherURL=https://github.com/Manhattanca/RimWise
AppSupportURL=https://github.com/Manhattanca/RimWise/issues
AppUpdatesURL=https://github.com/Manhattanca/RimWise/releases
DefaultDirName={localappdata}\Programs\{#AppNameValue}
UsePreviousAppDir=yes
DisableDirPage=no
DefaultGroupName={#AppNameValue}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDirectory}
OutputBaseFilename=RimWise-v{#AppVersion}-Setup
SetupIconFile={#IconPath}
UninstallDisplayIcon={#ProgramDir}\RimWise.exe
LicenseFile={#LicensePath}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex={#ActiveMutexValue}
SetupMutex=Local\RimWise.Setup.Active
CloseApplications=no
RestartApplications=no
UninstallLogMode=overwrite
Uninstallable=not IsPortableUpdate
UninstallDisplayName={#AppNameValue}
VersionInfoVersion={#AppVersion}
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseMessages}"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
#ifdef ExternalPayload
  #include "ExternalFiles.iss"
#else
Source: "{#PackageDirectory}\*"; DestDir: "{#ProgramDir}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RuntimeInstaller}"; DestName: "windowsdesktop-runtime.exe"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\{#AppNameValue} 独立诊断导出"; Filename: "{#ProgramDir}\Diagnostics\RimWise.Diagnostics.exe"; WorkingDir: "{#ProgramDir}\Diagnostics"; Check: not IsPortableUpdate
Name: "{autoprograms}\{#AppNameValue}"; Filename: "{#ProgramDir}\RimWise.exe"; WorkingDir: "{#ProgramDir}"; Check: not IsPortableUpdate
Name: "{autodesktop}\{#AppNameValue}"; Filename: "{#ProgramDir}\RimWise.exe"; WorkingDir: "{#ProgramDir}"; Check: KeepDesktopShortcut

[Run]
Filename: "{#ProgramDir}\RimWise.exe"; Description: "{cm:LaunchProgram,{#AppNameValue}}"; Flags: nowait postinstall skipifsilent unchecked
Filename: "{#ProgramDir}\RimWise.exe"; Flags: nowait runasoriginaluser; Check: IsAutomaticUpdate

[Code]
function IsPortableUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RIMWISEPORTABLE|0}') = '1';
end;

function KeepDesktopShortcut: Boolean;
begin
  Result := not IsPortableUpdate and
    (WizardIsTaskSelected('desktopicon') or FileExists(ExpandConstant('{autodesktop}\{#AppNameValue}.lnk')));
end;

function IsAutomaticUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RIMWISEUPDATE|0}') = '1';
end;

function InitializeSetup: Boolean;
var
  Installed: String;
  PreviousVersion, NewVersion: Int64;
  WaitCount: Integer;
begin
  Result := True;
  if IsAutomaticUpdate then
  begin
    { The app starts Setup only after handling unsaved edits, then exits normally.
      Never terminate an application or install while any RimWise instance is open. }
    WaitCount := 0;
    while CheckForMutexes('{#ActiveMutexValue}') and (WaitCount < 300) do
    begin
      Sleep(100);
      WaitCount := WaitCount + 1;
    end;
    if CheckForMutexes('{#ActiveMutexValue}') then
    begin
      SuppressibleMsgBox('RimWise 尚未退出，更新未开始。请保存并关闭其他窗口后重试。原文件保持不变。', mbError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  end;
  if not IsPortableUpdate and RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1', 'DisplayVersion', Installed) then
    if StrToVersion(Installed, PreviousVersion) and StrToVersion('{#AppVersion}', NewVersion) then
      if ComparePackedVersion(PreviousVersion, NewVersion) > 0 then
      begin
        SuppressibleMsgBox('已安装更新的 RimWise 版本（' + Installed + '）。为避免版本混用，本安装包不会覆盖更新的版本。', mbError, MB_OK, IDOK);
        Result := False;
      end;
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
  Executable: String;
begin
  Result := True;
  { Silent uninstall always preserves user data. Interactive uninstall defaults to No. }
  if UninstallSilent then exit;
  if SuppressibleMsgBox('是否同时清除 RimWise 的个人数据？选择“否”将保留设置、排序记录和其他用户数据。',
      mbConfirmation, MB_YESNO, IDNO) <> IDYES then exit;
  Executable := ExpandConstant('{#ProgramDir}\RimWise.exe');
  if not FileExists(Executable) then
  begin
    SuppressibleMsgBox('找不到 RimWise 数据清理程序。为保护个人数据，卸载已取消。', mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  if not Exec(Executable, '--uninstall-clear-user-data', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
  begin
    SuppressibleMsgBox('个人数据未能安全清除（退出代码 ' + IntToStr(ExitCode) + '）。卸载已取消。', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function HasRuntimeAt(Root: String): Boolean;
var
  Item: TFindRec;
  Core: String;
begin
  Result := False;
  if Root = '' then exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.0.*', Item) then
  begin
    try
      repeat
        Core := AddBackslash(Root) + 'shared\Microsoft.NETCore.App\' + Item.Name;
        if (Pos('-', Item.Name) = 0) and
           FileExists(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\' + Item.Name + '\PresentationFramework.dll') and
           FileExists(Core + '\coreclr.dll') then
        begin
          Result := True;
          break;
        end;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
  end;
end;

function HasDesktopRuntime: Boolean;
var
  Root: String;
begin
  Result := HasRuntimeAt(GetEnv('DOTNET_ROOT_X64'));
  if not Result then Result := HasRuntimeAt(GetEnv('DOTNET_ROOT'));
  if not Result then
    if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
      Result := HasRuntimeAt(Root);
  if not Result then Result := HasRuntimeAt(ExpandConstant('{commonpf64}\dotnet'));
end;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsPlainPath(Path: String): Boolean;
var
  Attributes: Cardinal;
  Parent: String;
begin
  Result := False;
  while Path <> '' do
  begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
  Result := True;
end;

procedure CheckPayloadFile(RelativePath, AllowedHashes, ExpectedHash: String);
var
  Target, SourcePath: String;
begin
  Target := AddBackslash(ExpandConstant('{#ProgramDir}')) + RelativePath;
  if not IsPlainPath(Target) or DirExists(Target) then
    RaiseException('安装位置包含目录链接或同名目录：' + RelativePath);
  if FileExists(Target) then
    if Pos('|' + Lowercase(GetSHA256OfFile(Target)) + '|', AllowedHashes) = 0 then
      RaiseException('安装位置存在无法确认归属的同名文件：' + RelativePath);
#ifdef ExternalPayload
  SourcePath := ExpandConstant('{src}\payload\') + RelativePath;
  if not IsPlainPath(SourcePath) or not FileExists(SourcePath) then
    RaiseException('缺少新版程序文件：' + RelativePath);
  if CompareText(GetSHA256OfFile(SourcePath), ExpectedHash) <> 0 then
    RaiseException('新版程序文件校验失败：' + RelativePath);
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  try
    #include "CheckPayload.iss"
  except
    Result := GetExceptionMessage;
    exit;
  end;
  if HasDesktopRuntime then
  begin
    Log('.NET 10 Desktop Runtime x64 already available.');
    exit;
  end;
#ifdef ExternalPayload
  Result := '缺少 .NET 10 桌面运行时（x64），请使用完整安装包。';
#else
  if WizardSilent then
  begin
    Result := '请先安装 .NET 10 桌面运行时（x64），或双击本安装包进行安装。';
    exit;
  end;
  if MsgBox('RimWise 需要 .NET 10 桌面运行时（x64）。是否现在安装随包提供的微软运行时？Windows 可能要求管理员授权。', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := '缺少 .NET 10 桌面运行时（x64），安装已取消，原有 RimWise 文件未更改。';
    exit;
  end;
  ExtractTemporaryFile('windowsdesktop-runtime.exe');
  if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := '无法启动微软运行时安装程序，RimWise 尚未更新。';
    exit;
  end;
  if (ExitCode <> 0) and (ExitCode <> 3010) then
    Result := '微软运行时安装失败（退出代码 ' + IntToStr(ExitCode) + '），RimWise 尚未更新。'
  else if not HasDesktopRuntime then
    Result := '仍未检测到所需运行时。如系统提示需要重启，请重启后重新运行本安装包。';
  NeedsRestart := ExitCode = 3010;
#endif
end;

procedure RemoveKnownOldFile(RelativePath, ExpectedHash: String);
var
  Path: String;
begin
  if IsPortableUpdate and (Pos('versions\', RelativePath) = 1) then exit;
  { Generated entries only: exact files and hashes from prior distribution packages. }
  Path := ExpandConstant('{app}\') + RelativePath;
  if not IsPlainPath(Path) or not FileExists(Path) then exit;
  try
    if CompareText(GetSHA256OfFile(Path), ExpectedHash) = 0 then
    begin
      if not DeleteFile(Path) then Log('Preserved occupied old package file: ' + RelativePath);
    end
    else Log('Preserved modified old package file: ' + RelativePath);
  except
    Log('Preserved unreadable old package file: ' + RelativePath);
  end;
end;

procedure RemoveKnownEmptyDir(RelativePath: String);
var
  Path: String;
begin
  if IsPortableUpdate then exit;
  Path := ExpandConstant('{app}\') + RelativePath;
  if IsPlainPath(Path) then RemoveDir(Path);
end;

procedure RemoveLegacyUninstallShortcut;
var
  Path, Target: String;
  Shell, Shortcut: Variant;
begin
  if IsPortableUpdate then exit;
  Path := ExpandConstant('{app}\卸载 RimWise.lnk');
  Target := ExpandConstant('{uninstallexe}');
  if not IsPlainPath(Path) or not IsPlainPath(Target) or not FileExists(Path) then exit;
  try
    { Read only: never Save the shortcut or remove a same-name personal link. }
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Path);
    if (CompareText(Shortcut.TargetPath, Target) = 0) and (Shortcut.Arguments = '') then
    begin
      if not DeleteFile(Path) then Log('Preserved occupied legacy uninstall shortcut.');
    end
    else Log('Preserved unrelated same-name shortcut.');
  except
    Log('Preserved unreadable legacy uninstall shortcut.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
  begin
    RemoveLegacyUninstallShortcut;
#ifndef LegacyTestLayout
    #include "RetiredInstallerFiles.iss"
#endif
  end;
end;
