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
            Assert.True(ClipboardReader.HasLocalExcelCopy(), "Excel must own the copied cells before testing incoming replacement.");
            Assert.Equal(cut ? 2 : 1, (int)excel.CutCopyMode);
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
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    }

    private static void Pump()
    {
        var until = Environment.TickCount64 + 600;
        do { Application.DoEvents(); Thread.Sleep(10); } while (Environment.TickCount64 < until);
    }
}
