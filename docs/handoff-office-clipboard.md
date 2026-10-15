# Handoff: Office(Excel·PowerPoint)에서 자동 클립보드 공유 중 복사 취소·우클릭 취소 문제

작성: 2026-10-15, Claude (yunhyok/IntraDrop 세션). 이어받는 에이전트(Codex)를 위한 인수인계 문서.
기준 코드: `main` = v1.9.6 + PR #20. 최신 릴리스: [v1.9.6](https://github.com/yunhyok/IntraDrop/releases/tag/v1.9.6).

## TL;DR (English)

- Symptom (user report, Office only, not Explorer): in Excel, selecting a range and pressing Ctrl+C **immediately cancels the selection (marching ants)**, and the cell right-click menu **keeps closing**. Reported as recurring once a copy has been made ("복사를 한 번 실행하면 다시 재발"). PowerPoint right-click menus close too.
- v1.9.6 fixed one real cause (IntraDrop reading the clipboard while Office is interacting, which forces Excel's delayed render and dismisses the popup) but the user reports the symptom **persists on a PC running 1.9.6**.
- Since 1.9.3 IntraDrop refuses to apply a received clipboard while Excel owns the clipboard, so a 1.9.6 sender should never have its own clipboard rewritten by IntraDrop right after Ctrl+C. Remaining suspects: (1) an external clipboard echo (Windows "Sync across devices", RDP clipboard redirection, mouse/KVM sharing tools) bouncing the applied clipboard back from the receiving PC; (2) an IntraDrop path not yet found (self-registered peer, protection check failing on that PC's Office version); (3) receiver-side collisions.
- Next step is data, not code: run `tools/watch-clipboard.ps1` on the affected PC during a repro and read `owner=` / `formats=` per clipboard change. Then apply the matching fix from section 7 and release v1.9.7 (which must also carry PR #20).

## 1. 사용자 보고 (원문 요약)

1. "엑셀 같은 오피스 제품군 작업에서 복사 명령이 제대로 안 되거나 마우스 오른쪽 클릭이 계속 캔슬되는 문제. 이전 버전(1.8.x, 복사 후 대상 지정)에서는 문제 없었고, 1.9.0에서 모든 컴퓨터로 자동 전송하도록 바꾼 뒤 발생."
2. v1.9.6 설치 후 "사라졌다" → 곧 "다시 생김".
3. "복사를 한 번 실행하면 다시 재발하는 듯."
4. "엑셀에서 영역 설정하고 Ctrl+C 하는 순간에 영역이 cancel되고, 또 오른쪽 클릭하면 그게 또 cancel 됨."
5. "오른쪽 클릭 캔슬은 엑셀이나 파워포인트 등 오피스 제품군에서만 발생함. 일반 탐색기 등은 정상 작동함."
6. "증상이 나는 PC도 1.9.6입니다." (스크립트 로그와 나머지 정보는 아직 받지 못함)

아직 모르는 것: 증상 PC의 Office 버전, 상대 PC들의 IntraDrop 버전, 증상 PC 목록에 자기 자신이 등록돼 있는지, PC 간 클립보드 동기화·원격 데스크톱·마우스 공유 도구 사용 여부, 증상 시점에 다른 PC에서도 복사가 있었는지.

개발 PC(이 문서를 쓴 환경) 사실: Excel 2013(ProgID `Excel.Application.15`) 설치. IntraDrop 1.9.4가 `C:\Program Files\IntraDrop`에 설치·실행 중이며 **등록 피어가 없음**(설정 `Peers` 비어 있음) → 증상을 재현할 수 없는 환경. Windows 클립보드 기록은 켜져 있고(`EnableClipboardHistory=1`) 클라우드 동기화는 꺼져 있음. RDP·클립보드 도구 없음.

## 2. 관련 코드 지도

클립보드 자동 공유 경로 (v1.9.0 도입):

| 단계 | 코드 | 역할 |
|---|---|---|
| 감시 | `src/IntraDrop/UI/ClipboardMonitor.cs` | `AddClipboardFormatListener`로 `WM_CLIPBOARDUPDATE`(0x031D) 수신 → 150ms 디바운스 타이머 → `CaptureChange()`. `_sequence`(마지막으로 처리한 `GetClipboardSequenceNumber`)로 자기 쓰기·중복을 거른다. `ApplyReceived()`는 수신 적용 진입점. |
| 읽기 | `src/IntraDrop/UI/ClipboardReader.cs` | `CaptureAutomatic()`: Win32 `OpenClipboard(NULL)` → `DeniedFormats`(파일 계열 + 수신 마커 `IntraDrop.ReceivedClipboard`)가 있으면 null → `CF_UNICODETEXT`면 `GetClipboardData`로 읽음(여기서 Excel의 **지연 렌더 콜백**이 Excel 스레드에서 실행되고, 끝날 때까지 IntraDrop이 클립보드를 연 채 기다림) → 아니면 `CF_BITMAP`일 때만 OLE `Clipboard.GetDataObject()` 사용. `HasLocalExcelCopy()`(클립보드 소유 프로세스가 EXCEL), `HasActiveOfficeInteraction(includeHeldInput)`(전면 Office 앱의 팝업·대화상자·셀 편집·마우스 캡처·키 입력), `ShouldProtectLocalClipboard()`(둘의 OR, 수신 보호용). |
| 송신 | `src/IntraDrop/UI/TrayApplicationContext.cs` `BroadcastClipboard()` → `src/IntraDrop/Core/ClipboardBroadcaster.cs` → `TransferClient.SendClipboardAsync()` | 등록 피어 전원에게 전송. 자기 `DeviceId`와 같은 항목만 제외. 피어별 진행 중 1건 + 대기 최신 1건. |
| 수신 | `src/IntraDrop/Core/TransferServer.cs` `ReceiveClipboardAsync` → `ApplyClipboardAndAcknowledgeAsync` → `TrayApplicationContext.OnClipboardReceivedAsync` → `ClipboardMonitor.ApplyReceived` → `src/IntraDrop/UI/ClipboardService.cs` `Apply` | 검증 후 UI 스레드에서 `Clipboard.SetDataObject(data, copy: true)`로 적용. 적용 데이터에 마커 형식 `IntraDrop.ReceivedClipboard`를 함께 넣어 되돌이 전송을 막는다. 적용 성공 시 1바이트 ack(+암호 사용 시 서명된 `clipboard_applied` 영수증), 실패 시 0. |

버전별 보호 로직:

- 1.9.1: 파일·폴더 복사는 자동 전송하지 않음(`DeniedFormats`).
- 1.9.2: 자동 공유 실패는 팝업 없이 트레이 메뉴 상태·툴팁에만 표시.
- 1.9.3: **수신 적용 보호**. Excel이 클립보드를 소유하고 있으면(`HasLocalExcelCopy`) 받은 내용을 적용하지 않고 실패(0) 응답. 보내는 쪽 트레이에 "전송 실패"로 보임.
- 1.9.4: 수신 적용 보호 확장. Excel이 전면에서 메뉴·대화상자·셀 편집·마우스 캡처·Ctrl+C/X/V 조합 중이면 적용하지 않음(`IsOfficeInteraction`, 당시 이름 `IsExcelInteraction`).
- 1.9.6 (PR #21): **캡처(읽기) 보호**. `ClipboardMonitor.CaptureChange`가 `_deferCapture()`(기본 `HasActiveOfficeInteraction(includeHeldInput: true)`)가 true면 클립보드를 열지 않고 150ms 후 재확인. Excel·Word·PowerPoint 메인 창 클래스(`XLMAIN`, `OpusApp`, `PPTFrameClass`) 표로 일반화. 수신 보호는 기존의 좁은 규칙(Ctrl+C/X/V 조합만) 유지.

알아둘 사실:

- `ClipboardMonitor.ApplyReceived`는 **지연이 아니라 거부**다. 보호 조건이면 예외를 던지고, 서버가 실패 바이트를 보내며, 송신 측은 재시도하지 않는다("연결되지 않은 컴퓨터에는 다음에 새로 복사할 때 다시 전송").
- 수신 적용 후 `_sequence`를 갱신하고, 큐에 쌓인 `WM_CLIPBOARDUPDATE`는 시퀀스가 같아 무시된다 → IntraDrop끼리는 되돌이(echo)가 없어야 한다. 마커 형식도 `DeniedFormats`에 있어 다시 캡처되지 않는다.
- 서버는 `SenderDeviceId == 자기 DeviceId`를 거부하지 않는다. 자기 IP·이름이 피어로 등록돼 있으면(`TryPair`/`TryRegisterAuthenticated`는 자기 ID를 거부하므로 그 항목은 영원히 장치 ID 없는 legacy 행) 자기 자신에게 전송하고 자기 서버가 적용한다. 단 Excel이 소유 중이면 1.9.3 보호로 거부된다.
- Excel은 복사 모드(점선 테두리) 중 **다른 프로세스가 클립보드를 쓰면** 소유권을 잃고 복사 모드를 취소한다. 또 컨텍스트 메뉴가 열린 동안 클립보드가 바뀌거나(붙여넣기 옵션 갱신) 지연 렌더 요청이 오면 메뉴가 닫힌다. 탐색기는 이런 반응이 없다 → "오피스에서만" 증상.

## 3. 지금까지 한 조치

| 버전/PR | 내용 | 상태 |
|---|---|---|
| v1.9.5 ([#19](https://github.com/yunhyok/IntraDrop/pull/19)) | 코드 검토: `AppInfo` 버전 자동화(1.8.2로 표시되던 버그), 호스트명 등록 피어 허용 목록 수정, 설정 파일 원자적 저장, 로드 정화, Excel 창 판별 예외 | 릴리스됨 |
| v1.9.6 ([#21](https://github.com/yunhyok/IntraDrop/pull/21)) | 캡처 게이트(위 2절). 실제 Excel 2013으로 검증: 복사 → 실제 우클릭 → 메뉴 열린 동안 캡처 없음 → Esc → 1회 캡처, `CutCopyMode` 유지 (`tests/IntraDrop.Tests/ExcelClipboardTests.cs` `OutgoingCaptureWaitsUntilExcelContextMenuCloses`) | 릴리스됨. **사용자: 증상 재발** |
| [#20](https://github.com/yunhyok/IntraDrop/pull/20) | 간헐 실패 테스트 원인: 백신이 막 닫은 파일을 잠깐 쥐어 `Directory.Move(stage, batch)` 실패 → 2초 재시도. 실제 제품 버그(파일 클립보드 묶음 수신 실패) | main 머지됨, **미릴리스** → v1.9.7에 포함할 것 |
| `tools/watch-clipboard.ps1` | 수동 진단 스크립트(5절) | 이 문서와 같은 PR |

## 4. 현재 가설 (우선순위 순)

1. **외부 되돌이(echo) 루프.** 증상 PC(A)에서 복사 → IntraDrop이 B에 적용 → B의 클립보드가 다른 경로로 A에 되돌아옴(Windows 클립보드 "여러 디바이스에서 동기화", 원격 데스크톱 클립보드 리디렉션(`rdpclip`), Mouse Without Borders·Logitech Flow·Synergy·Barrier 등) → A의 Excel이 소유권을 잃어 복사 모드 취소. 되돌아온 내용은 마커가 없으므로 A의 IntraDrop이 다시 캡처·전송 → B 적용 → 다시 되돌아옴 … 반복되면 우클릭 메뉴도 계속 닫힌다. "복사 한 번에 재발", "오피스만", "1.8.x(수동)에서는 없음"과 모두 맞는다.
2. **IntraDrop의 자기 적용.** A의 목록에 A 자신(자기 IP·이름, DHCP로 바뀐 IP)이 등록돼 있어 자기 서버가 적용. 1.9.3 보호(`HasLocalExcelCopy`)가 그 PC의 Office에서 **동작하지 않는다면**(예: `GetClipboardOwner`가 EXCEL 프로세스 창을 돌려주지 않는 환경) 바로 취소가 난다. 스크립트 로그의 `owner=` 컬럼으로 Excel 복사 직후 소유자가 `EXCEL(pid)`인지 확인하면 판별된다.
3. **수신 측 충돌.** A가 받는 쪽이고 상대가 동시에 복사하는 경우. 수신 보호는 "거부"이고 Ctrl 단독·마우스 버튼은 보지 않아 우클릭 직전(버튼 뗀 뒤 팝업 생성 전)이나 Ctrl+C 직전에 적용이 끼어들 수 있다. "순간 취소"가 매번 난다는 설명과는 덜 맞는다.
4. **아직 못 찾은 경로.** 1.9.6 송신 경로에는 클립보드 쓰기가 없다. 가능성이 낮지만 Office 버전별 창 클래스 차이로 게이트가 안 걸리는 경우(예: 메인 창 클래스가 표와 다름) → 1.9.4식 지연 렌더 방해가 남는다. `HasActiveOfficeInteraction`에 디버그 출력을 넣어 확인할 수 있다.

## 5. 진단 절차 (코드보다 먼저)

1. 증상 PC에서 실행(저장소 없어도 됨):

   ```powershell
   powershell -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/yunhyok/IntraDrop/main/tools/watch-clipboard.ps1 -OutFile $env:TEMP\watch-clipboard.ps1; & $env:TEMP\watch-clipboard.ps1"
   ```

   Excel에서 영역 선택 → Ctrl+C → 우클릭을 2~3회 재현 → 창에서 Ctrl+C로 중단 → `%TEMP%\IntraDrop-clipboard-watch.log`를 받는다. 스크립트는 `GetClipboardSequenceNumber`·`GetClipboardOwner`·`IsClipboardFormatAvailable`만 쓰므로 내용을 읽지 않고 지연 렌더를 유발하지 않는다.

2. 로그 해석:

   | 관찰 | 의미 | 조치 |
   |---|---|---|
   | Excel 복사 직후 수백 ms 안에 `owner=`가 `EXCEL`에서 `IntraDrop`으로 바뀌고 `formats=`에 `IntraDrop-received` | IntraDrop이 자기 PC에 적용(자기 등록 또는 보호 실패) | 7-B, 7-C |
   | `owner=`가 `svchost`/`ShellExperienceHost`/`rdpclip`/기타 도구 프로세스로 바뀜 | 외부 동기화 되돌이 | 7-A + 사용자에게 해당 기능 끄기 안내 |
   | 같은 패턴이 1초 안팎 간격으로 반복 | 루프 확정 | 7-A 필수 |
   | 소유자 변경 없이 메뉴만 닫힘 | 읽기(지연 렌더) 방해가 남아 있음 → 게이트가 안 걸리는 상태 | 7-D, 창 클래스 확인 |
   | Excel 복사 직후 `owner=`가 `EXCEL`이 아님 | `HasLocalExcelCopy` 전제 깨짐 | 7-C |

3. 사용자에게 확인: 증상 PC 컴퓨터 목록에 자기 자신 등록 여부, PC 간 동기화·RDP·마우스 공유 도구, Office 버전, 상대 PC들의 IntraDrop 버전, 증상 때 트레이 메뉴 "클립보드 자동 공유" 상태 텍스트.

## 6. 재현·검증 환경

- 비대화형 테스트: `dotnet test tests/IntraDrop.Tests/IntraDrop.Tests.csproj -c Release --filter "Category!=InteractiveShell&Category!=InteractiveExcel"` (현재 73개, 전부 통과. PR #20 이전에는 `FilesPreserveDuplicateRootsAndEmptyFoldersInsideOneBatch`가 간헐 실패).
- 실제 Excel 검사: `--filter "FullyQualifiedName~ExcelClipboardTests"` (Category `InteractiveExcel`). Excel COM으로 통합 문서를 만들고 닫으며, 새 테스트는 창을 보이게 띄워 **실제 마우스 입력**으로 우클릭한다. 실행 전 **실행 중인 IntraDrop을 종료**(`taskkill /IM IntraDrop.exe /F`)해 테스트 클립보드가 다른 PC로 전송되지 않게 하고, 끝나면 `C:\Program Files\IntraDrop\IntraDrop.exe`를 다시 실행한다.
- 클립보드 실제 검사: `--filter "Category=InteractiveShell"` (실제 클립보드·보내기 폴더를 건드림).
- 빌드 출력의 GUI 앱을 직접 실행하지 말 것: 시작 시 자동 실행 레지스트리와 보내기 바로 가기를 그 exe 경로로 다시 쓴다.
- CI는 태그 푸시(`v*`)에서만 돈다(`.github/workflows/release.yml`): 테스트 → net8/net48 publish → Inno Setup 인스톨러 2종 → GitHub Release. 약 3분.

## 7. 후보 조치 (로그 결과에 따라 선택, 코드 위치 포함)

- **A. 되돌이 루프 차단 (어느 가설이든 유효한 방어).**
  - `ClipboardMonitor`: 마지막으로 적용한 수신 내용(`format`+`bytes`)을 기억하고, `CaptureChange`에서 캡처한 내용이 그것과 같으면 `_copied`를 호출하지 않는다(`_sequence`는 갱신). 되돌아온 내용의 재전송을 막는다.
  - `ClipboardMonitor.ApplyReceived` / `ClipboardService.Apply`: 받은 내용이 현재 클립보드의 텍스트와 같으면 쓰지 않고 성공으로 처리(쓰기를 안 하면 되돌이도 없다). 현재 텍스트 비교는 `ClipboardReader.CaptureAutomatic()`를 재사용(이미 쓰려던 시점이므로 읽기 비용 허용).
  - 주의: README는 "같은 내용을 다시 복사하면 다시 전송"을 약속한다(연결 실패 복구). 직전 *수신* 내용과 같은 경우만 걸러야 하며 직전 *송신* 내용과 비교해서는 안 된다.
- **B. 자기 전송 차단.** `TransferServer.HandleClientAsync`에서 `header.SenderDeviceId`가 자기 `settings.DeviceId`와 같으면 `StatusRefused`. `TrayApplicationContext.BroadcastClipboard`에서 호스트가 로컬 주소(`NetworkInterface` 유니캐스트 목록·루프백·자기 호스트명)로 해석되는 피어는 제외. 목록 창에서 자기 자신 등록을 경고.
- **C. 보호 판별 강화.** `HasLocalExcelCopy`가 Office 버전에 따라 실패할 수 있으면 `GetClipboardOwner` 외에 `CF_UNICODETEXT`와 함께 Office 전용 형식(예: `Biff12`, `XML Spreadsheet`, `Link`)의 존재를 `IsClipboardFormatAvailable`로 확인해 Excel 소유로 간주. PowerPoint(`PowerPoint 12.0 Internal Slides` 등)도 같은 방식.
- **D. 캡처 게이트 보강.** `GetLastInputInfo`로 마지막 입력 후 300ms 미만이면 지연(우클릭 버튼을 뗀 직후부터 팝업 생성 사이의 틈을 없앰). 메인 창 클래스 표가 안 맞는 Office 버전 대응: 전면 프로세스가 Office면 `GUI_INMENUMODE|GUI_POPUPMENUMODE|GUI_SYSTEMMENUMODE`와 활성 창 클래스 변화만으로 판단하도록 완화.
- **E. 수신 보호를 거부 대신 짧은 지연으로.** `OnClipboardReceivedAsync`에서 최대 2~3초 동안 150ms 간격으로 `HasActiveOfficeInteraction(includeHeldInput: true)`가 풀리길 기다린 뒤 적용(`HasLocalExcelCopy`는 즉시 거부 유지). 송신 측 ack 대기는 120초(`TransferClient.AckWaitMs`)라 여유가 있다.
- **F. 사용자 설정.** "Office 앱이 전면일 때 자동 공유 안 함" 또는 앱별 제외. 1.8.x식 수동 "클립보드 전달 → 대상" 메뉴 복원은 1.9.0에서 제거됐고(`ClipboardUiTests.PortableFormatsAndAutomaticSharingMenu`가 없음을 단언) 마지막 수단.
- **G. 앱 내 진단 로그.** 스크립트로 부족하면 `%LOCALAPPDATA%\IntraDrop\clipboard.log`에 캡처 결정(지연 사유)·적용·거부를 기록하는 작은 트레이스를 넣는다.

## 8. 릴리스 절차 (v1.9.7)

1. 버전 문자열: `src/IntraDrop/IntraDrop.csproj` `<Version>`, `installer/IntraDrop.iss`·`installer/IntraDrop-Win7.iss` `MyAppVersion`, `README.md`·`README.companion.html`의 인스톨러 파일명, `src/IntraDrop/Help/index.html` 제목·헤더·푸터. 기능 설명 문단은 README 클립보드 절과 Help 클립보드 섹션에 "v1.9.7에서는 …" 형식으로 추가.
2. PR 제목 관행: `<요약> (v1.9.7)`, squash 머지. 커밋 본문에 원인·조치·검증.
3. `git tag -a v1.9.7 -m "IntraDrop v1.9.7 - ..."` → `git push origin v1.9.7` → Actions 완료 후 Release 자산 2개 확인.
4. PR #20이 이미 main에 있으므로 v1.9.7 릴리스 노트에 함께 언급.

## 9. 참고

- 1.9.5 PR 본문에 코드 검토 메모, 1.9.6 PR 본문에 원인 분석이 더 있다.
- Excel 지연 렌더·클립보드 소유권·Office 컨텍스트 메뉴 동작의 근거는 PR #21 본문과 `ClipboardReader.cs` 주석 참조.
- 사용자(한국어)는 이 세션에서 Excel을 열어 실험하는 것을 허용했다.
