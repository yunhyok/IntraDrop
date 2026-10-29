using System.ComponentModel;
using System.Runtime.InteropServices;
using IntraDrop.Core;

namespace IntraDrop.UI;

internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly Action<ClipboardContent> _copied;
    private readonly Action<Exception> _failed;
    private readonly Func<bool> _deferCapture;
    private uint _sequence;
    private int _attempts;
    private int _resetVersion;
    // What the clipboard held at _sequence when this monitor last read or wrote it, and what it last wrote.
    // A clipboard that another sync path (RDP, cloud clipboard, a self-registered peer) hands straight back must
    // not be written or broadcast again: each rewrite takes ownership from Excel and closes open Office menus.
    private ClipboardContent? _known;
    private readonly ClipboardEchoTracker _echo = new();
    private readonly Action<string>? _echoed;
    private bool _applying;
    private bool _capturing;
    private bool _deferLogged;
    private bool _disposed;

    public ClipboardMonitor(Action<ClipboardContent> copied, Action<Exception> failed, Func<bool>? deferCapture = null,
        Action<string>? echoed = null)
    {
        _copied = copied;
        _failed = failed;
        _echoed = echoed;
        _deferCapture = deferCapture ?? (() => ClipboardReader.HasActiveOfficeInteraction(includeHeldInput: true));
        // Ignore the clipboard already present when the app starts.
        _sequence = GetClipboardSequenceNumber();
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        if (!AddClipboardFormatListener(Handle))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            _timer.Dispose();
            throw new Win32Exception(error);
        }
        _timer.Tick += (_, _) => CaptureChange();
        ClipboardLog.Write($"monitor start seq={_sequence} {ClipboardReader.Describe()}");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x031D && !_disposed)
        {
            uint sequence = GetClipboardSequenceNumber();
            ObserveCopy(sequence);
            ClipboardLog.Write($"update seq={sequence} own={sequence == _sequence} applying={_applying} {ClipboardReader.Describe()}");
            if (!_applying && sequence != _sequence)
            {
                _attempts = 0;
                _timer.Stop();
                _timer.Start();
            }
        }
        base.WndProc(ref message);
    }

    private void CaptureChange()
    {
        _timer.Stop();
        uint sequence = GetClipboardSequenceNumber();
        if (_disposed || _applying || _capturing || sequence == _sequence) return;
        ObserveCopy(sequence);
        // Office 가 메뉴·편집·드래그·키 조작 중이면 클립보드를 열지 않는다. 열면 Excel 의 지연 렌더가 팝업을 닫고,
        // 그동안 클립보드를 쥐고 있어 Excel 의 다음 복사가 실패한다. 조작이 끝난 뒤 한 번만 읽는다.
        if (_deferCapture())
        {
            if (!_deferLogged) ClipboardLog.Write($"capture deferred seq={sequence} {ClipboardReader.Describe()}");
            _deferLogged = true;
            _timer.Start();
            return;
        }
        _deferLogged = false;
        int resetVersion = _resetVersion;
        ClipboardContent? content = null;
        string? skipped = null;
        try
        {
            _capturing = true;
            content = ClipboardReader.CaptureAutomatic();
        }
        catch (ExternalException ex)
        {
            if (resetVersion != _resetVersion) return;
            if (++_attempts < 10) { ClipboardLog.Write($"capture retry {_attempts}: {ex.Message}"); _timer.Start(); return; }
            ClipboardLog.Write($"capture failed: {ex.Message} {ClipboardReader.Describe()}");
            _failed(ex);
        }
        catch (InvalidOperationException ex) { skipped = ex.Message; } // Empty or unsupported clipboard formats.
        catch (Exception ex)
        {
            if (resetVersion != _resetVersion) return;
            ClipboardLog.Write($"capture failed: {ex.Message}");
            _failed(ex);
        }
        finally
        {
            _capturing = false;
            if (resetVersion != _resetVersion && !_disposed && GetClipboardSequenceNumber() != _sequence)
                _timer.Start(); // A new copy may have arrived while the old capture was unwinding.
        }
        if (resetVersion != _resetVersion) return;
        uint afterCapture = GetClipboardSequenceNumber();
        uint owner = ClipboardReader.OwnerProcessId();
        bool background = ClipboardReader.OwnerIsBackgroundProcess();
        if (afterCapture != sequence || GetClipboardSequenceNumber() != sequence)
        {
            ClipboardLog.Write($"capture: clipboard changed during read seq={sequence}->{afterCapture}, retry");
            _timer.Start();
            return;
        }
        _sequence = afterCapture;
        _known = content;
        var echo = content == null ? ClipboardEcho.None : _echo.Classify(content, sequence, owner, background);
        if (content == null)
            ClipboardLog.Write($"capture skipped seq={afterCapture}: {skipped ?? "files, received clipboard, or app-only formats"}");
        else if (echo == ClipboardEcho.Applied)
            ClipboardLog.Write($"capture is an echo of the last applied clipboard, not sent {ClipboardLog.Summary(content)} seq={afterCapture} {ClipboardReader.Describe()}");
        else if (echo == ClipboardEcho.Sent)
        {
            string process = ClipboardReader.OwnerProcess();
            ClipboardLog.Write($"capture is an echo of the last sent clipboard written back by {process}, not sent {ClipboardLog.Summary(content)} seq={afterCapture} {ClipboardReader.Describe()}");
            _echoed?.Invoke(process);
        }
        else
        {
            ClipboardLog.Write($"captured {ClipboardLog.Summary(content)} seq={afterCapture} {ClipboardReader.Describe()}");
            _echo.Sent(content);
            _copied(content);
        }
    }

    private void ObserveCopy(uint sequence)
    {
        uint owner = ClipboardReader.OwnerProcessId();
        bool foreground = !ClipboardReader.OwnerIsBackgroundProcess();
        if (sequence == GetClipboardSequenceNumber()) _echo.Observe(sequence, owner, foreground);
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClipboardMonitor));
        if (_applying) throw new InvalidOperationException("클립보드를 적용 중입니다. 잠시 후 다시 초기화하세요.");
        // A clipboard API may pump messages and leave an older capture frame on the stack.
        _resetVersion++;
        _timer.Stop();
        _applying = true;
        bool cleared = false;
        try
        {
            Clipboard.Clear();
            _sequence = GetClipboardSequenceNumber();
            _known = null;
            _echo.Clear();
            _attempts = 0;
            _deferLogged = false;
            cleared = true;
            ClipboardLog.Write($"cleared seq={_sequence}");
        }
        finally
        {
            _applying = false;
            if (!cleared) _timer.Start();
        }
    }

    public void ApplyReceived(ClipboardContent content, ExcelClipboardState? excelState = null)
    {
        uint sequence = GetClipboardSequenceNumber();
        string? refusal = _capturing ? "reading a local copy" : _applying ? "another apply in progress"
            : sequence != _sequence ? "local copy not captured yet"
            : ClipboardReader.ShouldProtectLocalClipboard(excelState) ? "Office protection" : null;
        if (refusal != null)
        {
            ClipboardLog.Write($"receive refused ({refusal}) {ClipboardLog.Summary(content)} seq={sequence} {ClipboardReader.Describe()}");
            throw new InvalidOperationException("현재 컴퓨터의 Excel 메뉴·입력·복사 작업을 보호하기 위해 수신 클립보드를 적용하지 않았습니다. 작업을 마친 뒤 다시 복사해 보내세요.");
        }
        if (content.ContentEquals(_known)) // Already on the clipboard: rewriting would only bounce it again.
        {
            ClipboardLog.Write($"receive skipped, already on the clipboard {ClipboardLog.Summary(content)} seq={sequence}");
            return;
        }
        _timer.Stop();
        _applying = true;
        _known = null;
        _deferLogged = false;
        bool applied = false;
        try
        {
            // OleSetClipboard may succeed before OleFlushClipboard needs a retry.
            ClipboardService.Apply(content, () =>
                (GetClipboardSequenceNumber() == sequence || ClipboardReader.HasReceivedClipboardFromCurrentProcess()) &&
                !ClipboardReader.ShouldProtectLocalClipboard(excelState));
            applied = true;
        }
        finally
        {
            _applying = false;
            if (applied)
            {
                _sequence = GetClipboardSequenceNumber();
                _known = content;
                _echo.Applied(content);
                ClipboardLog.Write($"applied {ClipboardLog.Summary(content)} seq={_sequence}");
            }
            else
            {
                ClipboardLog.Write($"apply failed {ClipboardLog.Summary(content)} {ClipboardReader.Describe()}");
                _timer.Start(); // A local copy during a failed write still needs capture.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
