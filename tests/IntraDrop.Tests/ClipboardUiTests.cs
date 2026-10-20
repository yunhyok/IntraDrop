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

[CollectionDefinition("Native clipboard", DisableParallelization = true)]
public sealed class NativeClipboardCollection { }

[Collection("Native clipboard")]
public sealed class ClipboardUiTests
{
    [Fact]
    public Task PortableFormatsAndAutomaticSharingMenu() => OnSta(() =>
    {
        const string text = "한글 clipboard\r\n둘째 줄 😀";
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        Assert.Equal(text, Encoding.UTF8.GetString(ClipboardService.Capture(data).Data));
        Assert.Equal(text, Encoding.UTF8.GetString(ClipboardService.CaptureAutomatic(data)!.Data));
        data.SetData(DataFormats.FileDrop, new[] { @"C:\selected\file.txt" });
        Assert.Equal("files", ClipboardService.Capture(data).Format);
        Assert.Throws<InvalidOperationException>(() => ClipboardService.Capture(new DataObject("PrivateApplicationObject", "opaque")));
        Assert.Throws<InvalidOperationException>(() => ClipboardService.Capture(new DataObject(DataFormats.UnicodeText, new string('가', ClipboardContent.MaxTextBytes / 3 + 1))));

        // Test the real menu without starting listeners or altering user settings/autostart.
        var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
        var settings = new AppSettings();
        typeof(TrayApplicationContext).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, settings);
        using var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
        var clipboard = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "클립보드 자동 공유 중");
        Assert.Empty(clipboard.DropDownItems.Cast<ToolStripItem>());
        Assert.False(clipboard.Enabled);
        Assert.DoesNotContain(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text?.Contains("클립보드 전달") == true);
    });

    [Fact]
    public Task AutomaticFailuresOnlyUpdateMenuStatus() => OnSta(() =>
    {
        var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
        using var lifetime = new CancellationTokenSource();
        using var status = new ToolStripMenuItem();
        foreach (var pair in new Dictionary<string, object> { ["_lifetime"] = lifetime, ["_clipboardStatus"] = status })
            typeof(TrayApplicationContext).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, pair.Value);
        // No NotifyIcon is provided: automatic errors must not try to display a balloon.
        var report = typeof(TrayApplicationContext).GetMethod("ReportClipboardFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (string reason in new[] { "offline", "clipboard busy", "application failed" })
        {
            report.Invoke(context, new object[] { reason });
            Assert.Equal("클립보드 자동 공유 (전송 실패)", status.Text);
            Assert.Equal(reason, status.ToolTipText);
        }
    });

    [Fact]
    public Task AutomaticCaptureSkipsFileCopiesBeforeReadingTheirTextFallback() => OnSta(() =>
    {
        Assert.Null(ClipboardService.CaptureAutomatic(null));
        var pathText = new DataObject(DataFormats.UnicodeText, @"C:\a path typed as plain text.txt");
        Assert.Equal("text", ClipboardService.CaptureAutomatic(pathText)!.Format);
        using var preview = new Bitmap(2, 2);
        foreach (string format in new[] { DataFormats.FileDrop, "Shell IDList Array", "MountedVolume", "FileName", "FileNameW", "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents" })
        {
            var data = new DataObject();
            data.SetText("private filename or file preview");
            data.SetImage(preview);
            using var payload = new MemoryStream(new byte[] { 0 });
            data.SetData(format, false, payload);
            Assert.Null(ClipboardService.CaptureAutomatic(data));
        }
    });

    [Fact]
    public Task BroadcastUsesAllRegisteredComputersWithoutSelectingATarget() => OnSta(() =>
    {
        var settings = new AppSettings();
        var first = new PeerInfo { Host = "192.0.2.1", DeviceId = Guid.NewGuid().ToString("N") };
        var second = new PeerInfo { Host = "192.0.2.2", DeviceId = Guid.NewGuid().ToString("N") };
        settings.Peers.AddRange(new[] { first, second, new PeerInfo { Host = "192.0.2.3", DeviceId = settings.DeviceId } });
        var sent = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var completed = new CountdownEvent(2);
        using var lifetime = new CancellationTokenSource();
        using var broadcaster = new ClipboardBroadcaster((peer, content, _) =>
        {
            Assert.Equal("all peers", Encoding.UTF8.GetString(content.Data));
            sent.Add(peer.Host);
            return Task.CompletedTask;
        }, (_, _) => completed.Signal());
        var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
        foreach (var pair in new Dictionary<string, object> { ["_settings"] = settings, ["_lifetime"] = lifetime, ["_clipboardBroadcaster"] = broadcaster })
            typeof(TrayApplicationContext).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, pair.Value);
        using var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
        typeof(TrayApplicationContext).GetMethod("BroadcastClipboard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context,
            new object[] { new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("all peers") } });
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { first.Host, second.Host }, sent.OrderBy(host => host));
    });

    [Fact]
    public Task ImageBoundsAreCheckedBeforeDecoding() => OnSta(() =>
    {
        using var bitmap = new Bitmap(32, 16);
        var data = new DataObject();
        data.SetImage(bitmap);
        var content = ClipboardService.CaptureAutomatic(data)!;
        Assert.Equal("png", content.Format);
        ClipboardContent.ValidateData("png", content.Data);
        content.Data[16] = 0x7f;
        Assert.Throws<InvalidDataException>(() => ClipboardContent.ValidateData("png", content.Data));
        Assert.Throws<InvalidDataException>(() => ClipboardContent.ValidateData("png", new byte[33]));
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task IncomingClipboardCannotReplaceAnUnobservedLocalCopy() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        try
        {
            Clipboard.Clear();
            var copies = new List<ClipboardContent>();
            using var monitor = new ClipboardMonitor(copies.Add, _ => { });
            Clipboard.SetText("new local copy");
            Assert.False(ClipboardReader.HasReceivedClipboardFromCurrentProcess());
            var remote = new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("old remote copy") };
            Assert.Throws<InvalidOperationException>(() => monitor.ApplyReceived(remote));
            Assert.Equal("new local copy", Clipboard.GetText());
            PumpUntil(() => copies.Count == 1);
            Assert.Equal("new local copy", Encoding.UTF8.GetString(copies[0].Data));
            Assert.Throws<InvalidOperationException>(() => ClipboardService.Apply(remote, () => false));
            Assert.Equal("new local copy", Clipboard.GetText());
            monitor.ApplyReceived(remote);
            Assert.Equal("old remote copy", Clipboard.GetText());
            Assert.True(ClipboardReader.HasReceivedClipboardFromCurrentProcess());
            Assert.Null(ClipboardReader.CaptureAutomatic());
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    });

    // Another sync path (RDP, cloud clipboard, a self-registered peer) can hand an applied clipboard straight back.
    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task EchoedClipboardIsNeitherRewrittenNorBroadcastAgain() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        try
        {
            Clipboard.Clear();
            var copies = new List<ClipboardContent>();
            using var monitor = new ClipboardMonitor(copies.Add, error => throw error);
            var remote = new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("echoed remote copy") };
            monitor.ApplyReceived(remote);
            PumpFor(300);
            Assert.Empty(copies);

            Clipboard.SetText("echoed remote copy"); // echo: same text, no marker, new sequence
            PumpFor(600);
            Assert.Empty(copies);
            Assert.False(ClipboardReader.HasReceivedClipboardFromCurrentProcess());
            monitor.ApplyReceived(new ClipboardContent { Format = "text", Data = remote.Data.ToArray() });
            Assert.False(ClipboardReader.HasReceivedClipboardFromCurrentProcess()); // not rewritten
            Assert.Equal("echoed remote copy", Clipboard.GetText());

            Clipboard.SetText("new local copy");
            PumpUntil(() => copies.Count == 1);
            Clipboard.SetText("new local copy"); // a deliberate re-copy of sent content still goes out
            PumpUntil(() => copies.Count == 2);
            monitor.ApplyReceived(new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("new local copy") });
            Assert.False(ClipboardReader.HasReceivedClipboardFromCurrentProcess());
            monitor.ApplyReceived(remote);
            Assert.True(ClipboardReader.HasReceivedClipboardFromCurrentProcess());
            Assert.Equal("echoed remote copy", Clipboard.GetText());
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task CaptureWaitsWhileLocalInteractionDefersIt() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        try
        {
            Clipboard.Clear();
            bool busy = true;
            var copies = new List<ClipboardContent>();
            using var monitor = new ClipboardMonitor(copies.Add, _ => { }, () => busy);
            Clipboard.SetText("deferred copy");
            PumpFor(600);
            Assert.Empty(copies);
            busy = false;
            PumpUntil(() => copies.Count == 1);
            Assert.Equal("deferred copy", Encoding.UTF8.GetString(copies[0].Data));
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task ClearClipboardMenuEmptiesFormatsCancelsDeferredCaptureAndResumesSharing() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        string folder = Directory.CreateTempSubdirectory("IntraDrop-clear-clipboard-").FullName;
        string file = Path.Combine(folder, "keep.txt");
        File.WriteAllText(file, "keep source file");
        try
        {
            Clipboard.Clear();
            bool busy = true;
            var copies = new List<ClipboardContent>();
            var errors = new List<Exception>();
            using var monitor = new ClipboardMonitor(copies.Add, errors.Add, () => busy);
            using var lifetime = new CancellationTokenSource();
            using var broadcaster = new ClipboardBroadcaster((_, _, _) => Task.CompletedTask, (_, _) => { });
            var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
            foreach (var pair in new Dictionary<string, object>
            {
                ["_settings"] = new AppSettings(), ["_lifetime"] = lifetime,
                ["_clipboardMonitor"] = monitor, ["_clipboardBroadcaster"] = broadcaster,
            })
                typeof(TrayApplicationContext).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, pair.Value);
            using var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
            var clear = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "클립보드 초기화(&C)");
            var status = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "클립보드 자동 공유 중");

            using var bitmap = new Bitmap(8, 8);
            foreach (string format in new[] { "text", "image", "cut files" })
            {
                busy = true;
                if (format == "text") Clipboard.SetText("deferred copy must be discarded");
                else if (format == "image") Clipboard.SetImage(bitmap);
                else
                {
                    var data = new DataObject();
                    data.SetFileDropList(new System.Collections.Specialized.StringCollection { file, folder });
                    data.SetText("cut file preview");
                    using var effect = new MemoryStream(new byte[] { 2, 0, 0, 0 });
                    data.SetData("Preferred DropEffect", effect);
                    Clipboard.SetDataObject(data, true);
                }
                PumpFor(300);
                Assert.Empty(copies);
                // No NotifyIcon is provided: successful manual reset must not display a balloon.
                clear.PerformClick();
                Assert.Empty(Clipboard.GetDataObject()?.GetFormats() ?? Array.Empty<string>());
                Assert.Equal("클립보드 초기화 완료", status.Text);
                Assert.Equal("keep source file", File.ReadAllText(file));
                Assert.True(Directory.Exists(folder));
                busy = false;
                PumpFor(300);
                Assert.Empty(copies);
            }

            Clipboard.SetText("fresh copy after reset");
            PumpUntil(() => copies.Count == 1);
            Assert.Equal("fresh copy after reset", Encoding.UTF8.GetString(copies[0].Data));
            Clipboard.SetImage(bitmap);
            PumpUntil(() => copies.Count == 2);
            Assert.Equal(new[] { "text", "png" }, copies.Select(content => content.Format));
            Assert.Empty(errors);
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
            Directory.Delete(folder, true);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "InteractiveShell")]
    public Task ClearClipboardMenuRejectsAlreadyQueuedReceiveAndAllowsNewReceive(bool withMonitor) => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        try
        {
            Clipboard.Clear();
            var copies = new List<ClipboardContent>();
            var errors = new List<Exception>();
            using var monitor = new ClipboardMonitor(copies.Add, errors.Add, () => false);
            using var lifetime = new CancellationTokenSource();
            using var broadcaster = new ClipboardBroadcaster((_, _, _) => Task.CompletedTask, (_, _) => { });
            var sync = new QueuedSynchronizationContext();
            var context = (TrayApplicationContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayApplicationContext));
            foreach (var pair in new Dictionary<string, object>
            {
                ["_settings"] = new AppSettings(), ["_lifetime"] = lifetime, ["_sync"] = sync,
                ["_clipboardBroadcaster"] = broadcaster,
            })
                typeof(TrayApplicationContext).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, pair.Value);
            if (withMonitor)
                typeof(TrayApplicationContext).GetField("_clipboardMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, monitor);
            using var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
            var receive = typeof(TrayApplicationContext).GetMethod("OnClipboardReceivedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stale = (Task)receive.Invoke(context, new object[] { "peer", new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("stale receive") } })!;
            Assert.False(stale.IsCompleted);

            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "클립보드 초기화(&C)").PerformClick();
            var fresh = (Task)receive.Invoke(context, new object[] { "peer", new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("fresh receive") } })!;
            sync.RunNext();
            PumpUntil(() => stale.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => stale.GetAwaiter().GetResult());
            Assert.Empty(Clipboard.GetDataObject()?.GetFormats() ?? Array.Empty<string>());
            Assert.False(fresh.IsCompleted);

            sync.RunNext();
            PumpUntil(() => fresh.IsCompleted);
            fresh.GetAwaiter().GetResult();
            Assert.Equal("fresh receive", Clipboard.GetText());
            PumpFor(300);
            Assert.Empty(copies);
            Assert.Empty(errors);
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task NativeReaderPreservesUnicodeAndSkipsShellFileFallbacks() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        try
        {
            const string text = "최신 셀\t123\r\n😀";
            Clipboard.SetText(text);
            Assert.Equal(text, Encoding.UTF8.GetString(ClipboardReader.CaptureAutomatic()!.Data));
            foreach (string format in new[] { "Shell IDList Array", "MountedVolume", "FileName", "FileNameW", "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents" })
            {
                var data = new DataObject(DataFormats.UnicodeText, text);
                using var stream = new MemoryStream(new byte[] { 0 });
                data.SetData(format, false, stream);
                Clipboard.SetDataObject(data, true);
                Assert.Null(ClipboardReader.CaptureAutomatic());
            }
            Clipboard.SetText(new string('가', ClipboardContent.MaxTextBytes / 3 + 1));
            Assert.Throws<InvalidOperationException>(() => ClipboardReader.CaptureAutomatic());
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task FileCopyAndCutStayLocalWithoutChangingClipboardOrFiles() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        string folder = Directory.CreateTempSubdirectory("IntraDrop-local-files-").FullName;
        string file = Path.Combine(folder, "local.txt");
        File.WriteAllText(file, "keep local");
        var copies = new List<ClipboardContent>();
        var errors = new List<Exception>();
        try
        {
            using var monitor = new ClipboardMonitor(copies.Add, errors.Add);
            foreach (byte effect in new byte[] { 1, 2 })
            {
                var data = new DataObject();
                var paths = new System.Collections.Specialized.StringCollection();
                paths.AddRange(new[] { file, folder });
                data.SetFileDropList(paths);
                data.SetText("file preview must not be shared either");
                using var dropEffect = new MemoryStream(new byte[] { effect, 0, 0, 0 });
                data.SetData("Preferred DropEffect", dropEffect);
                Clipboard.SetDataObject(data, true);
                PumpFor(500);
                Assert.Empty(copies);
                Assert.Equal(new[] { file, folder }, Clipboard.GetFileDropList().Cast<string>());
                using var actualEffect = (MemoryStream)Clipboard.GetData("Preferred DropEffect")!;
                Assert.Equal(effect, actualEffect.ToArray()[0]);
                Assert.Equal("keep local", File.ReadAllText(file));
            }
            Clipboard.SetText("text still shares");
            PumpUntil(() => copies.Count == 1);
            using var bitmap = new Bitmap(8, 8);
            Clipboard.SetImage(bitmap);
            PumpUntil(() => copies.Count == 2);
            Assert.Equal(new[] { "text", "png" }, copies.Select(content => content.Format));
            Assert.Empty(errors);
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100);
            else Clipboard.Clear();
            Directory.Delete(folder, true);
        }
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task LocalCopiesAreObservedWithoutRebroadcastingReceivedFormats() => OnSta(() =>
    {
        var original = Clipboard.GetDataObject();
        string folder = Directory.CreateTempSubdirectory("IntraDrop-monitor-").FullName;
        var copies = new List<ClipboardContent>();
        var errors = new List<Exception>();
        try
        {
            Clipboard.SetText("existing clipboard must stay local");
            using var monitor = new ClipboardMonitor(copies.Add, errors.Add);
            PumpFor(300);
            Assert.Empty(copies);

            Clipboard.SetText("local copy 한글");
            PumpUntil(() => copies.Count == 1);
            Assert.Equal("local copy 한글", Encoding.UTF8.GetString(copies[0].Data));

            monitor.ApplyReceived(new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("remote") });
            PumpFor(300);
            Assert.Single(copies);
            Assert.Equal("remote", Clipboard.GetText());

            using var bitmap = new Bitmap(8, 8);
            var data = new DataObject();
            data.SetImage(bitmap);
            // Direct writes also carry the marker, covering delayed notifications/rendering.
            ClipboardService.Apply(ClipboardService.Capture(data));
            PumpFor(300);
            Assert.Single(copies);
            ClipboardService.Apply(new ClipboardContent { Format = "files", Paths = new[] { folder } });
            PumpFor(300);
            Assert.Single(copies);
            Assert.Equal(folder, Assert.Single(Clipboard.GetFileDropList().Cast<string>()));

            Clipboard.Clear();
            PumpFor(300);
            Assert.Single(copies);
            Clipboard.SetText("next local copy");
            PumpUntil(() => copies.Count == 2);
            Assert.Equal("next local copy", Encoding.UTF8.GetString(copies[1].Data));
            monitor.Dispose();
            Clipboard.SetText("after disposal");
            PumpFor(300);
            Assert.Equal(2, copies.Count);
            Assert.Empty(errors);
        }
        finally
        {
            if (original != null) Clipboard.SetDataObject(original, true, 10, 100);
            else Clipboard.Clear();
            Directory.Delete(folder, true);
        }
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

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));

        public void RunNext()
        {
            var (callback, state) = _queue.Dequeue();
            callback(state);
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) PumpFor(20);
        Assert.True(completed(), "Clipboard update was not delivered by the native message loop.");
    }

    private static void PumpFor(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < until);
    }

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
