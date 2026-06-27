; AudioStreamer Installer Script
; Requires: Inno Setup 6  (https://jrsoftware.org/isinfo.php)
; Build:    .\build-installer.ps1  (from repo root)
;
; Produces a per-user installer (no UAC prompt) that installs to
; %LOCALAPPDATA%\AudioStreamer.  The app writes its config.json next to the
; executable (i.e. into the install folder), so the per-user location keeps it
; writable; the uninstaller removes it.

#define MyAppName      "AudioStreamer"
; Version is read at compile time from the published executable, so the
; .csproj <Version> (overridable via build-installer.ps1 -Version) is the
; single source of truth. Requires ..\publish\AudioStreamer.exe to exist first —
; build-installer.ps1 runs `dotnet publish` before invoking ISCC.
#define MyAppVersion   GetVersionNumbersString(AddBackslash(SourcePath) + "..\publish\AudioStreamer.exe")
#define MyAppPublisher "DoctorKomodo"
#define MyAppURL       "https://github.com/DoctorKomodo/AudioStreamer"
#define MyAppExeName   "AudioStreamer.exe"

[Setup]
; NOTE: The AppId value uniquely identifies this application.
; Do not change it once the installer has been distributed.
AppId={{F9D28724-EEF6-40FF-99DA-B04F7D743854}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Install to %LOCALAPPDATA%\AudioStreamer — no UAC prompt required.
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

; Installer output
OutputDir=Output
OutputBaseFilename=AudioStreamer-Setup
SetupIconFile=..\Resources\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Copies everything produced by `dotnet publish` into {app}.
Source: "..\publish\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; WorkingDir is set to {app} so the app writes config.json into the install
; folder (where it expects it) when launched from the shortcut.
Name: "{userstartmenu}\{#MyAppName}"; \
  Filename: "{app}\{#MyAppExeName}"; \
  WorkingDir: "{app}"
Name: "{userstartmenu}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Offer to launch the app immediately after installation completes.
Filename: "{app}\{#MyAppExeName}"; \
  WorkingDir: "{app}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Terminate any running instance before the uninstaller removes files.
Filename: "taskkill"; \
  Parameters: "/IM {#MyAppExeName} /F"; \
  Flags: runhidden; \
  RunOnceId: "KillAudioStreamer"

[UninstallDelete]
; Remove the runtime-generated config and the install folder if now empty.
Type: files;     Name: "{app}\config.json"
Type: dirifempty; Name: "{app}"

[Code]
// ---------------------------------------------------------------------------
// .NET 10 Windows Desktop Runtime check
// ---------------------------------------------------------------------------
// dotnet writes a subkey per installed version under this path (e.g. "10.0.0").
const
  DotNetRegKey =
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

function IsDotNet10Installed(): Boolean;
var
  Names: TArrayOfString;
  I:     Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM, DotNetRegKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    // Version subkeys start with "10." (e.g. "10.0.0", "10.0.1").
    if Pos('10.', Names[I]) = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet10Installed() then
  begin
    if MsgBox(
        '.NET 10 Windows Desktop Runtime is required but does not appear to be installed.'
        + #13#10#13#10
        + 'Download it from:'
        + #13#10
        + 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0'
        + #13#10#13#10
        + 'Install it first, then run this installer again.'
        + #13#10#13#10
        + 'Continue installation anyway?',
        mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
