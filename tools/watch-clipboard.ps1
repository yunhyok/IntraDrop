# 클립보드 소유자·형식과 전면 창·메뉴 상태 변화를 기록한다. 클립보드 내용과 창 제목은 읽지 않는다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\watch-clipboard.ps1 [-Seconds 60] [-LogPath 경로]
#   증상이 나는 PC 에서 실행한 뒤 Excel 에서 영역을 잡고 Ctrl+C, 우클릭을 해 보고, 로그 파일(기본 %TEMP%\IntraDrop-clipboard-watch.log)을 보낸다. Ctrl+C 로 중단.
param([int]$Seconds = 0, [string]$LogPath = (Join-Path $env:TEMP 'IntraDrop-clipboard-watch.log'))
Add-Type -Namespace Clip -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
[DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int capacity);
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
[DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
[DllImport("user32.dll")] public static extern int CountClipboardFormats();
[StructLayout(LayoutKind.Sequential)] public struct GuiThreadInfo {
    public int Size; public uint Flags;
    public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
    public int Left, Top, Right, Bottom;
}
'@
function WindowPid($handle) {
    $id = 0; [void][Clip.Native]::GetWindowThreadProcessId($handle, [ref]$id)
    return $id
}
function Proc($handle) {
    $id = WindowPid $handle
    if ($id -eq 0) { return 'none' }
    $name = (Get-Process -Id $id -ErrorAction SilentlyContinue).ProcessName
    if ($name) { "$name($id)" } else { "pid$id" }
}
function Class($handle) {
    if ($handle -eq [IntPtr]::Zero) { return '-' }
    $name = New-Object Text.StringBuilder 128
    [void][Clip.Native]::GetClassName($handle, $name, $name.Capacity)
    if ($name.Length) { $name.ToString() } else { '-' }
}
function Held {
    $names = @()
    if ([Clip.Native]::GetAsyncKeyState(0x11) -lt 0) {
        $names += 'Ctrl'
        foreach ($item in @(@(0x43,'C'), @(0x58,'X'), @(0x56,'V'))) {
            if ([Clip.Native]::GetAsyncKeyState($item[0]) -lt 0) { $names += $item[1] }
        }
    }
    foreach ($item in @(@(0x01,'LMouse'), @(0x02,'RMouse'))) {
        if ([Clip.Native]::GetAsyncKeyState($item[0]) -lt 0) { $names += $item[1] }
    }
    if ($names.Count) { $names -join '+' } else { '-' }
}
function UiState {
    $foreground = [Clip.Native]::GetForegroundWindow()
    $foregroundProcessId = 0; $thread = [Clip.Native]::GetWindowThreadProcessId($foreground, [ref]$foregroundProcessId)
    $info = New-Object Clip.Native+GuiThreadInfo
    $info.Size = [Runtime.InteropServices.Marshal]::SizeOf([type]'Clip.Native+GuiThreadInfo')
    $guiAvailable = [Clip.Native]::GetGUIThreadInfo($thread, [ref]$info)
    $owner = [Clip.Native]::GetClipboardOwner()
    [pscustomobject]@{
        Foreground = $foreground; ForegroundPid = $foregroundProcessId; Active = $info.Active; Focus = $info.Focus
        MenuFlags = ($info.Flags -band 0x1c); Capture = ($info.Capture -ne [IntPtr]::Zero)
        Held = Held; Owner = $owner; OwnerPid = (WindowPid $owner)
        GuiAvailable = $guiAvailable
    }
}
function UiKey($state) {
    '{0:x}:{1}:{2:x}:{3}:{4:x}:{5}:{6:x}:{7}:{8}:{9:x}:{10}:{11}' -f $state.Foreground.ToInt64(),
        $state.ForegroundPid, $state.Active.ToInt64(), (Class $state.Active), $state.Focus.ToInt64(),
        (Class $state.Focus), $state.MenuFlags, $state.Capture, $state.Held, $state.Owner.ToInt64(), $state.OwnerPid, $state.GuiAvailable
}
function Out($line) { $line; Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 }
$marker = [Clip.Native]::RegisterClipboardFormat('IntraDrop.ReceivedClipboard')
$last = [Clip.Native]::GetClipboardSequenceNumber()
$lastUi = $null
$until = if ($Seconds -gt 0) { (Get-Date).AddSeconds($Seconds) } else { [DateTime]::MaxValue }
Out ("{0:yyyy-MM-dd HH:mm:ss} IntraDrop clipboard watch start: seq={1} computer={2} log={3}" -f (Get-Date), $last, $env:COMPUTERNAME, $LogPath)
"Excel 에서 Ctrl+C / 우클릭을 해 보세요. 로그: $LogPath (중단: Ctrl+C)"
while ((Get-Date) -lt $until) {
    $seq = [Clip.Native]::GetClipboardSequenceNumber()
    $ui = UiState
    $uiKey = UiKey $ui
    $changed = $seq -ne $last
    $uiChanged = $uiKey -ne $lastUi
    if ($changed -or $uiChanged) {
        $delta = if ($changed) { ([uint64]$seq + 0x100000000L - [uint64]$last) % 0x100000000L } else { 0 }
        $last = $seq
        $formats = @()
        if ([Clip.Native]::IsClipboardFormatAvailable(13)) { $formats += 'text' }
        if ([Clip.Native]::IsClipboardFormatAvailable(8)) { $formats += 'image' }
        if ([Clip.Native]::IsClipboardFormatAvailable(15)) { $formats += 'files' }
        if ([Clip.Native]::IsClipboardFormatAvailable($marker)) { $formats += 'IntraDrop-received' }
        Out ("{0:HH:mm:ss.fff} event={1} seq={2} delta={3} owner=0x{4:x}/{5} formats={6} count={7} fg=0x{8:x}/{9} active=0x{10:x}/{11} focus=0x{12:x}/{13} menu=0x{14:x} capture={15} held={16} guiAvailable={17}" -f
            (Get-Date), $(if ($changed -and $uiChanged) { 'clipboard+ui' } elseif ($changed) { 'clipboard' } else { 'ui' }), $seq, $delta, $ui.Owner.ToInt64(),
            (Proc $ui.Owner), ($formats -join '+'), [Clip.Native]::CountClipboardFormats(), $ui.Foreground.ToInt64(),
            (Proc $ui.Foreground), $ui.Active.ToInt64(), (Class $ui.Active), $ui.Focus.ToInt64(),
            (Class $ui.Focus), $ui.MenuFlags, $ui.Capture, $ui.Held, $ui.GuiAvailable)
        $lastUi = $uiKey
    }
    Start-Sleep -Milliseconds 20
}
