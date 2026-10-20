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

    // Office main-window classes. A different active window means a popup menu, gallery, or dialog is open.
    private static readonly Dictionary<string, string> OfficeMainClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EXCEL"] = "XLMAIN",
        ["WINWORD"] = "OpusApp",
        ["POWERPNT"] = "PPTFrameClass",
    };

    public static bool HasLocalExcelCopy()
    {
        // Metadata only: querying Excel's IDataObject can disturb its menus/copy state.
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero || CountClipboardFormats() == 0) return false;
        return OfficeProcessName(owner) == "EXCEL";
    }

    public static bool ShouldProtectLocalClipboard() => HasLocalExcelCopy() || HasActiveOfficeInteraction();

    /// <summary>전면의 Office 앱이 메뉴·대화상자·셀 편집·마우스 조작 중이면 true.
    /// 이때 클립보드를 열면 Excel 의 지연 렌더가 팝업을 닫고, 그동안 클립보드를 쥐고 있어 Excel 자신의 다음 복사가 실패한다.
    /// includeHeldInput: Ctrl 이나 마우스 버튼이 눌린 상태도 조작 중으로 본다 (캡처 지연용; 수신 보호는 복사·잘라내기·붙여넣기 조합만 본다).</summary>
    public static bool HasActiveOfficeInteraction(bool includeHeldInput = false)
    {
        IntPtr foreground = GetForegroundWindow();
        string? app = OfficeProcessName(foreground);
        if (app == null) return false;
        uint thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
        if (!GetGUIThreadInfo(thread, ref info)) return true;
        bool control = GetAsyncKeyState(0x11) < 0;
        bool inputHeld = includeHeldInput
            ? control || GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0
            : control && (GetAsyncKeyState(0x43) < 0 || GetAsyncKeyState(0x58) < 0 || GetAsyncKeyState(0x56) < 0);
        return IsOfficeInteraction(OfficeMainClasses[app], WindowClass(info.Active), WindowClass(info.Focus), info.Flags,
            info.Capture != IntPtr.Zero, inputHeld);
    }

    internal static bool IsOfficeInteraction(string mainClass, string activeClass, string focusClass, uint guiFlags,
        bool hasMouseCapture, bool inputHeld) =>
        // Office's custom popup reports no GUI menu flags; its active window is not the main frame.
        !string.Equals(activeClass, mainClass, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(focusClass, "EXCEL6", StringComparison.OrdinalIgnoreCase) ||   // Excel in-cell edit
        (guiFlags & 0x1c) != 0 || hasMouseCapture || inputHeld;

    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    /// <summary>창을 소유한 프로세스가 Office 앱이면 대문자 프로세스 이름, 아니면 null.</summary>
    private static string? OfficeProcessName(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string name = process.ProcessName.ToUpperInvariant();
            return OfficeMainClasses.ContainsKey(name) ? name : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; } // The owner just exited.
    }

    public static bool HasReceivedClipboardFromCurrentProcess()
    {
        GetWindowThreadProcessId(GetClipboardOwner(), out uint processId);
        return processId == GetCurrentProcessId() && IsClipboardFormatAvailable(ReceivedClipboardFormat);
    }

    /// <summary>클립보드 소유 프로세스가 전면 앱도 IntraDrop 도 아니면 true. 사용자의 복사는 전면 앱이 소유하고,
    /// VNC·원격 데스크톱·동기화 프로그램의 되돌이는 백그라운드 프로세스가 소유한다.</summary>
    public static bool OwnerIsBackgroundProcess()
    {
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out uint ownerProcessId);
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcessId);
        return ownerProcessId != foregroundProcessId && ownerProcessId != GetCurrentProcessId();
    }

    public static string OwnerProcess() => ProcessName(GetClipboardOwner());

    /// <summary>진단 로그용 상태. 클립보드를 열거나 내용을 읽지 않고, Ctrl 없는 일반 문자 키는 기록하지 않는다.</summary>
    public static string Describe()
    {
        IntPtr foreground = GetForegroundWindow();
        string text = $"owner={ProcessName(GetClipboardOwner())} formats={CountClipboardFormats()} fg={ProcessName(foreground)}";
        if (OfficeProcessName(foreground) == null) return text;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
        text += GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info)
            ? $" active={WindowClass(info.Active)} focus={WindowClass(info.Focus)} menu=0x{info.Flags & 0x1c:x} capture={info.Capture != IntPtr.Zero}"
            : " gui=unavailable";
        var held = new List<string>();
        if (GetAsyncKeyState(0x11) < 0)
        {
            held.Add("Ctrl");
            if (GetAsyncKeyState(0x43) < 0) held.Add("C");
            if (GetAsyncKeyState(0x58) < 0) held.Add("X");
            if (GetAsyncKeyState(0x56) < 0) held.Add("V");
        }
        if (GetAsyncKeyState(0x01) < 0) held.Add("LMouse");
        if (GetAsyncKeyState(0x02) < 0) held.Add("RMouse");
        return text + " held=" + (held.Count == 0 ? "-" : string.Join("+", held));
    }

    private static string ProcessName(IntPtr window)
    {
        if (window == IntPtr.Zero) return "none";
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"{process.ProcessName}({processId})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "pid" + processId; }
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
