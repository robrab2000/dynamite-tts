; Dynamite TTS — Inno Setup 6 script
; Built by publish.ps1 (pass /DMyAppVersion=x.y.z)

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Dynamite TTS"
#define MyAppPublisher "Rob Homewood"
#define MyAppURL "https://github.com/robrab2000/dynamite-tts"
#define MyAppExeName "DynamiteTts.exe"

[Setup]
AppId={{8F3C1A2E-7B9D-4E61-9C40-A1B2C3D4E5F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\DynamiteTTS
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=DynamiteTts-Setup-v{#MyAppVersion}
SetupIconFile=..\src\DynamiteTts\Resources\app_idle.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Windows 10 1809 (build 17763) or newer, 64-bit only
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user install (no admin). Matches tray-app norms (VS Code / Discord style).
PrivilegesRequired=lowest
CloseApplications=yes
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WindowsVersionNotSupported=Dynamite TTS requires Windows 10 version 1809 (October 2018) or newer, or Windows 11.
OnlyOnTheseArchitectures=Dynamite TTS requires a 64-bit version of Windows.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start Dynamite TTS when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
; Published app payload (exe, voices, kokoro.onnx). Strip debug/link leftovers.
Source: "..\artifacts\publish\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.pdb;*.lib;*.exp;*.iobj;*.ipdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Same Run key the app's Settings toggle uses
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "DynamiteTts"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(Version);

  if not IsWin64 then
  begin
    MsgBox('Dynamite TTS only supports 64-bit Windows (x64).', mbError, MB_OK);
    Result := False;
    exit;
  end;

  { MinVersion already enforces 10.0.17763; spell out a clearer message if somehow bypassed. }
  if (Version.Major < 10) or ((Version.Major = 10) and (Version.Build < 17763)) then
  begin
    MsgBox('Dynamite TTS requires Windows 10 version 1809 (build 17763) or newer, or Windows 11.', mbError, MB_OK);
    Result := False;
  end;
end;
