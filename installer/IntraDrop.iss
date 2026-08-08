; IntraDrop 인스톨러 스크립트 (Inno Setup 6)
; 빌드 전에 다음을 먼저 실행:
;   dotnet publish src\IntraDrop\IntraDrop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

#define MyAppName "IntraDrop"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "yunhyok"
#define MyAppURL "https://github.com/yunhyok/IntraDrop"
#define MyAppExeName "IntraDrop.exe"
#define PublishDir "..\src\IntraDrop\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{7E3F9C1A-5B26-4D8E-9A47-D14C2B8E6F03}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=IntraDrop-Setup-{#MyAppVersion}
SetupIconFile=..\src\IntraDrop\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
AppMutex=IntraDrop_SingleInstance

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "IntraDrop"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent
