using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IntraDrop.UI;

internal sealed class ScreenCaptureService : NativeWindow, IDisposable
{
    private const int HotKeyId = 1;
    private readonly Action<string, string, ToolTipIcon> _notify;
    private ScreenCaptureForm? _selection;
    private bool _disposed;

    internal static bool Supports(OperatingSystem os) =>
        os.Platform == PlatformID.Win32NT && os.Version.Major == 6 && os.Version.Minor == 1;

    public ScreenCaptureService(Action<string, string, ToolTipIcon> notify)
    {
        if (!Supports(Environment.OSVersion)) throw new PlatformNotSupportedException();
        _notify = notify;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // Message-only window.
        // MOD_WIN | MOD_SHIFT | MOD_NOREPEAT: holding S must not reopen the overlay.
        if (!RegisterHotKey(Handle, HotKeyId, 0x0008 | 0x0004 | 0x4000, (uint)Keys.S))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "Win+Shift+S를 등록하지 못했습니다. 다른 프로그램의 단축키 설정을 확인한 뒤 IntraDrop을 다시 실행하세요.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotKeyId)
        {
            CaptureRegion();
            return;
        }
        base.WndProc(ref m);
    }

    private void CaptureRegion()
    {
        if (_disposed || _selection != null) return;
        try
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            using var desktop = CaptureDesktop(bounds);

            using var selection = new ScreenCaptureForm(desktop, bounds);
            _selection = selection;
            if (selection.ShowDialog() != DialogResult.OK || _disposed) return;
            using var image = desktop.Clone(selection.Selection, PixelFormat.Format24bppRgb);
            var data = new DataObject();
            data.SetImage(image);
            Clipboard.SetDataObject(data, true, 10, 100);
            _notify("화면 캡처 완료", "클립보드에 복사했습니다. Ctrl+V로 붙여넣으세요.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (!_disposed) _notify("화면 캡처 실패", ex.Message, ToolTipIcon.Warning);
        }
        finally { _selection = null; }
    }

    internal static Bitmap CaptureDesktop(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var screen = Graphics.FromHwnd(IntPtr.Zero);
            using var target = Graphics.FromImage(bitmap);
            IntPtr sourceDc = screen.GetHdc();
            try
            {
                IntPtr targetDc = target.GetHdc();
                try
                {
                    // CopyFromScreen rejects SRCCOPY | CAPTUREBLT; BitBlt includes layered windows.
                    if (!BitBlt(targetDc, 0, 0, bounds.Width, bounds.Height, sourceDc, bounds.X, bounds.Y, 0x40CC0020))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "화면을 읽지 못했습니다. 다시 시도하세요.");
                }
                finally { target.ReleaseHdc(targetDc); }
            }
            finally { screen.ReleaseHdc(sourceDc); }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection?.Close();
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, HotKeyId);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}
