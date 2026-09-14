using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using IntraDrop.Core;
using IntraDrop.Models;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ClipboardUiTests
{
    [Fact]
    public Task PortableFormatsAndDynamicDestinationMenu() => OnSta(() =>
    {
        const string text = "한글 clipboard\r\n둘째 줄 😀";
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        Assert.Equal(text, Encoding.UTF8.GetString(ClipboardService.Capture(data).Data));
        data.SetData(DataFormats.FileDrop, new[] { @"C:\selected\file.txt" });
        Assert.Equal("files", ClipboardService.Capture(data).Format);
        Assert.Throws<InvalidOperationException>(() => ClipboardService.Capture(new DataObject("PrivateApplicationObject", "opaque")));
        Assert.Throws<InvalidOperationException>(() => ClipboardService.Capture(new DataObject(DataFormats.UnicodeText, new string('가', ClipboardContent.MaxTextBytes / 3 + 1))));

        // Test the real menu without starting listeners or altering user settings/autostart.
        var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
        var settings = new AppSettings();
        typeof(TrayApplicationContext).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, settings);
        using var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
        var clipboard = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text?.StartsWith("클립보드 전달") == true);
        context.PopulateClipboardMenu(clipboard);
        Assert.False(clipboard.DropDownItems[0].Enabled);
        settings.Peers.Add(new PeerInfo { Nickname = "Windows 7 & Lab", Host = "127.0.0.2", DeviceId = Guid.NewGuid().ToString("N") });
        context.PopulateClipboardMenu(clipboard);
        Assert.Single(clipboard.DropDownItems.Cast<ToolStripItem>());
        Assert.Contains("Windows 7 && Lab", clipboard.DropDownItems[0].Text);
        Assert.True(clipboard.DropDownItems[0].Enabled);
        settings.Peers.Clear();
        context.PopulateClipboardMenu(clipboard);
        Assert.False(clipboard.DropDownItems[0].Enabled);
    });

    [Fact]
    public Task ImageBoundsAreCheckedBeforeDecoding() => OnSta(() =>
    {
        using var bitmap = new Bitmap(32, 16);
        var data = new DataObject();
        data.SetImage(bitmap);
        var content = ClipboardService.Capture(data);
        Assert.Equal("png", content.Format);
        ClipboardContent.ValidateData("png", content.Data);
        content.Data[16] = 0x7f;
        Assert.Throws<InvalidDataException>(() => ClipboardContent.ValidateData("png", content.Data));
        Assert.Throws<InvalidDataException>(() => ClipboardContent.ValidateData("png", new byte[33]));
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task NativeClipboardRoundTripPreservesTextImageAndCopyFiles() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        string folder = Directory.CreateTempSubdirectory("IntraDrop-clipboard-ui-").FullName;
        try
        {
            const string text = "IntraDrop 검증\r\nCtrl+V 😀";
            ClipboardService.Apply(new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes(text) });
            Assert.Equal(text, Clipboard.GetText(TextDataFormat.UnicodeText));
            using var bitmap = new Bitmap(40, 20);
            bitmap.SetPixel(2, 2, Color.Blue);
            var data = new DataObject();
            data.SetImage(bitmap);
            ClipboardService.Apply(ClipboardService.Capture(data));
            using var result = (Bitmap)Clipboard.GetImage()!;
            Assert.Equal(40, result.Width);
            Assert.Equal(Color.Blue.ToArgb(), result.GetPixel(2, 2).ToArgb());
            string path = Path.Combine(folder, "한글 파일.txt");
            File.WriteAllText(path, text);
            ClipboardService.Apply(new ClipboardContent { Format = "files", Paths = new[] { path, folder } });
            Assert.Equal(new[] { path, folder }, Clipboard.GetFileDropList().Cast<string>());
            using var effect = (MemoryStream)Clipboard.GetData("Preferred DropEffect")!;
            Assert.Equal(1, effect.ToArray()[0]);
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100);
            else Clipboard.Clear();
            Directory.Delete(folder, true);
        }
    });

    private static async Task OnSta(Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(true); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
