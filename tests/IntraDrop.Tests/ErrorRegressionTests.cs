using System.Net;
using System.Reflection;
using System.Windows.Forms;
using IntraDrop.Core;
using IntraDrop.Models;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ErrorRegressionTests
{
    [Fact]
    public async Task ImpossibleTransferSizeIsRejectedBeforeAcceptingData()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var settings = new AppSettings { DownloadFolder = Path.GetTempPath(), ConfirmThresholdMB = int.MaxValue };
        var server = new TransferServer { GetSettings = () => settings, ConfirmRequest = _ => true };
        server.Start(port);
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct.Token);
            using var stream = tcp.GetStream();
            await Protocol.WriteMagicAsync(stream, Protocol.FlagPlain, ct.Token);
            byte[] nonce = await Protocol.ReadNonceAsync(stream, ct.Token);
            await Segment.WriteSegmentAsync(stream, null, nonce, Segment.IndexA, Protocol.ToJsonBytes(new TransferHeader
            {
                TotalSize = long.MaxValue,
                Items = new List<TransferItem> { new() { Path = "impossible.bin", Size = long.MaxValue } },
            }), ct.Token);
            Assert.Equal(Protocol.StatusRefused, await Protocol.ReadByteAsync(stream, ct.Token));
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReceivingFilePreservesExistingPartFile(bool secured, bool tampered)
    {
        string root = Path.Combine(Path.GetTempPath(), "IntraDrop-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "report.txt");
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        Directory.CreateDirectory(settings.DownloadFolder);
        await File.WriteAllTextAsync(source, "new report");
        string existing = Path.Combine(settings.DownloadFolder, "report.txt.part");
        await File.WriteAllTextAsync(existing, "existing file must survive");
        string? secret = secured ? "test-shared-secret" : null;
        if (secret != null) SettingsStore.SetSecret(settings, secret);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new TransferServer { GetSettings = () => settings };
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.TransferFailed += (_, reason) => failure.TrySetResult(reason);
        server.Start(port);
        try
        {
            if (tampered)
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port, ct.Token);
                using var stream = tcp.GetStream();
                var key = KeyMaterial.FromSecret(secret);
                byte[] data = await File.ReadAllBytesAsync(source);
                await Protocol.WriteMagicAsync(stream, Protocol.FlagSecured, ct.Token);
                byte[] nonce = await Protocol.ReadNonceAsync(stream, ct.Token);
                await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA, Protocol.ToJsonBytes(new TransferHeader
                {
                    TotalSize = data.Length,
                    Items = new List<TransferItem> { new() { Path = "report.txt", Size = data.Length } },
                }), ct.Token);
                Assert.Equal(Protocol.StatusAccepted, await Protocol.ReadByteAsync(stream, ct.Token));
                using var segment = new MemoryStream();
                await Segment.WriteSegmentAsync(segment, key, nonce, Segment.IndexB, data, ct.Token);
                byte[] bytes = segment.ToArray();
                bytes[bytes.Length - 1] ^= 1; // corrupt the MAC after otherwise valid encrypted data
                await stream.WriteAsync(bytes, ct.Token);
                await failure.Task.WaitAsync(ct.Token);
                Assert.False(File.Exists(Path.Combine(settings.DownloadFolder, "report.txt")));
                Assert.Single(Directory.GetFiles(settings.DownloadFolder));
            }
            else
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TransferClient.SendAsync(
                    "127.0.0.1", port, "sender", new[] { source }, secret, null, CancellationToken.None)));
                string[] received = Directory.GetFiles(settings.DownloadFolder, "*.txt");
                Assert.Equal(8, received.Length);
                Assert.All(received, file => Assert.Equal("new report", File.ReadAllText(file)));
                Assert.Equal(9, Directory.GetFiles(settings.DownloadFolder).Length);
            }
            Assert.True(File.Exists(existing), "Receiving report.txt removed the existing report.txt.part file.");
            Assert.Equal("existing file must survive", await File.ReadAllTextAsync(existing));
        }
        finally { server.Stop(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "", "old-secret")]
    [InlineData(true, "", null)]
    [InlineData(true, "replacement-secret", "replacement-secret")]
    public async Task SettingsSavesNewSecretAfterConfirmedClear(bool clear, string input, string? expected)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new AppSettings();
                SettingsStore.SetSecret(settings, "old-secret");
                using var form = new SettingsForm(settings);
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                // Reproduce the state after confirming the clear dialog, then entering text.
                typeof(SettingsForm).GetField("_clearSecret", fields)!.SetValue(form, clear);
                ((TextBox)typeof(SettingsForm).GetField("_secret", fields)!.GetValue(form)!).Text = input;
                form.ApplyTo(settings);
                Assert.Contains(AppInfo.DisplayName, form.Text);
                Assert.Equal(expected, SettingsStore.ReadSecret(settings).Secret);
                completion.SetResult(true);
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
