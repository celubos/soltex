; Soltex per-user installer (Inno Setup 6).
;
; The app runs unelevated, so the installer targets the per-user Programs folder
; and never asks for administrator rights. It installs the whole published folder
; (Soltex.exe with its runtime, TaskbarHelper\, Soltex.Audio.Host\, licenses\ and
; the notices).
;
; Built by eng\publish-release.ps1 or by the release workflow:
;
;   ISCC.exe /DPublishDir=<app folder> /DAppVersion=<x.y.z> /DOutputDir=<dir> eng\soltex.iss

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
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
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] for your account. No administrator rights are needed.%n%nNothing in Windows changes until you turn it on in the app, and every change can be undone from History.
FinishedLabel=Soltex is installed. Find it in the Start menu, or in the notification area while it runs.

[Tasks]
Name: "startup"; Description: "Start Soltex when I sign in (keeps your taskbar, desktop and key settings working after a restart)"
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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Soltex"; ValueData: """{app}\{#AppExeName}"" --tray"; Tasks: startup; Flags: uninsdeletevalue
; Removed on uninstall even when it was turned on later from inside the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Soltex"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Ask a running copy to quit (it asks first if audio is playing). Does nothing when Soltex is not running.
Filename: "{app}\{#AppExeName}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitSoltex"

; Uninstall removes the program files and its shortcuts only. Settings and the change
; history stay in %LOCALAPPDATA%\Soltex, so reinstalling keeps them; delete that folder
; to remove them too.

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingExe: String;
  ResultCode: Integer;
begin
  Result := '';
  { Upgrading: ask the running copy to quit so its files can be replaced. }
  ExistingExe := ExpandConstant('{app}\{#AppExeName}');
  if FileExists(ExistingExe) then
  begin
    Exec(ExistingExe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent() then
    Result := MsgBox('Remove Soltex?' + #13#10#13#10 +
      'Changes Soltex made to Windows stay as they are. To undo them first, choose No, open Soltex and use History.',
      mbConfirmation, MB_YESNO) = IDYES;
end;
