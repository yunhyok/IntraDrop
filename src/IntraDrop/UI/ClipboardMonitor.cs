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
    private ClipboardContent? _known, _lastApplied;
    private bool _applying;
    private bool _capturing;
    private bool _disposed;

    public ClipboardMonitor(Action<ClipboardContent> copied, Action<Exception> failed, Func<bool>? deferCapture = null)
    {
        _copied = copied;
        _failed = failed;
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
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x031D && !_disposed && !_applying && GetClipboardSequenceNumber() != _sequence)
        {
            _attempts = 0;
            _timer.Stop();
            _timer.Start();
        }
        base.WndProc(ref message);
    }

    private void CaptureChange()
    {
        _timer.Stop();
        uint sequence = GetClipboardSequenceNumber();
        if (_disposed || _applying || _capturing || sequence == _sequence) return;
        // Office 가 메뉴·편집·드래그·키 조작 중이면 클립보드를 열지 않는다. 열면 Excel 의 지연 렌더가 팝업을 닫고,
        // 그동안 클립보드를 쥐고 있어 Excel 의 다음 복사가 실패한다. 조작이 끝난 뒤 한 번만 읽는다.
        if (_deferCapture()) { _timer.Start(); return; }
        int resetVersion = _resetVersion;
        ClipboardContent? content = null;
        try
        {
            _capturing = true;
            content = ClipboardReader.CaptureAutomatic();
        }
        catch (ExternalException ex)
        {
            if (resetVersion != _resetVersion) return;
            if (++_attempts < 10) { _timer.Start(); return; }
            _failed(ex);
        }
        catch (InvalidOperationException) { } // Empty or unsupported clipboard formats.
        catch (Exception ex) { if (resetVersion == _resetVersion) _failed(ex); }
        finally
        {
            _capturing = false;
            if (resetVersion != _resetVersion && !_disposed && GetClipboardSequenceNumber() != _sequence)
                _timer.Start(); // A new copy may have arrived while the old capture was unwinding.
        }
        if (resetVersion != _resetVersion) return;
        uint afterCapture = GetClipboardSequenceNumber();
        if (afterCapture != sequence) { _timer.Start(); return; }
        _sequence = afterCapture;
        _known = content;
        if (content != null && !content.ContentEquals(_lastApplied)) _copied(content);
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
            _known = _lastApplied = null;
            _attempts = 0;
            cleared = true;
        }
        finally
        {
            _applying = false;
            if (!cleared) _timer.Start();
        }
    }

    public void ApplyReceived(ClipboardContent content)
    {
        uint sequence = GetClipboardSequenceNumber();
        if (_capturing || _applying || sequence != _sequence || ClipboardReader.ShouldProtectLocalClipboard())
            throw new InvalidOperationException("현재 컴퓨터의 Excel 메뉴·입력·복사 작업을 보호하기 위해 수신 클립보드를 적용하지 않았습니다. 작업을 마친 뒤 다시 복사해 보내세요.");
        if (content.ContentEquals(_known)) return; // Already on the clipboard: rewriting would only bounce it again.
        _timer.Stop();
        _applying = true;
        _known = null;
        bool applied = false;
        try
        {
            // OleSetClipboard may succeed before OleFlushClipboard needs a retry.
            ClipboardService.Apply(content, () =>
                (GetClipboardSequenceNumber() == sequence || ClipboardReader.HasReceivedClipboardFromCurrentProcess()) &&
                !ClipboardReader.ShouldProtectLocalClipboard());
            applied = true;
        }
        finally
        {
            _applying = false;
            if (applied) { _sequence = GetClipboardSequenceNumber(); _known = _lastApplied = content; }
            else _timer.Start(); // A local copy during a failed write still needs capture.
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
