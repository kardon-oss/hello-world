; =====================================================================================
;  OpenMouseConfig — Inno Setup script (requires Inno Setup 6.3 or newer)
;
;  Normally built by installer\build-installer.ps1, which publishes the app first:
;      powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.1.0
;
;  Manual build (after `dotnet publish` into artifacts\publish\win-x64):
;      "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer\OpenMouseConfig.iss
;
;  Output: artifacts\installer\OpenMouseConfig-Setup-<version>-x64.exe
; =====================================================================================

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; Folder produced by `dotnet publish -r win-x64 --self-contained` (relative to this .iss file).
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#define AppName       "OpenMouseConfig"
#define AppPublisher  "OpenMouseConfig contributors"
#define AppExeName    "OpenMouseConfig.exe"
; Update AppUrl if the repository is renamed.
#define AppUrl        "https://github.com/kardon-oss/hello-world"

[Setup]
; Never change AppId after the first public release — upgrades and uninstall rely on it.
AppId={{6F1C2B7E-4A3D-4F7B-9C1E-8D2A5B3C7E91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

; Per-user install by default (no UAC prompt, files stay user-editable); the user can still
; choose "install for all users" in the first dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; The published app is x64-only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
; Compress in a separate (64-bit) process: the 32-bit Inno Setup 6 compiler otherwise runs out
; of address space on the ~150 MB self-contained .NET payload.
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; Close a running instance so OpenMouseConfig.exe can be replaced.
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output (app + .NET runtime). devices.json is included and is
; deliberately overwritten on upgrade: users keep their own definitions in
; %APPDATA%\OpenMouseConfig\devices\, which the installer never touches.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; User data (%APPDATA%\OpenMouseConfig: settings + custom definitions) is created by the app on
; first launch, per Windows user, and intentionally left in place on uninstall so a reinstall
; keeps everything.
