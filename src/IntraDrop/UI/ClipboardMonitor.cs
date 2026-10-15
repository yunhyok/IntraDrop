using System.ComponentModel;
using System.Runtime.InteropServices;
using IntraDrop.Core;

namespace IntraDrop.UI;

internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly Action<ClipboardContent> _copied;
    private readonly Action<Exception> _failed;
    private uint _sequence;
    private int _attempts;
    private bool _applying;
    private bool _capturing;
    private bool _disposed;

    public ClipboardMonitor(Action<ClipboardContent> copied, Action<Exception> failed)
    {
        _copied = copied;
        _failed = failed;
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
        ClipboardContent? content = null;
        try
        {
            _capturing = true;
            content = ClipboardReader.CaptureAutomatic();
        }
        catch (ExternalException ex)
        {
            if (++_attempts < 10) { _timer.Start(); return; }
            _failed(ex);
        }
        catch (InvalidOperationException) { } // Empty or unsupported clipboard formats.
        catch (Exception ex) { _failed(ex); }
        finally { _capturing = false; }
        uint afterCapture = GetClipboardSequenceNumber();
        if (afterCapture != sequence) { _timer.Start(); return; }
        _sequence = afterCapture;
        if (content != null) _copied(content);
    }

    public void ApplyReceived(ClipboardContent content)
    {
        uint sequence = GetClipboardSequenceNumber();
        if (_capturing || _applying || sequence != _sequence || ClipboardReader.HasLocalExcelCopy())
            throw new InvalidOperationException("현재 컴퓨터의 복사·잘라내기를 보호하기 위해 수신 클립보드를 적용하지 않았습니다. Excel 복사·잘라내기를 끝낸 뒤 다시 복사해 보내세요.");
        _timer.Stop();
        _applying = true;
        bool applied = false;
        try
        {
            // OleSetClipboard may succeed before OleFlushClipboard needs a retry.
            ClipboardService.Apply(content, () =>
                (GetClipboardSequenceNumber() == sequence || ClipboardReader.HasReceivedClipboardFromCurrentProcess()) &&
                !ClipboardReader.HasLocalExcelCopy());
            applied = true;
        }
        finally
        {
            _applying = false;
            if (applied) _sequence = GetClipboardSequenceNumber();
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
