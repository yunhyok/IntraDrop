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
        if (_disposed || _applying || sequence == _sequence) return;
        ClipboardContent? content = null;
        try
        {
            var data = Clipboard.GetDataObject();
            // Received data keeps this marker even if OLE renders it later.
            if (data != null && !data.GetDataPresent(ClipboardService.ReceivedFormat, false))
                content = ClipboardService.Capture(data);
        }
        catch (ExternalException ex)
        {
            if (++_attempts < 10) { _timer.Start(); return; }
            _failed(ex);
        }
        catch (InvalidOperationException) { } // Empty or unsupported clipboard formats.
        catch (Exception ex) { _failed(ex); }
        uint afterCapture = GetClipboardSequenceNumber();
        if (afterCapture != sequence) { _timer.Start(); return; }
        _sequence = afterCapture;
        if (content != null) _copied(content);
    }

    public void ApplyReceived(ClipboardContent content)
    {
        _timer.Stop();
        _applying = true;
        try { ClipboardService.Apply(content); }
        finally
        {
            _sequence = GetClipboardSequenceNumber();
            _applying = false;
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
