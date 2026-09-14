using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using IntraDrop.Core;
using IntraDrop.Models;
using IntraDrop.UI;

// Desktop smoke probe: compile as IntraDrop.Tests against each target framework.
internal static class ClipboardCompatibilityProbe
{
    [STAThread]
    private static void Main(string[] args)
    {
        string mode = args[0], root = Path.GetFullPath(args[1]);
        int port = int.Parse(args[2]);
        Directory.CreateDirectory(root);
        Application.EnableVisualStyles();
        using var form = new Form { Text = AppInfo.DisplayName + " 클립보드 검증 - " + mode,
            Width = 540, Height = 240, StartPosition = FormStartPosition.CenterScreen };
        var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = "IntraDrop 클립보드 검증\r\n한글과 이모지 😀" };
        form.Controls.Add(text);
        var settings = new AppSettings { DeviceName = "Clipboard probe", Port = port,
            DeviceId = mode == "receive" ? "22222222222222222222222222222222" : "11111111111111111111111111111111",
            DownloadFolder = Path.Combine(root, "received"), AcceptFromRegisteredOnly = true };
        settings.Peers.Add(new PeerInfo { Nickname = "검증용 수신 PC", Host = mode == "receive" ? "127.0.0.1" : "127.0.0.2",
            DeviceId = mode == "receive" ? "11111111111111111111111111111111" : "22222222222222222222222222222222" });
        SettingsStore.SetSecret(settings, "clipboard-local-smoke-only");
        var server = new TransferServer { GetSettings = () => settings };
        var original = mode == "receive" ? null : Clipboard.GetDataObject();
        using var tray = new NotifyIcon { Text = AppInfo.DisplayName + " 검증", Icon = SystemIcons.Application, Visible = true };
        using var lifetime = new CancellationTokenSource();
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(root, "stop"))) form.Close(); };
        form.Shown += async (_, _) =>
        {
            try
            {
#pragma warning disable SYSLIB0050
                var context = (TrayApplicationContext)FormatterServices.GetUninitializedObject(typeof(TrayApplicationContext));
#pragma warning restore SYSLIB0050
                foreach (var pair in new Dictionary<string, object> { ["_settings"] = settings, ["_sync"] = SynchronizationContext.Current!, ["_tray"] = tray, ["_lifetime"] = lifetime })
                    typeof(TrayApplicationContext).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, pair.Value);
                if (mode == "receive")
                {
                    server.ApplyClipboardAsync = async (sender, content) =>
                    {
                        await (Task)typeof(TrayApplicationContext).GetMethod("OnClipboardReceivedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, new object[] { sender, content })!;
                        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        form.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var read = ClipboardService.Capture(Clipboard.GetDataObject());
                                if (read.Format != content.Format) throw new Exception("Clipboard format mismatch");
                                if (content.Format == "text" && !read.Data.SequenceEqual(content.Data)) throw new Exception("Unicode mismatch");
                                if (content.Format == "png")
                                {
                                    using var image = (Bitmap)Clipboard.GetImage()!;
                                    if (image.Width != 40 || image.Height != 20 || image.GetPixel(2, 2).ToArgb() != Color.Blue.ToArgb()) throw new Exception("Image mismatch");
                                }
                                if (content.Format == "files" && (!read.Paths.SequenceEqual(content.Paths) || !content.Paths.Any(Directory.Exists))) throw new Exception("File clipboard mismatch");
                                text.Text = "클립보드 수신 완료: " + content.Format + "\r\nCtrl+V로 붙여넣으세요.";
                                File.AppendAllText(Path.Combine(root, "receipts.txt"), content.Format + "\n");
                                applied.SetResult(true);
                            }
                            catch (Exception ex) { applied.SetException(ex); }
                        }));
                        await applied.Task;
                    };
                    server.Start(port, System.Net.IPAddress.Parse("127.0.0.2"));
                    File.WriteAllText(Path.Combine(root, "ready"), AppInfo.DisplayName);
                    timer.Start();
                    return;
                }
                if (mode == "menu")
                {
                    var menu = (ContextMenuStrip)typeof(TrayApplicationContext).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!;
                    tray.ContextMenuStrip = menu;
                    var button = new Button { Text = "트레이 메뉴 보기", Dock = DockStyle.Bottom, Height = 40 };
                    button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
                    form.Controls.Add(button);
                    return;
                }
                var contents = new List<ClipboardContent> { new() { Format = "text", Data = Encoding.UTF8.GetBytes(text.Text) } };
                using (var bitmap = new Bitmap(40, 20))
                {
                    bitmap.SetPixel(2, 2, Color.Blue);
                    var data = new DataObject(); data.SetImage(bitmap);
                    contents.Add(ClipboardService.Capture(data));
                }
                string first = Path.Combine(root, "source-a", "같은 이름.txt");
                string second = Path.Combine(root, "source-b", "같은 이름.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
                File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
                string folder = Path.Combine(root, "빈 폴더"); Directory.CreateDirectory(folder);
                contents.Add(new ClipboardContent { Format = "files", Paths = new[] { first, second, folder } });
                foreach (var content in contents)
                {
                    ClipboardService.Apply(content);
                    var captured = ClipboardService.Capture(Clipboard.GetDataObject());
                    await TransferClient.SendClipboardAsync("127.0.0.2", port, settings.DeviceName, captured,
                        "clipboard-local-smoke-only", null, lifetime.Token, settings.DeviceId, settings.Peers[0].DeviceId);
                }
                var files = Directory.GetFiles(settings.DownloadFolder, "*.txt", SearchOption.AllDirectories);
                if (files.Length != 2 || !files.Any(p => File.ReadAllText(p) == "first") || !files.Any(p => File.ReadAllText(p) == "second")) throw new Exception("Saved files mismatch");
                File.WriteAllText(Path.Combine(root, "success"), "Unicode, PNG pixels, files, duplicate names, empty folder: PASS");
                form.Close();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "error-" + mode + ".txt"), ex.ToString()); form.Close(); Environment.ExitCode = 1; }
        };
        form.FormClosed += (_, _) =>
        {
            lifetime.Cancel(); server.Stop(); tray.Visible = false;
            if (mode != "receive")
            {
                if (original != null) Clipboard.SetDataObject(original, true, 10, 100); else Clipboard.Clear();
                File.WriteAllText(Path.Combine(root, "stop"), "done");
            }
        };
        Application.Run(form);
    }
}
