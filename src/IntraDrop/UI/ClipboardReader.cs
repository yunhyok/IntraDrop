using System.Runtime.InteropServices;
using System.Diagnostics;
using IntraDrop.Core;

namespace IntraDrop.UI;

// Avoid OLE owner callbacks while observing text copies in applications such as Excel.
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
        GetWindowThreadProcessId(owner, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; } // The clipboard owner just exited.
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
