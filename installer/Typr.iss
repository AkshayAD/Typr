; Build through tools/Build-Installer.ps1 so the payload is a fresh win-x64 publish.
#ifndef PublishDir
  #error PublishDir must point to a self-contained, multi-file win-x64 publish.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef FileVersion
  #define FileVersion "0.1.1.0"
#endif

[Setup]
; Keep this identity unchanged across releases so upgrades reuse the uninstall log.
AppId={{C64D5375-C6FC-4864-93F3-6E50D2747A08}
AppName=Typr
AppVersion={#AppVersion}
AppVerName=Typr {#AppVersion}
VersionInfoVersion={#FileVersion}
VersionInfoDescription=Typr Setup
DefaultDirName={localappdata}\Programs\Typr
DefaultGroupName=Typr
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Typr-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\Typr.Windows\Assets\Typr.ico
UninstallDisplayIcon={app}\Typr.exe
UninstallDisplayName=Typr
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
AppMutex=Local\Typr.Instance
CloseApplications=no
RestartApplications=no
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\INSTALLATION.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Typr"; Filename: "{app}\Typr.exe"; WorkingDir: "{app}"; Comment: "Your voice, your words"; AppUserModelID: "Typr.Desktop"
Name: "{userdesktop}\Typr"; Filename: "{app}\Typr.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "Typr.Desktop"

[Run]
Filename: "{app}\Typr.exe"; Description: "Launch Typr"; Flags: nowait postinstall skipifsilent unchecked

; Deliberately no [UninstallDelete] entries: user settings, models, providers,
; audio, diagnostics and history live outside {app} and must survive uninstall.
