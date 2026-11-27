; Gizlet Launcher for Windows: a per-user installer that needs no administrator rights.
;
; scripts/package.ps1 compiles it with Inno Setup 7:
;   ISCC.exe /DAppVersion=1.2.3 /DArch=x64 /DSource=<published app> /DOutput=<folder> GizletLauncher.iss
; A signed release adds /DSign=1 and /Ssign=<signtool command>, which signs the uninstaller too.
;
; The program goes to %LOCALAPPDATA%\Programs\Gizlet Launcher. Installed apps, profiles, saves and
; settings live in %LOCALAPPDATA%\Gizlet, which installing, updating and uninstalling never touch.
; The Launcher updates itself by starting this installer with
;   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1
; and exiting; the installer waits for it to close, replaces the files and opens it again. When a
; file cannot be replaced, Inno Setup rolls back what it changed, so the old version still runs.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\artifacts\publish\win-x64"
#endif
#ifndef Output
  #define Output "..\artifacts\installer"
#endif

[Setup]
AppId={{5B0F2C3E-7A4D-4C8B-9E61-2F3A9D6C1E47}
AppName=Gizlet Launcher
AppVersion={#AppVersion}
AppVerName=Gizlet Launcher {#AppVersion}
AppPublisher=Gizlet
AppPublisherURL=https://gizlet.io
AppSupportURL=https://github.com/gizlet/windows
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Gizlet Launcher
VersionInfoCompany=Gizlet
DefaultDirName={localappdata}\Programs\Gizlet Launcher
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir={#Output}
OutputBaseFilename=GizletSetup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\Gizlet.Launcher\Assets\gizlet.ico
UninstallDisplayIcon={app}\Gizlet.Launcher.exe
UninstallDisplayName=Gizlet Launcher
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
CloseApplications=no
SetupMutex=GizletLauncherSetup
#ifdef Sign
SignTool=sign
SignedUninstaller=yes
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
en.LaunchNow=Open Gizlet Launcher
zh.LaunchNow=打开 Gizlet 启动器
en.StillRunning=Gizlet Launcher is still running. Close it and try again.
zh.StillRunning=Gizlet 启动器仍在运行，请关闭后重试。

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Gizlet Launcher"; Filename: "{app}\Gizlet.Launcher.exe"; AppUserModelID: "Gizlet.Launcher"

[Run]
Filename: "{app}\Gizlet.Launcher.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Gizlet.Launcher.exe"; Flags: nowait; Check: Relaunch

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ""Gizlet Launcher"" /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ The Launcher holds this mutex while it runs. An update starts right before the Launcher exits,
  so the installer waits for it instead of asking. }
function WaitForLauncher(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 4 do
  begin
    if not CheckForMutexes('Local\GizletLauncher') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Result := not CheckForMutexes('Local\GizletLauncher');
end;

function InitializeSetup: Boolean;
begin
  Result := WaitForLauncher(30);
  if not Result then
    SuppressibleMsgBox(CustomMessage('StillRunning'), mbError, MB_OK, IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForLauncher(5);
  if not Result then
    MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
end;
