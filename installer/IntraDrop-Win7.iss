; IntraDrop 인스톨러 스크립트 - Windows 7 SP1+ 전용 (.NET Framework 4.8 기반)
; 빌드 전에 다음을 먼저 실행:
;   dotnet publish src\IntraDrop\IntraDrop.csproj -c Release -f net48
; 32비트/64비트 Windows 모두 지원 (AnyCPU)

#define MyAppName "IntraDrop"
#define MyAppVersion "1.8.0"
#define MyAppPublisher "yunhyok"
#define MyAppURL "https://github.com/yunhyok/IntraDrop"
#define MyAppExeName "IntraDrop.exe"
#define PublishDir "..\src\IntraDrop\bin\Release\net48\publish"

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
OutputBaseFilename=IntraDrop-Setup-{#MyAppVersion}-win7
SetupIconFile=..\src\IntraDrop\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
AppMutex=IntraDrop_SingleInstance
MinVersion=6.1sp1

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

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-explorer-integration"; Flags: runhidden waituntilterminated skipifdoesntexist

[Run]
; runasoriginaluser: 관리자 권한으로 실행되면 일반 탐색기에서의 드래그앤드롭이 차단(UIPI)됨
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// .NET Framework 4.8 설치 여부 확인 (Release >= 528040)
function IsDotNet48Installed(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', Release) and (Release >= 528040);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    if MsgBox('IntraDrop을 실행하려면 .NET Framework 4.8이 필요합니다.'#13#10 +
              '지금 다운로드 페이지를 여시겠습니까?'#13#10#13#10 +
              '.NET Framework 4.8 설치 후 이 설치 프로그램을 다시 실행해 주세요.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExecAsOriginalUser('open',
        'https://dotnet.microsoft.com/download/dotnet-framework/net48',
        '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
