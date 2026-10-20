using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using IntraDrop.Core;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

[Collection("Native clipboard")]
public sealed class ExcelClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "InteractiveExcel")]
    public async Task LocalExcelCopyAndCutSurviveIncomingClipboard(bool cut)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Verify(cut); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Verify(bool cut)
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application", true)!;
        var original = Clipboard.GetDataObject();
        dynamic excel = Activator.CreateInstance(excelType)!;
        dynamic? book = null, sheet = null, source = null, destination = null;
        try
        {
            excel.DisplayAlerts = false;
            dynamic books = excel.Workbooks;
            try { book = books.Add(); } finally { Marshal.FinalReleaseComObject(books); }
            dynamic sheets = book.Worksheets;
            try { sheet = sheets[1]; } finally { Marshal.FinalReleaseComObject(sheets); }
            source = sheet.Range["A1"];
            destination = sheet.Range["B1"];
            source.Value2 = "fresh Excel cells";
            Clipboard.Clear();
            var copies = new List<ClipboardContent>();
            using var monitor = new ClipboardMonitor(copies.Add, error => throw error);
            if (cut) source.Cut(); else source.Copy();
            Assert.Equal(cut ? 2 : 1, (int)excel.CutCopyMode);
            Assert.True(WaitUntil(ClipboardReader.HasLocalExcelCopy, 3000),
                "Excel copy mode started but clipboard ownership was not published within 3 seconds.");
            var remote = new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("old remote value") };
            Assert.Throws<InvalidOperationException>(() => monitor.ApplyReceived(remote));
            Pump();
            Assert.True(ClipboardReader.HasLocalExcelCopy());
            Assert.Single(copies);
            Assert.Equal("fresh Excel cells", Encoding.UTF8.GetString(copies[0].Data).TrimEnd('\r', '\n'));
            Assert.Throws<InvalidOperationException>(() => monitor.ApplyReceived(remote));
            Assert.Equal(cut ? 2 : 1, (int)excel.CutCopyMode);
            sheet.Paste(destination);
            // Excel can invalidate existing Range objects when moving cells.
            Marshal.FinalReleaseComObject(source);
            Marshal.FinalReleaseComObject(destination);
            source = sheet.Range["A1"];
            destination = sheet.Range["B1"];
            Assert.Equal("fresh Excel cells", (string)destination.Value2);
            if (cut) Assert.Null((object?)source.Value2);
            else Assert.Equal("fresh Excel cells", (string)source.Value2);
            excel.CutCopyMode = false;
            Pump();
            Assert.False(ClipboardReader.HasLocalExcelCopy());
            monitor.ApplyReceived(remote);
            Assert.Equal("old remote value", Clipboard.GetText());
        }
        finally
        {
            excel.CutCopyMode = false;
            if (book != null) book.Close(false);
            excel.Quit();
            foreach (object? value in new object?[] { destination, source, sheet, book, excel })
                if (value != null) Marshal.FinalReleaseComObject(value);
            // Restoring is a courtesy: the previous owner may have exited and can no longer render its formats.
            try { if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear(); }
            catch (ExternalException) { Clipboard.Clear(); }
        }
    }

    [Fact]
    [Trait("Category", "InteractiveExcel")]
    public async Task OutgoingCaptureWaitsUntilExcelContextMenuCloses()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { VerifyCaptureWaitsForContextMenu(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    // Copy, then right-click a cell before the monitor's debounce timer runs, like a user reaching for Paste.
    private static void VerifyCaptureWaitsForContextMenu()
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application", true)!;
        var original = Clipboard.GetDataObject();
        dynamic excel = Activator.CreateInstance(excelType)!;
        dynamic? book = null, sheet = null, source = null;
        try
        {
            excel.DisplayAlerts = false;
            excel.Visible = true;
            dynamic books = excel.Workbooks;
            try { book = books.Add(); } finally { Marshal.FinalReleaseComObject(books); }
            dynamic sheets = book.Worksheets;
            try { sheet = sheets[1]; } finally { Marshal.FinalReleaseComObject(sheets); }
            source = sheet.Range["A1"];
            source.Value2 = "menu stays open";
            var window = new IntPtr((int)excel.Hwnd);
            GetWindowRect(window, out var rect);
            int centerX = (rect.Left + rect.Right) / 2, centerY = (rect.Top + rect.Bottom) / 2;
            SetForegroundWindow(window);
            if (!WaitUntil(() => GetForegroundWindow() == window, 2000)) Click(centerX, centerY, 0x0002, 0x0004);
            Assert.True(WaitUntil(() => GetForegroundWindow() == window, 5000), "Excel did not become the foreground window.");
            Assert.False(ClipboardReader.HasActiveOfficeInteraction(includeHeldInput: true), "Idle Excel must not block capture.");

            Clipboard.Clear();
            var copies = new List<ClipboardContent>();
            using var monitor = new ClipboardMonitor(copies.Add, error => throw error);
            source.Copy();
            Click(centerX, centerY, 0x0008, 0x0010);
            Assert.True(WaitUntil(() => ClipboardReader.HasActiveOfficeInteraction(), 3000), "The cell context menu was not detected.");
            Pump();
            Assert.Empty(copies);
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            Assert.True(WaitUntil(() => !ClipboardReader.HasActiveOfficeInteraction(includeHeldInput: true), 3000), "The context menu did not close.");
            var deadline = Environment.TickCount64 + 5000;
            while (copies.Count == 0 && Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.Single(copies);
            Assert.Equal("menu stays open", Encoding.UTF8.GetString(copies[0].Data).TrimEnd('\r', '\n'));
            Assert.Equal(1, (int)excel.CutCopyMode); // Reading after the menu closed kept Excel's copy mode (xlCopy).
        }
        finally
        {
            try { excel.CutCopyMode = false; if (book != null) book.Close(false); excel.Quit(); } catch { }
            foreach (object? value in new object?[] { source, sheet, book, excel })
                if (value != null) Marshal.FinalReleaseComObject(value);
            // Restoring is a courtesy: the previous owner may have exited and can no longer render its formats.
            try { if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear(); }
            catch (ExternalException) { Clipboard.Clear(); }
        }
    }

    private static void Click(int x, int y, uint down, uint up)
    {
        SetCursorPos(x, y);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    private static bool WaitUntil(Func<bool> condition, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static void Pump()
    {
        var until = Environment.TickCount64 + 600;
        do { Application.DoEvents(); Thread.Sleep(10); } while (Environment.TickCount64 < until);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
}
