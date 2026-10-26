; Compile after scripts/Build.ps1 -Standalone.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{648D3F02-43C8-4C54-9FC6-4508759B56A8}
AppName=ADM
AppVersion={#AppVersion}
AppPublisher=ADM
DefaultDirName={localappdata}\Programs\ADM
DefaultGroupName=ADM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=ADM-Setup-win-x64
SetupIconFile=..\src\ADM.App\Assets\adm.ico
UninstallDisplayIcon={app}\ADM.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\ADM-standalone\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ADM"; Filename: "{app}\ADM.exe"
Name: "{autodesktop}\ADM"; Filename: "{app}\ADM.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ADM.exe"; Description: "Open ADM"; Flags: nowait postinstall skipifsilent

; No UninstallDelete section: preserve downloaded files and the queue in LocalAppData\ADM.
