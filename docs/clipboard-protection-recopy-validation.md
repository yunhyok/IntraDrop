# Clipboard protection and deliberate re-copy — 1.10.5

## Change

Excel clipboard ownership alone no longer means a live Copy/Cut operation. Before replacing an Excel-owned clipboard, a hidden instance of the same executable reads the owning Excel instance's `Application.CutCopyMode`. Mode 0 permits replacement; Copy, Cut, an unknown result, or a busy/unavailable Excel instance remain protected. The helper has a two-second deadline and only that helper is terminated on timeout. It never sets `CutCopyMode`, activates a workbook, or reads clipboard contents.

The observation is bound to the clipboard sequence, owner window and process ID. The receiver checks these again after the asynchronous query and during clipboard write retries. Menu, cell-edit, mouse and shortcut protection remains active. Reset, a new local copy, or a more recent incoming copy invalidates a pending receive. Actual Copy/Cut can intentionally keep receiving blocked until that operation ends; protection is not expired by a timer.

Clipboard echo classification preserves foreground ownership observed for the same sequence and owner before delayed capture. Changing focus while Excel defers capture does not turn a deliberate re-copy into a background echo. An intervening local copy also expires previous received-content suppression. Background duplicates and received-format markers still prevent echo traffic.

## Validation on 2026-10-29

- `dotnet test tests/IntraDrop.Tests/IntraDrop.Tests.csproj -c Release --filter "Category!=InteractiveShell&Category!=InteractiveExcel"`: 123 passed, 0 failed.
- New automated coverage: idle/Copy/Cut/unknown result matching, stale owner/sequence/PID rejection, malformed helper arguments, helper timeout/cancellation/exit, deferred foreground re-copy, bridge duplicates, new sequence/owner isolation, and receive-A → local-B → local-A.
- Both `net8.0-windows` and `net48` production builds and publish succeeded.
- Both published executables were started with invalid probe arguments; each exited with code 3 before normal startup or single-instance handling.
- Windows 10/11 x64 and Windows 7 installers were built locally as version 1.10.5 before publication. Desktop installation was not performed during this validation.
- Independent source review checked the native helper, owner matching, echo state and asynchronous receive integration. A concurrent receive issue was addressed by accepting only the newest pending receive.

## Not verified here

Native clipboard and actual Excel tests were not run against the user's desktop. A separate window station was attempted to isolate its clipboard, but Windows rejected `CreateWindowStation` with Win32 error 5 even with minimum access. No isolated Excel process started and no live clipboard fallback was used. Temporary isolation tooling is retained under the ignored `bin/isolated-clipboard` directory for a host that permits it.

Actual Excel 2013 native-object lookup, foreground menu noninterference, and installation on Windows 7 still require a desktop validation run. Newly updated native regressions (background echo simulation and newest pending receive) are included in `ClipboardUiTests`, but were excluded from the 123-test run. If Excel does not expose its native object model or responds too slowly, the application deliberately keeps protection and logs `Excel copy probe unknown`, `timeout`, or `unavailable`.

## Platform references

Windows clipboard ownership lasts beyond an individual copy action; it is not an Excel copy-mode indicator. [Microsoft: Clipboard Operations](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations).

The helper reads the documented false/copy/cut state through Excel's existing `EXCEL7` native object model. [Microsoft: CutCopyMode](https://learn.microsoft.com/en-us/office/vba/api/excel.application.cutcopymode), [Microsoft: AccessibleObjectFromWindow](https://learn.microsoft.com/en-us/previous-versions/ms696137(v=vs.85)).

A window station has its own clipboard, which is why native validation attempted a separate station instead of changing the user's clipboard. [Microsoft: Window Stations and Desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/about-window-stations-and-desktops).
