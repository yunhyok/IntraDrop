# 클립보드가 바뀔 때마다 누가(소유 프로세스) 무엇을(형식) 썼는지 기록한다. 내용은 읽지 않으므로 Excel 의 지연 렌더를 건드리지 않는다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\watch-clipboard.ps1 [-Seconds 60] [-LogPath 경로]
#   증상이 나는 PC 에서 실행한 뒤 Excel 에서 영역을 잡고 Ctrl+C, 우클릭을 해 보고, 로그 파일(기본 %TEMP%\IntraDrop-clipboard-watch.log)을 보낸다. Ctrl+C 로 중단.
param([int]$Seconds = 0, [string]$LogPath = (Join-Path $env:TEMP 'IntraDrop-clipboard-watch.log'))
Add-Type -Namespace Clip -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
[DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
[DllImport("user32.dll")] public static extern int CountClipboardFormats();
'@
function Proc($handle) {
    $id = 0; [void][Clip.Native]::GetWindowThreadProcessId($handle, [ref]$id)
    if ($id -eq 0) { return 'none' }
    $name = (Get-Process -Id $id -ErrorAction SilentlyContinue).ProcessName
    if ($name) { "$name($id)" } else { "pid$id" }
}
function Out($line) { $line; Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 }
$marker = [Clip.Native]::RegisterClipboardFormat('IntraDrop.ReceivedClipboard')
$last = [Clip.Native]::GetClipboardSequenceNumber()
$until = if ($Seconds -gt 0) { (Get-Date).AddSeconds($Seconds) } else { [DateTime]::MaxValue }
Out ("{0:yyyy-MM-dd HH:mm:ss} IntraDrop clipboard watch start: seq={1} computer={2} log={3}" -f (Get-Date), $last, $env:COMPUTERNAME, $LogPath)
"Excel 에서 Ctrl+C / 우클릭을 해 보세요. 로그: $LogPath (중단: Ctrl+C)"
while ((Get-Date) -lt $until) {
    $seq = [Clip.Native]::GetClipboardSequenceNumber()
    if ($seq -ne $last) {
        $last = $seq
        $formats = @()
        if ([Clip.Native]::IsClipboardFormatAvailable(13)) { $formats += 'text' }
        if ([Clip.Native]::IsClipboardFormatAvailable(8)) { $formats += 'image' }
        if ([Clip.Native]::IsClipboardFormatAvailable(15)) { $formats += 'files' }
        if ([Clip.Native]::IsClipboardFormatAvailable($marker)) { $formats += 'IntraDrop-received' }
        Out ("{0:HH:mm:ss.fff} seq={1} owner={2} formats={3} count={4} foreground={5}" -f (Get-Date), $seq,
            (Proc ([Clip.Native]::GetClipboardOwner())), ($formats -join '+'), [Clip.Native]::CountClipboardFormats(),
            (Proc ([Clip.Native]::GetForegroundWindow())))
    }
    Start-Sleep -Milliseconds 20
}
