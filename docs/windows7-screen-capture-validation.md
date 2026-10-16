# Windows 7 영역 캡처 검증

## 동작과 포함 범위

- `net48` 빌드에만 캡처 코드를 포함하며, 실행 환경이 Windows NT 6.1일 때만 `Win+Shift+S`를 등록한다. `net8.0-windows` 실행 파일에는 캡처 타입과 등록 코드가 없다.
- 단축키를 누른 시점의 가상 데스크톱을 먼저 복사하고 영역 선택 창을 표시한다. 선택한 원본 픽셀을 클립보드에 지속 저장한다. v1.10.0에서는 기존 이미지 복사와 같이 등록된 컴퓨터로 자동 공유하며 파일은 자동 저장하지 않는다.
- 역방향 드래그, 주 모니터 왼쪽·위쪽 좌표, 화면 경계 밖으로 나간 드래그를 처리한다. `Esc`, 오른쪽 클릭, 빈 영역, 창 전환, 마우스 캡처 상실, 화면 배치 변경은 취소한다.
- 단축키 등록 및 화면·클립보드 접근 실패는 알림으로 표시한다. 앱 종료 시 선택 창과 전역 단축키를 정리한다.

## 2026-10-16 v1.10.0 릴리스 검증

환경: Windows 10 22H2, .NET SDK 8.0.426, .NET Framework CLR 4.0.30319.42000.

- `dotnet build src/IntraDrop/IntraDrop.csproj -c Release`: 두 타깃 모두 성공, 경고·오류 0개.
- `dotnet test tests/IntraDrop.Tests/IntraDrop.Tests.csproj -c Release --filter "Category!=InteractiveShell&Category!=InteractiveExcel"`: 90개 통과.
- `dotnet test tests/IntraDrop.Tests/IntraDrop.Tests.csproj -c Release --filter "FullyQualifiedName~ScreenCaptureTests|FullyQualifiedName~WindowsSendTo_PassesDownloadedExeAndMultipleFilesWithoutExecutingSelection"`: 캡처 18개 및 탐색기 보내기 1개, 합계 19개 통과. OS 제한·현대 빌드 제외·좌표/역방향 드래그·원본 픽셀/음영·빈 영역/취소·자동 공유 가능 형식 및 실제 화면 2×2 픽셀 읽기 포함. 캡처 검사는 시스템 클립보드를 변경하지 않는다.
- 실제 `net48` 실행 파일을 Windows PowerShell의 .NET Framework 런타임에서 읽어 OS 제한과 화면 2×2 픽셀 읽기를 별도로 확인했다. Windows 7에서 실행한 검증은 아니다.
- 독립 코드 검토에서 `Graphics.CopyFromScreen`이 `SourceCopy | CaptureBlt` 조합을 거부하는 문제를 발견·재현했다. 레이어 창을 포함하는 [BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt) 호출로 수정하고 두 런타임에서 확인했다.

## Windows 7 실기기 확인 항목

1. Windows 7 SP1 + .NET Framework 4.8에서 IntraDrop을 실행하고, 다른 앱에 포커스를 둔 채 `Win+Shift+S`를 누른다. 한 번의 선택 창만 열리고 S를 길게 눌러도 중복되지 않아야 한다.
2. 네 방향으로 드래그한 뒤 그림판과 문서에 `Ctrl+V`로 붙여넣어 크기·내용이 맞고 선택 테두리·음영이 포함되지 않는지 확인한다. 앱을 종료한 뒤에도 붙여넣기가 되어야 한다.
3. 기존 텍스트를 복사한 뒤 `Esc`·오른쪽 클릭·제자리 클릭·선 모양 드래그·Alt+Tab으로 취소한다. 기존 텍스트가 유지되어야 한다.
4. 시스템 배율 100%·125%·150%에서, 보조 모니터가 왼쪽/위쪽인 구성과 모니터 사이를 가로지르는 선택을 확인한다. Windows 7은 로그인 시 시스템 배율을 따른다.
5. 선택 중 모니터 연결/해상도를 바꾸면 취소되는지, 기본/Aero 테마에서 반투명 창도 캡처되는지 확인한다.
6. 다른 앱이 같은 단축키를 등록한 경우 실패 알림이 나타나고 기존 파일 전송은 정상인지 확인한다. 충돌 앱을 종료하고 IntraDrop을 재시작하면 등록되어야 한다. [RegisterHotKey의 등록 실패 조건](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)을 따른다.
7. 캡처 이미지가 기존 자동 공유 경로로 등록된 Windows 10/11 PC에 전달되는지 확인하고 붙여넣는다. 기존 이미지 전송 한도는 그대로 적용된다.
