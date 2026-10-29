using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using IntraDrop.Core;

namespace IntraDrop.UI;

internal sealed class ExcelClipboardState
{
    public uint Sequence { get; }
    public IntPtr Owner { get; }
    public uint ProcessId { get; }
    public int Mode { get; }

    public ExcelClipboardState(uint sequence, IntPtr owner, uint processId, int mode)
    {
        Sequence = sequence;
        Owner = owner;
        ProcessId = processId;
        Mode = mode;
    }

    public bool IsIdleFor(uint sequence, IntPtr owner, uint processId) =>
        Mode == 0 && Owner != IntPtr.Zero && ProcessId != 0 &&
        Sequence == sequence && Owner == owner && ProcessId == processId;

    internal bool IsCurrent() => Sequence == ClipboardReader.SequenceNumber &&
        Owner == ClipboardReader.OwnerWindow && ProcessId == ClipboardReader.OwnerProcessId();
}

internal static class ExcelClipboardProbe
{
    internal const string Argument = "--probe-excel-copy";
    internal const int Unknown = 3;

    public static async Task<ExcelClipboardState?> CheckAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested || !ClipboardReader.HasLocalExcelCopy()) return null;
        if (ClipboardReader.HasActiveOfficeInteraction(includeHeldInput: true))
        {
            ClipboardLog.Write("Excel copy probe skipped: active Office interaction");
            return null;
        }
        var state = new ExcelClipboardState(ClipboardReader.SequenceNumber, ClipboardReader.OwnerWindow,
            ClipboardReader.OwnerProcessId(), Unknown);
        if (!state.IsCurrent()) return null;
        try
        {
            ulong owner = IntPtr.Size == 4 ? unchecked((uint)state.Owner.ToInt32()) : unchecked((ulong)state.Owner.ToInt64());
            using var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                $"{Argument} {state.Sequence.ToString(CultureInfo.InvariantCulture)} {owner.ToString(CultureInfo.InvariantCulture)} {state.ProcessId.ToString(CultureInfo.InvariantCulture)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return null;
            int? mode = await WaitForExitAsync(process, 2000, token).ConfigureAwait(false);
            bool current = state.IsCurrent();
            string result = !mode.HasValue ? token.IsCancellationRequested ? "cancelled" : "timeout"
                : !current ? "clipboard changed" : mode.Value == 0 ? "idle" : mode.Value == 1 ? "copy"
                : mode.Value == 2 ? "cut" : "unknown (exit=" + mode.Value + ")";
            ClipboardLog.Write($"Excel copy probe {result} seq={state.Sequence} pid={state.ProcessId}");
            if (!mode.HasValue || mode.Value < 0 || mode.Value > 2 || !current) return null;
            return new ExcelClipboardState(state.Sequence, state.Owner, state.ProcessId, mode.Value);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ClipboardLog.Write($"Excel copy probe unavailable: {ex.GetType().Name} seq={state.Sequence} pid={state.ProcessId}");
            return null;
        }
    }

    internal static async Task<int?> WaitForExitAsync(Process process, int timeoutMilliseconds, CancellationToken token)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onExit = (_, _) => exited.TrySetResult(true);
        process.Exited += onExit;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) exited.TrySetResult(true);
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task deadline = Task.Delay(timeoutMilliseconds, delay.Token);
            await Task.WhenAny(exited.Task, deadline).ConfigureAwait(false);
            delay.Cancel();
            if (token.IsCancellationRequested || !exited.Task.IsCompleted)
                return null;
            return process.ExitCode;
        }
        finally
        {
            process.Exited -= onExit;
            // Only this disposable helper can be killed; never terminate the owning Excel process.
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }

    internal static bool TryParseArguments(string[] args, out ExcelClipboardState? state)
    {
        state = null;
        if (args.Length != 4 || args[0] != Argument ||
            !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint sequence) ||
            !ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong owner) || owner == 0 ||
            (IntPtr.Size == 4 && owner > uint.MaxValue) ||
            !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint processId) || processId == 0)
            return false;
        state = new ExcelClipboardState(sequence, IntPtr.Size == 4 ? new IntPtr(unchecked((int)owner)) : new IntPtr(unchecked((long)owner)), processId, Unknown);
        return true;
    }

    internal static int Run(string[] args)
    {
        if (!TryParseArguments(args, out var state)) return Unknown;
        object? window = null, application = null;
        try
        {
            if (!CanQuery(state!)) return Unknown;
            IntPtr sheet = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out uint processId);
                if (processId != state!.ProcessId) return true;
                EnumChildWindows(top, (child, _) =>
                {
                    var name = new StringBuilder(32);
                    GetClassName(child, name, name.Capacity);
                    GetWindowThreadProcessId(child, out uint childProcessId);
                    if (childProcessId != state.ProcessId || name.ToString() != "EXCEL7") return true;
                    sheet = child;
                    return false;
                }, IntPtr.Zero);
                return sheet == IntPtr.Zero;
            }, IntPtr.Zero);
            if (sheet == IntPtr.Zero || !CanQuery(state!)) return Unknown;
            var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            if (AccessibleObjectFromWindow(sheet, 0xfffffff0, ref dispatch, out window) != 0 || window == null)
                return Unknown;
            application = ((dynamic)window).Application;
            GetWindowThreadProcessId(new IntPtr((int)((dynamic)application).Hwnd), out uint applicationProcessId);
            if (applicationProcessId != state!.ProcessId || !CanQuery(state)) return Unknown;
            object value = ((dynamic)application).CutCopyMode;
            int mode = value is bool flag ? flag ? Unknown : 0 : value is int number && number >= 0 && number <= 2 ? number : Unknown;
            return CanQuery(state) ? mode : Unknown;
        }
        catch { return Unknown; }
        finally
        {
            if (application != null && !ReferenceEquals(application, window)) Release(application);
            if (window != null) Release(window);
        }
    }

    private static bool CanQuery(ExcelClipboardState state) => state.IsCurrent() &&
        ClipboardReader.HasLocalExcelCopy() && !ClipboardReader.HasActiveOfficeInteraction(includeHeldInput: true);

    private static void Release(object value)
    {
        try { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IDispatch)] out object? result);
}
