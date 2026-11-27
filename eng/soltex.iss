; Soltex per-user installer (Inno Setup 6).
;
; The app runs unelevated, so the installer targets the per-user Programs folder
; and never asks for administrator rights. It installs the whole published folder
; (Soltex.exe with its runtime, TaskbarHelper\, Soltex.Audio.Host\, licenses\ and
; the notices).
;
; Built by eng\publish-release.ps1 or by the release workflow. AppVersion is required:
;
;   ISCC.exe /DPublishDir=<app folder> /DAppVersion=<x.y.z> [/DNumericVersion=<x.y.z>] /DOutputDir=<dir> eng\soltex.iss
;
; NumericVersion defaults to AppVersion and must be numeric; pass it when AppVersion has a -suffix.

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef AppVersion
  #error AppVersion is missing. Pass /DAppVersion=<x.y.z> to ISCC (see the top of eng\soltex.iss).
#endif
; Windows version resources must be numeric (1.2.3), without a -suffix.
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Soltex"
#define AppPublisher "Soltex"
#define AppUrl "https://github.com/celubos/soltex"
#define AppExeName "Soltex.exe"

[Setup]
AppId={{8E1F0C6A-5B3D-4A77-9F2E-1C4D6B8A0E52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 Soltex contributors. MIT License.

; Per-user install: {autopf} resolves to {localappdata}\Programs, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The folder is asked for on a first install only; upgrades reuse it.
DisableDirPage=auto
DisableReadyPage=yes
DisableWelcomePage=no
AllowNoIcons=yes

; The app targets Windows 10 version 2004 (build 19041) or newer.
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

SetupIconFile=..\src\Soltex.App\Soltex.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=installer\wizard-100.bmp,installer\wizard-200.bmp
WizardSmallImageFile=installer\small-100.bmp,installer\small-200.bmp
WizardImageAlphaFormat=premultiplied

OutputDir={#OutputDir}
OutputBaseFilename=Soltex-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
; Soltex itself is asked to quit first (see [Code]); this covers anything else using the files.
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] for your account. No administrator rights are needed.%n%nNothing in Windows changes until you turn it on in the app, and every change can be undone from History.
FinishedLabel=Soltex is installed. Find it in the Start menu, or in the notification area while it runs.

[Tasks]
; First install only. After that the app's own "Start when I sign in to Windows" switch decides, so an
; upgrade never turns sign-in start back on.
Name: "startup"; Description: "Start Soltex when I sign in (keeps your taskbar, desktop and key settings working after a restart)"; Check: not IsUpgrade
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; The complete published folder, including its subfolders.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Directly in Start's app list (no folder), like most current apps.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start when I sign in to Windows" switch writes, so the switch shows it as on.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Soltex"; ValueData: """{app}\{#AppExeName}"" --tray"; Tasks: startup; Check: not IsUpgrade; Flags: uninsdeletevalue
; Removed on uninstall even when it was turned on later from inside the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Soltex"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Uninstall removes the program files, its shortcuts and the sign-in entry. Settings and the change
; history stay in %LOCALAPPDATA%\Soltex and HKCU\Software\Soltex, and the Full De-Windows (administrator)
; history in %ProgramData%\Soltex\System, so reinstalling keeps them and can still undo. A taskbar
; look stays in File Explorer, with the helper DLL from %LOCALAPPDATA%\Soltex\Taskbar loaded, until the
; user signs out or restarts; README.md says how to remove everything.

[Code]
const
  { Exit codes of Soltex.exe --quit (src/Soltex.App/App.Instance.cs). It waits up to 20 seconds itself. }
  QuitExited = 0;
  QuitNotRunning = 1;
  QuitCancelled = 2;
  QuitTimedOut = 3;
  { Soltex before 1.1 returns from --quit at once, so also wait for its program file to be free. }
  FileFreeTimeoutMs = 20000;

  { CreateFileW arguments and result. }
  WinGenericWrite = $40000000;
  WinFileShareRead = $00000001;
  WinOpenExisting = 3;
  WinFileAttributeNormal = $00000080;
  WinInvalidHandle = -1;

  { The key Setup writes for this AppId. }
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E1F0C6A-5B3D-4A77-9F2E-1C4D6B8A0E52}_is1';

var
  Upgrading: Boolean;

function WinCreateFile(lpFileName: String; dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile: Cardinal): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
procedure WinCloseHandle(hObject: Integer);
  external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
begin
  { Decided before anything is written. }
  Upgrading := RegValueExists(HKCU, UninstallKey, 'UninstallString');
  Result := True;
end;

function IsUpgrade(): Boolean;
begin
  Result := Upgrading;
end;

{ True while a running program has the file open, as Windows does with Soltex.exe while Soltex runs. }
function FileInUse(const FileName: String): Boolean;
var
  FileHandle: Integer;
begin
  Result := False;
  if not FileExists(FileName) then
    Exit;
  FileHandle := WinCreateFile(FileName, WinGenericWrite, WinFileShareRead, 0, WinOpenExisting, WinFileAttributeNormal, 0);
  if FileHandle = WinInvalidHandle then
    Result := True
  else
    WinCloseHandle(FileHandle);
end;

function WaitUntilFileFree(const FileName: String; TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while FileInUse(FileName) and (Waited < TimeoutMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not FileInUse(FileName);
end;

{ Asks a running Soltex to quit and waits until it has exited. Returns '' once it is not running,
  otherwise why not. Soltex.exe --quit never shows anything itself; the running copy may ask first
  while audio is playing. }
function QuitSoltex(const ExeName: String): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not FileExists(ExeName) then
    Exit;
  if not Exec(ExeName, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := QuitNotRunning;
  Log(Format('Soltex.exe --quit returned %d.', [ResultCode]));
  if ResultCode = QuitCancelled then
    Result := 'Soltex is still running because quitting was cancelled in Soltex.'
  else if ResultCode = QuitTimedOut then
    Result := 'Soltex is still running: it did not quit within 20 seconds. It may be waiting for an answer in its own window.'
  else if not WaitUntilFileFree(ExeName, FileFreeTimeoutMs) then
    Result := 'Soltex is still running.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Reason: String;
begin
  Result := '';
  { Upgrading: Soltex must have quit before its files can be replaced. A silent setup stops with exit code 7. }
  Reason := QuitSoltex(ExpandConstant('{app}\{#AppExeName}'));
  if Reason <> '' then
    Result := Reason + #13#10#13#10 + 'Quit Soltex from its icon in the notification area, then run Setup again.';
end;

function InitializeUninstall(): Boolean;
var
  Reason: String;
begin
  Result := True;
  if not UninstallSilent() then
    Result := MsgBox('Remove Soltex?' + #13#10#13#10 +
      'Changes Soltex made to Windows stay as they are. To undo them first, choose No, open Soltex and use History.' + #13#10#13#10 +
      'If you changed the taskbar look, set it to Windows default in Soltex first. Otherwise it stays until you sign out or restart Windows.',
      mbConfirmation, MB_YESNO) = IDYES;
  if not Result then
    Exit;
  { Soltex must have quit before its files can be removed. A silent uninstall stops with a non-zero exit code. }
  Reason := QuitSoltex(ExpandConstant('{app}\{#AppExeName}'));
  if Reason <> '' then
  begin
    Log('Uninstall stopped: ' + Reason);
    if not UninstallSilent() then
      MsgBox(Reason + #13#10#13#10 + 'Quit Soltex from its icon in the notification area, then uninstall again.', mbError, MB_OK);
    Result := False;
  end;
end;
