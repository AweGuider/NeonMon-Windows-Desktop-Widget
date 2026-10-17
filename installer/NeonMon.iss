; Per-user installer (no admin prompt). The Release workflow builds it once per publish folder:
;   ISCC /DAppVersion=0.9.0 /DBuild=small|standalone installer\NeonMon.iss
; SourceDir and the output folder (/O) can be overridden for local builds.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Build
  #define Build "small"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\" + Build
#endif
#if Build == "standalone"
  #define Suffix "-standalone"
#else
  #define Suffix ""
#endif

#define AppGuid "EF798571-8B71-4A30-A7A4-AACFA61318EE"
#define AppName "NeonMon"
#define AppExe "NeonMon.exe"
#define RepositoryUrl "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget"

[Setup]
; AppId identifies NeonMon for upgrades and uninstall. Both builds share it, so either one replaces the other; never change it.
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=AweDev
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publish
OutputBaseFilename=NeonMon-{#AppVersion}{#Suffix}-setup
SetupIconFile=..\NeonMon.ico
UninstallDisplayIcon={app}\{#AppExe}
; Matches the winget PackageName, so winget can find the installed copy.
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern

[Tasks]
; Offered on first install only; afterwards the in-app setting owns this shortcut.
Name: "startup"; Description: "Start with Windows"; Flags: unchecked; Check: not IsUpgrade

[InstallDelete]
#if Build == "standalone"
; Files only the small build has, left behind when the standalone build replaces it.
Type: files; Name: "{app}\NeonMon.dll"
Type: files; Name: "{app}\NeonMon.deps.json"
Type: files; Name: "{app}\NeonMon.runtimeconfig.json"
Type: files; Name: "{app}\System.CodeDom.dll"
Type: files; Name: "{app}\System.Management.dll"
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
; Same path and target as the in-app "Start with Windows" setting (Services/StartupShortcut.cs).
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "{#AppName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent
; A silent upgrade (for example from winget) starts NeonMon again only if it was running.
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent; Check: WasRunning

[Code]
const
  InstanceMutex = 'Local\NeonMon.Instance';
  ExitSignal = 'Local\NeonMon.Exit';
  EVENT_MODIFY_STATE = $0002;

var
  NeonMonWasRunning: Boolean;

function OpenEvent(DesiredAccess: Cardinal; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsUpgrade: Boolean;
begin
  Result := RegValueExists(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1', 'UninstallString');
end;

function WasRunning: Boolean;
begin
  Result := NeonMonWasRunning;
  Log(Format('Restart NeonMon after a silent install: %d', [Ord(Result)]));
end;

// Signals the same exit event as "NeonMon.exe --exit", which also reaches a copy running from a zip elsewhere.
// Returns whether NeonMon was running.
function CloseNeonMon: Boolean;
var
  Event: THandle;
  Attempt: Integer;
begin
  Result := CheckForMutexes(InstanceMutex);
  Log(Format('NeonMon running: %d', [Ord(Result)]));
  if not Result then
    Exit;
  Event := OpenEvent(EVENT_MODIFY_STATE, False, ExitSignal);
  if Event <> 0 then
  begin
    SetEvent(Event);
    CloseHandle(Event);
  end;
  for Attempt := 1 to 50 do
  begin
    if not CheckForMutexes(InstanceMutex) then
    begin
      Log('NeonMon closed');
      Exit;
    end;
    Sleep(100);
  end;
  Log('NeonMon did not close within 5 seconds');
end;

#if Build == "small"
function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not WizardSilent and not DesktopRuntimeInstalled then
    Result := MsgBox('NeonMon needs the .NET 9 Desktop Runtime, which was not found.' + #13#10#13#10 +
      'Choose Yes to install anyway; NeonMon offers the runtime download when it starts. ' +
      'Choose No to cancel and use NeonMon-{#AppVersion}-standalone-setup.exe instead, which includes .NET.',
      mbConfirmation, MB_YESNO) = IDYES;
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeonMonWasRunning := CloseNeonMon;
  Result := '';
end;

// The in-app setting can create the Startup shortcut after install; remove it only when it points at this copy.
procedure DeleteStartupShortcut;
var
  Path: String;
  Shell, Link: Variant;
begin
  Path := ExpandConstant('{userstartup}\{#AppName}.lnk');
  if not FileExists(Path) then
    Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    if CompareText(Link.TargetPath, ExpandConstant('{app}\{#AppExe}')) = 0 then
      DeleteFile(Path);
  except
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseNeonMon;
    DeleteStartupShortcut;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // Settings and quota history stay unless the user asks; a silent uninstall always keeps them.
    DataDir := ExpandConstant('{localappdata}\{#AppName}');
    if not UninstallSilent and DirExists(DataDir) and
      (MsgBox('Also delete your NeonMon settings and quota history?' + #13#10#13#10 +
        'They are in ' + DataDir + '. Keep them if you plan to install NeonMon again.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
