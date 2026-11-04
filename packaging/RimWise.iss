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
UninstallDisplayIcon={app}\versions\{#AppVersion}\RimWise.exe
LicenseFile={#LicensePath}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\RimWise.Installation.Active
SetupMutex=Local\RimWise.Setup.Active
CloseApplications=no
RestartApplications=no
UninstallLogMode=append
UninstallDisplayName={#AppNameValue}
VersionInfoVersion={#AppVersion}
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseMessages}"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RuntimeInstaller}"; DestName: "windowsdesktop-runtime.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppNameValue}"; Filename: "{app}\versions\{#AppVersion}\RimWise.exe"; WorkingDir: "{app}\versions\{#AppVersion}"
Name: "{autodesktop}\{#AppNameValue}"; Filename: "{app}\versions\{#AppVersion}\RimWise.exe"; WorkingDir: "{app}\versions\{#AppVersion}"; Tasks: desktopicon

[Run]
Filename: "{app}\versions\{#AppVersion}\RimWise.exe"; Description: "{cm:LaunchProgram,{#AppNameValue}}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function InitializeSetup: Boolean;
var
  Installed: String;
  PreviousVersion, NewVersion: Int64;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1', 'DisplayVersion', Installed) then
    if StrToVersion(Installed, PreviousVersion) and StrToVersion('{#AppVersion}', NewVersion) then
      if ComparePackedVersion(PreviousVersion, NewVersion) > 0 then
      begin
        SuppressibleMsgBox('已安装更新的 RimWise 版本（' + Installed + '）。为避免版本混用，本安装包不会覆盖更新的版本。', mbError, MB_OK, IDOK);
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime then
  begin
    Log('.NET 10 Desktop Runtime x64 already available.');
    exit;
  end;
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
end;

procedure RemoveKnownOldFile(RelativePath, ExpectedHash: String);
var
  Path: String;
begin
  { Generated entries only: exact files and hashes from prior distribution packages. }
  Path := ExpandConstant('{app}\') + RelativePath;
  if not FileExists(Path) then exit;
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
  begin
    #include "RetiredInstallerFiles.iss"
  end;
end;
