using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using IntraDrop.Core;

namespace IntraDrop.UI;

// Read standard text formats without querying the clipboard's IDataObject.
internal static class ClipboardReader
{
    private const uint CfBitmap = 2;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private static readonly uint ReceivedClipboardFormat = Register(ClipboardService.ReceivedFormat);
    private static readonly uint[] DeniedFormats =
    {
        ReceivedClipboardFormat,
        CfHDrop,
        Register("Shell IDList Array"),
        Register("MountedVolume"),
        Register("FileName"),
        Register("FileNameW"),
        Register("FileGroupDescriptor"),
        Register("FileGroupDescriptorW"),
        Register("FileContents")
    };

    public static bool HasLocalExcelCopy()
    {
        // Metadata only: querying Excel's IDataObject can disturb its menus/copy state.
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero || CountClipboardFormats() == 0) return false;
        return IsExcelWindow(owner);
    }

    public static bool ShouldProtectLocalClipboard() => HasLocalExcelCopy() || HasActiveExcelInteraction();

    private static bool HasActiveExcelInteraction()
    {
        IntPtr foreground = GetForegroundWindow();
        if (!IsExcelWindow(foreground)) return false;
        uint thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
        if (!GetGUIThreadInfo(thread, ref info)) return true;
        bool clipboardShortcut = GetAsyncKeyState(0x11) < 0 &&
            (GetAsyncKeyState(0x43) < 0 || GetAsyncKeyState(0x58) < 0 || GetAsyncKeyState(0x56) < 0);
        return IsExcelInteraction(WindowClass(info.Active), WindowClass(info.Focus), info.Flags,
            info.Capture != IntPtr.Zero, clipboardShortcut);
    }

    internal static bool IsExcelInteraction(string activeClass, string focusClass, uint guiFlags,
        bool hasMouseCapture, bool clipboardShortcut) =>
        // Office's custom popup reports no GUI menu flags; its active window is not XLMAIN.
        !string.Equals(activeClass, "XLMAIN", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(focusClass, "EXCEL6", StringComparison.OrdinalIgnoreCase) ||
        (guiFlags & 0x1c) != 0 || hasMouseCapture || clipboardShortcut;

    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static bool IsExcelWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; } // The owner just exited.
    }

    public static bool HasReceivedClipboardFromCurrentProcess()
    {
        GetWindowThreadProcessId(GetClipboardOwner(), out uint processId);
        return processId == GetCurrentProcessId() && IsClipboardFormatAvailable(ReceivedClipboardFormat);
    }

    public static ClipboardContent? CaptureAutomatic()
    {
        bool bitmap = false;
        string? text = null;
        if (!OpenClipboard(IntPtr.Zero))
            throw new ExternalException("클립보드를 사용 중이어서 읽을 수 없습니다.", Marshal.GetHRForLastWin32Error());
        try
        {
            if (DeniedFormats.Any(IsClipboardFormatAvailable)) return null;
            if (IsClipboardFormatAvailable(CfUnicodeText)) text = ReadUnicodeText();
            else bitmap = IsClipboardFormatAvailable(CfBitmap);
        }
        finally
        {
            CloseClipboard();
        }

        if (text != null) return ClipboardService.Capture(new DataObject(DataFormats.UnicodeText, text));
        if (bitmap) return ClipboardService.CaptureAutomatic(Clipboard.GetDataObject());
        throw new InvalidOperationException("전달할 텍스트, 이미지 또는 파일·폴더가 없습니다. 앱 전용 객체나 가상 첨부 파일은 파일로 저장한 뒤 복사하세요.");
    }

    private static string ReadUnicodeText()
    {
        IntPtr handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero) throw ClipboardError("클립보드 텍스트를 읽을 수 없습니다.");
        ulong byteCount = GlobalSize(handle).ToUInt64();
        if (byteCount < sizeof(char))
            throw new InvalidOperationException("클립보드 텍스트가 올바른 UTF-16 문자열이 아닙니다.");

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw ClipboardError("클립보드 텍스트를 읽을 수 없습니다.");
        try
        {
            int capacity = (int)Math.Min(byteCount / sizeof(char), (ulong)ClipboardContent.MaxTextBytes + 1);
            var chars = new char[capacity];
            Marshal.Copy(pointer, chars, 0, capacity);
            int length = Array.IndexOf(chars, '\0');
            if (length < 0)
            {
                if ((ulong)capacity * sizeof(char) < byteCount)
                    throw new InvalidOperationException("클립보드 텍스트는 1MB까지 전달할 수 있습니다.");
                throw new InvalidOperationException("클립보드 텍스트가 올바른 UTF-16 문자열이 아닙니다.");
            }

            return new string(chars, 0, length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static uint Register(string name)
    {
        uint format = RegisterClipboardFormat(name);
        if (format == 0) throw ClipboardError("클립보드 형식을 등록할 수 없습니다.");
        return format;
    }

    private static ExternalException ClipboardError(string message) =>
        new(message, Marshal.GetHRForLastWin32Error());

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}
