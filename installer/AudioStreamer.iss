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

[Tasks]
; Final-page checkbox (checked by default). The app's "Start with Windows" toggle
; manages the same registry value, so it stays in sync after installation.
Name: "startup"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Startup:"

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

[Registry]
; Matches exactly what StartupService.Enable() writes, so the app's toggle reads
; the correct state immediately after the first launch.
Root: HKCU; \
  Subkey:    "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; \
  ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Flags:     uninsdeletevalue; \
  Tasks:     startup

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

; No .NET runtime check here: a 32-bit Inno installer reading HKLM gets redirected to
; WOW6432Node, where the x64 desktop runtime isn't listed, so the check false-positived on
; machines that already had it. The framework-dependent apphost shows its own "install .NET"
; prompt (with a download link) on first launch if the runtime is genuinely missing.
