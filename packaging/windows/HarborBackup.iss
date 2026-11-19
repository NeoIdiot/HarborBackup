; Build-Installer.ps1 supplies absolute, validated paths and one release version.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef ProductId
  #define ProductId "HarborBackup.Desktop"
#endif

[Setup]
AppId={#ProductId}
AppName=HarborBackup
AppVersion={#AppVersion}
AppPublisher=HarborBackup
VersionInfoVersion={#NumericVersion}
DefaultDirName={localappdata}\Programs\HarborBackup
DisableDirPage=yes
DefaultGroupName=HarborBackup
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=HarborBackup-{#AppVersion}-Setup-x64
UninstallDisplayIcon={app}\app\HarborBackup.Desktop.exe
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
UsePreviousAppDir=yes
#ifdef ValidationBuild
CreateUninstallRegKey=no
UninstallDisplayName=HarborBackup isolated validation
#endif

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\release.json"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: filesandordirs; Name: "{app}\app"
; Remove the obsolete private shared-runtime directory when upgrading from preview.6 or earlier.
Type: filesandordirs; Name: "{app}\runtime"

[Icons]
#ifndef ValidationBuild
; The stub activator keeps protocol-activated toasts in Notification Center without a COM server.
Name: "{autoprograms}\HarborBackup"; Filename: "{app}\app\HarborBackup.Desktop.exe"; AppUserModelID: "{#ProductId}"; AppUserModelToastActivatorCLSID: "AA1C93C1-A4B4-4786-84EA-738DE2E8F1CB"
Name: "{autodesktop}\HarborBackup"; Filename: "{app}\app\HarborBackup.Desktop.exe"; Tasks: desktopicon
#endif

[Registry]
#ifndef ValidationBuild
Root: HKCU; Subkey: "Software\Classes\harborbackup-alert"; ValueType: string; ValueData: "URL:HarborBackup notifications"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\harborbackup-alert"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
; Ignore the protocol payload and open only the read-only Issues page.
Root: HKCU; Subkey: "Software\Classes\harborbackup-alert\shell\open\command"; ValueType: string; ValueData: """{app}\app\HarborBackup.Desktop.exe"" --open-issues"
#endif

[Run]
Filename: "{app}\app\HarborBackup.Desktop.exe"; Description: "{cm:LaunchProgram,HarborBackup}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchApp

[Code]
const
  WAIT_OBJECT_0 = 0;
  WAIT_ABANDONED = $80;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  Maintenance: THandle;
  InitializationHandle: THandle;
  InitializationLease: String;
  DataPrepared: Boolean;
  DataCheckFailed: Boolean;

function CreateMutexW(Attributes: NativeInt; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function LockMaintenance: Boolean;
var
  Gate: THandle;
  WaitResult: Cardinal;
  Prefix: String;
begin
  Result := False;
  if Maintenance <> 0 then begin Result := True; exit; end;
  Prefix := 'Global\HarborBackup.' + Uppercase(GetUserNameString);
  Gate := CreateMutexW(0, False, Prefix + '.Startup');
  if Gate = 0 then exit;
  try
    WaitResult := WaitForSingleObject(Gate, 5000);
    if (WaitResult <> WAIT_OBJECT_0) and (WaitResult <> WAIT_ABANDONED) then exit;
    try
      if CheckForMutexes(Prefix + '.Maintenance') or CheckForMutexes(Prefix + '.Running') then exit;
      Maintenance := CreateMutexW(0, False, Prefix + '.Maintenance');
      Result := Maintenance <> 0;
    finally
      ReleaseMutex(Gate);
    end;
  finally
    CloseHandle(Gate);
  end;
end;

procedure UnlockMaintenance;
begin
  if InitializationHandle <> 0 then begin CloseHandle(InitializationHandle); InitializationHandle := 0; end;
  if Maintenance <> 0 then begin CloseHandle(Maintenance); Maintenance := 0; end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion, NewVersion: Int64;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\HarborBackup.Tray.exe')) then begin
    Result := 'Uninstall HarborBackup 0.4 before installing 0.5. The new layout does not import old configuration or recovery data.';
    exit;
  end;
  if not LockMaintenance then begin
    Result := 'HarborBackup is running or another installation is active. Exit from the tray menu, wait for safe shutdown, then retry. CLI operations must also finish.';
    exit;
  end;
  if GetPackedVersion(ExpandConstant('{app}\app\HarborBackup.Desktop.exe'), OldVersion) and StrToVersion('{#AppVersion}.0', NewVersion) then
    if ComparePackedVersion(OldVersion, NewVersion) > 0 then begin
      Result := 'A newer version is installed. Downgrades require a separate compatibility review.';
      UnlockMaintenance;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    DataPrepared := False;
    DataCheckFailed := True;
    { Keep maintenance held until the installed helper finishes checking or rebuilding data. }
    InitializationLease := 'Global\HarborBackup.' + Uppercase(GetUserNameString) + '.Initialize.' + Lowercase(GetSHA256OfString(ExpandConstant('{tmp}')));
    InitializationHandle := CreateMutexW(0, False, InitializationLease);
    if InitializationHandle = 0 then RaiseException('Unable to create the installation data-check lease.');
    Log('Checking the local project data format before starting HarborBackup.');
    if not Exec(ExpandConstant('{app}\app\HarborBackup.Cli.exe'),
      'installation-prepare --lease "' + InitializationLease + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Unable to start the installation data check. Original data is retained.');
    if ResultCode <> 0 then
      RaiseException('The installation data check failed. Original data is retained. Close programs holding configuration files and retry installation.');
    Log('Local data check completed. Current settings were preserved or old data was retained and new settings generated.');
    DataPrepared := True;
    DataCheckFailed := False;
    UnlockMaintenance;
  end;
end;

function CanLaunchApp: Boolean;
begin
  Result := DataPrepared;
end;

function GetCustomSetupExitCode: Integer;
begin
  if DataCheckFailed then Result := 10 else Result := 0;
end;

procedure DeinitializeSetup;
begin
  UnlockMaintenance;
end;

function InitializeUninstall: Boolean;
begin
  Result := LockMaintenance;
  if not Result then
    SuppressibleMsgBox('Exit HarborBackup from its tray menu and wait for CLI tasks to finish before uninstalling.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, OwnedPrefix: String;
begin
#ifndef ValidationBuild
  if CurUninstallStep = usUninstall then begin
    OwnedPrefix := '"' + ExpandConstant('{app}\app\HarborBackup.Desktop.exe') + '" ';
    if RegQueryStringValue(HKCU, RunKey, 'HarborBackup', Command) then
      if CompareText(Copy(Command, 1, Length(OwnedPrefix)), OwnedPrefix) = 0 then
        RegDeleteValue(HKCU, RunKey, 'HarborBackup');
  end;
#endif
end;

procedure DeinitializeUninstall;
begin
  UnlockMaintenance;
end;
