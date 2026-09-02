; IntraDrop 인스톨러 스크립트 - Windows 10/11용 (Inno Setup 6)
; Windows 7용은 IntraDrop-Win7.iss 를 사용할 것.
; 빌드 전에 다음을 먼저 실행:
;   dotnet publish src\IntraDrop\IntraDrop.csproj -c Release -f net8.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true

#define MyAppName "IntraDrop"
#define MyAppVersion "1.6.2"
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
; .NET 8 기반이므로 Windows 10 (1607)+ 전용
MinVersion=10.0.14393

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "IntraDrop"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

Root: HKCU; Subkey: "Software\Classes\AllFilesystemObjects\shell\IntraDrop"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\IntraDrop.ContextMenu"; Flags: uninsdeletekey

[Run]
; runasoriginaluser: 관리자 권한으로 실행되면 일반 탐색기에서의 드래그앤드롭이 차단(UIPI)되므로
; 반드시 원래 사용자 권한으로 실행한다.
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser
