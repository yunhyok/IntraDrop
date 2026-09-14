using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ClipboardTransferTests
{
    [Fact]
    public async Task ForgedRawSuccessCannotAcknowledgeSecuredClipboard()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fake = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await Protocol.ReadMagicAsync(stream, timeout.Token);
            await stream.WriteAsync(new byte[KeyMaterial.NonceLength], timeout.Token);
            // This endpoint has no shared key. Raw acceptance/completion must not suffice.
            await stream.WriteAsync(new byte[] { 1, 1 }, timeout.Token);
            await Task.Delay(500, timeout.Token);
        });
        await Assert.ThrowsAnyAsync<IOException>(() => TransferClient.SendClipboardAsync(
            "127.0.0.1", port, "sender", new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("verify receipt") },
            "secret-never-known-by-fake", null, timeout.Token));
        await fake;
    }

    [Fact]
    public async Task CapturedSecuredReceiptCannotBeReplayedForFreshClipboardRequest()
    {
        const string secret = "receipt-replay-secret";
        string senderId = Guid.NewGuid().ToString("N");
        string recipientId = Guid.NewGuid().ToString("N");
        byte[] nonce = Enumerable.Repeat((byte)0x5a, KeyMaterial.NonceLength).ToArray();
        byte[][] payloads =
        {
            Encoding.UTF8.GetBytes("first value"),
            Encoding.UTF8.GetBytes("other value"),
        };
        var headers = new List<TransferHeader>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var endpoint = Task.Run(async () =>
        {
            var key = KeyMaterial.FromSecret(secret)!;
            byte[] capturedReceipt = Array.Empty<byte>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = tcp.GetStream();
                Assert.Equal(Protocol.FlagSecured, await Protocol.ReadMagicAsync(stream, timeout.Token));
                await stream.WriteAsync(nonce, 0, nonce.Length, timeout.Token);
                await stream.FlushAsync(timeout.Token);

                var header = Protocol.FromJsonBytes<TransferHeader>(await Segment.ReadSegmentAsync(
                    stream, key, nonce, Segment.IndexA, Protocol.MaxJsonLength, timeout.Token));
                headers.Add(header);
                Assert.Equal("clipboard", header.Type);
                Assert.Equal("text", header.ClipboardFormat);
                Assert.Equal(senderId, header.SenderDeviceId);
                Assert.Equal(recipientId, header.RecipientDeviceId);

                await Protocol.WriteByteAsync(stream, Protocol.StatusAccepted, timeout.Token);
                Assert.Equal(payloads[attempt], await Segment.ReadSegmentAsync(
                    stream, key, nonce, Segment.IndexB, ClipboardContent.MaxTextBytes, timeout.Token));
                await Protocol.WriteByteAsync(stream, 1, timeout.Token);

                if (attempt == 0)
                {
                    using var receipt = new MemoryStream();
                    await Segment.WriteSegmentAsync(receipt, key, nonce, Segment.IndexC,
                        Protocol.ToJsonBytes(new TransferHeader
                        {
                            Type = "clipboard_applied",
                            ClipboardFormat = header.ClipboardFormat,
                            ClipboardRequestId = header.ClipboardRequestId,
                            SenderDeviceId = recipientId,
                            RecipientDeviceId = senderId,
                        }), timeout.Token);
                    capturedReceipt = receipt.ToArray();
                }

                // The second connection replays the exact signed receipt captured from the first.
                await stream.WriteAsync(capturedReceipt, 0, capturedReceipt.Length, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
        });

        Assert.Equal(1, await TransferClient.SendClipboardAsync(
            "127.0.0.1", port, "sender", new ClipboardContent { Format = "text", Data = payloads[0] },
            secret, null, timeout.Token, senderId, recipientId));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TransferClient.SendClipboardAsync(
            "127.0.0.1", port, "sender", new ClipboardContent { Format = "text", Data = payloads[1] },
            secret, null, timeout.Token, senderId, recipientId));
        Assert.Contains("인증", error.Message);
        await endpoint;

        Assert.Equal(2, headers.Count);
        Assert.NotEqual(headers[0].ClipboardRequestId, headers[1].ClipboardRequestId);
        Assert.All(headers, header =>
            Assert.Equal(KeyMaterial.NonceLength, Convert.FromBase64String(header.ClipboardRequestId).Length));
    }

    [Fact]
    public async Task SecuredSuccessWaitsForActualClipboardApplication()
    {
        const string secret = "receipt-secret";
        var settings = new AppSettings();
        SettingsStore.SetSecret(settings, secret);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TransferServer { GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => { entered.TrySetResult(true); return applied.Task; } };
        int port = FreePort();
        server.Start(port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var send = TransferClient.SendClipboardAsync("127.0.0.1", port, "sender",
                new ClipboardContent { Format = "text", Data = Encoding.UTF8.GetBytes("await apply") },
                secret, null, timeout.Token, Guid.NewGuid().ToString("N"), settings.DeviceId);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.False(send.IsCompleted);
            applied.SetResult(true);
            Assert.Equal(1, await send);
        }
        finally { applied.TrySetResult(true); server.Stop(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shared-secret")]
    public async Task UnicodeTextRoundTripsPlainAndEncrypted(string? secret)
    {
        string root = NewRoot();
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        if (secret != null) SettingsStore.SetSecret(settings, secret);
        ClipboardContent? applied = null;
        string? appliedSender = null;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (sender, content) =>
            {
                appliedSender = sender;
                applied = content;
                return Task.CompletedTask;
            },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes("한글 clipboard 😀");
            int count = await TransferClient.SendClipboardAsync("127.0.0.1", port, "sender",
                new ClipboardContent { Format = "text", Data = payload }, secret, null, CancellationToken.None,
                Guid.NewGuid().ToString("N"), settings.DeviceId);

            Assert.Equal(1, count);
            Assert.Equal("sender", appliedSender);
            Assert.NotNull(applied);
            Assert.Equal("text", applied!.Format);
            Assert.Equal(payload, applied.Data);
            Assert.Empty(applied.Paths);
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task PngRoundTripsEncrypted()
    {
        string root = NewRoot();
        const string secret = "image-secret";
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        SettingsStore.SetSecret(settings, secret);
        ClipboardContent? applied = null;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, content) => { applied = content; return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            byte[] png = MakePng();
            await TransferClient.SendClipboardAsync("127.0.0.1", port, "sender",
                new ClipboardContent { Format = "png", Data = png }, secret, null, CancellationToken.None,
                Guid.NewGuid().ToString("N"), settings.DeviceId);
            Assert.NotNull(applied);
            Assert.Equal("png", applied!.Format);
            Assert.Equal(png, applied.Data);
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task FilesPreserveDuplicateRootsAndEmptyFoldersInsideOneBatch()
    {
        string root = NewRoot();
        string first = Path.Combine(root, "one", "Same");
        string second = Path.Combine(root, "two", "Same");
        Directory.CreateDirectory(Path.Combine(first, "empty"));
        Directory.CreateDirectory(Path.Combine(second, "nested", "empty-too"));
        File.WriteAllText(Path.Combine(first, "first.txt"), "first");
        File.WriteAllText(Path.Combine(second, "nested", "second.txt"), "second");
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        ClipboardContent? applied = null;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, content) => { applied = content; return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            int count = await TransferClient.SendClipboardAsync("127.0.0.1", port, "sender",
                new ClipboardContent { Format = "files", Paths = new[] { first, second } },
                null, null, CancellationToken.None);

            Assert.Equal(2, count);
            Assert.NotNull(applied);
            Assert.Equal(new[] { "Same", "Same (2)" }, applied!.Paths.Select(Path.GetFileName));
            Assert.Single(applied.Paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("first", File.ReadAllText(Path.Combine(applied.Paths[0], "first.txt")));
            Assert.True(Directory.Exists(Path.Combine(applied.Paths[0], "empty")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(applied.Paths[1], "nested", "second.txt")));
            Assert.True(Directory.Exists(Path.Combine(applied.Paths[1], "nested", "empty-too")));
            Assert.StartsWith("Clipboard-", Path.GetFileName(Path.GetDirectoryName(applied.Paths[0])));
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task CallbackFailureReturnsFailureAndKeepsVerifiedFiles()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "report.txt");
        File.WriteAllText(source, "retained");
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => Task.FromException(new InvalidOperationException("clipboard busy")),
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => TransferClient.SendClipboardAsync(
                "127.0.0.1", port, "sender", new ClipboardContent { Format = "files", Paths = new[] { source } },
                null, null, CancellationToken.None));
            Assert.Contains("클립보드", error.Message);
            string batch = Assert.Single(Directory.GetDirectories(settings.DownloadFolder, "Clipboard-*"));
            Assert.Equal("retained", File.ReadAllText(Path.Combine(batch, "report.txt")));
            Assert.Empty(Directory.GetDirectories(settings.DownloadFolder, ".intradrop-*.part"));
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task MalformedFileManifestsAreRejectedBeforeClipboardOrDiskMutation()
    {
        string root = NewRoot();
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        int applications = 0;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref applications); return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            var invalid = new[]
            {
                new List<TransferItem> { new() { Path = "../escape.txt" } },
                new List<TransferItem> { new() { Path = "C:/escape.txt" } },
                new List<TransferItem> { new() { Path = "safe/CON.txt" }, new() { Path = "safe", IsDirectory = true } },
                new List<TransferItem> { new() { Path = "root" }, new() { Path = "root/child.txt" } },
                new List<TransferItem> { new() { Path = "same.txt" }, new() { Path = "SAME.TXT" } },
            };
            foreach (var items in invalid)
            {
                byte status = await SendHeaderAndReadStatusAsync(port, null, new TransferHeader
                {
                    Type = "clipboard", ClipboardFormat = "files", TotalSize = 0, Items = items,
                });
                Assert.Equal(Protocol.StatusRefused, status);
            }
            Assert.Equal(0, applications);
            Assert.False(Directory.Exists(settings.DownloadFolder));
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task TamperedEncryptedPayloadNeverReachesClipboard()
    {
        string root = NewRoot();
        const string secret = "tamper-secret";
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        SettingsStore.SetSecret(settings, secret);
        int applications = 0;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref applications); return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var stream = tcp.GetStream();
            var key = KeyMaterial.FromSecret(secret);
            await Protocol.WriteMagicAsync(stream, Protocol.FlagSecured, timeout.Token);
            byte[] nonce = await Protocol.ReadNonceAsync(stream, timeout.Token);
            byte[] payload = Encoding.UTF8.GetBytes("untampered text");
            await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA, Protocol.ToJsonBytes(new TransferHeader
            {
                Type = "clipboard", ClipboardFormat = "text",
                ClipboardRequestId = Convert.ToBase64String(Crypto.NewNonce()), TotalSize = payload.Length,
            }), timeout.Token);
            Assert.Equal(Protocol.StatusAccepted, await Protocol.ReadByteAsync(stream, timeout.Token));

            using var encoded = new MemoryStream();
            await Segment.WriteSegmentAsync(encoded, key, nonce, Segment.IndexB, payload, timeout.Token);
            byte[] bytes = encoded.ToArray();
            bytes[bytes.Length - 1] ^= 1;
            await stream.WriteAsync(bytes, timeout.Token);
            Assert.Equal(0, await Protocol.ReadByteAsync(stream, timeout.Token));
            Assert.Equal(0, applications);
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task SecuredClipboardRejectsInvalidRequestIdBeforeClipboardMutation()
    {
        const string secret = "request-id-secret";
        var settings = new AppSettings();
        SettingsStore.SetSecret(settings, secret);
        int applications = 0;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref applications); return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            string[] invalidIds =
            {
                "",
                "not-base64",
                Convert.ToBase64String(new byte[KeyMaterial.NonceLength - 1]),
                Convert.ToBase64String(new byte[KeyMaterial.NonceLength]) + " ",
            };
            foreach (string requestId in invalidIds)
            {
                byte status = await SendHeaderAndReadStatusAsync(port, secret, new TransferHeader
                {
                    Type = "clipboard",
                    ClipboardFormat = "text",
                    ClipboardRequestId = requestId,
                    TotalSize = 1,
                });
                Assert.Equal(Protocol.StatusRefused, status);
            }
            Assert.Equal(0, applications);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task WrongSecretIsRejectedWithoutClipboardMutation()
    {
        string root = NewRoot();
        var settings = new AppSettings { DownloadFolder = Path.Combine(root, "received") };
        SettingsStore.SetSecret(settings, "correct-secret");
        int applications = 0;
        var server = new TransferServer
        {
            GetSettings = () => settings,
            ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref applications); return Task.CompletedTask; },
        };
        int port = FreePort();
        server.Start(port);
        try
        {
            var error = await Assert.ThrowsAsync<TransferStatusException>(() => TransferClient.SendClipboardAsync(
                "127.0.0.1", port, "sender", new ClipboardContent
                { Format = "text", Data = Encoding.UTF8.GetBytes("secret") },
                "wrong-secret", null, CancellationToken.None));
            Assert.Equal(Protocol.StatusAuthFailed, error.Status);
            Assert.Equal(0, applications);
        }
        finally { server.Stop(); DeleteRoot(root); }
    }

    [Fact]
    public async Task OldPeerCloseProducesUpgradeMessage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task oldPeer = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            using var stream = tcp.GetStream();
            byte flags = await Protocol.ReadMagicAsync(stream, CancellationToken.None);
            byte[] nonce = Crypto.NewNonce();
            await stream.WriteAsync(nonce);
            var header = Protocol.FromJsonBytes<TransferHeader>(await Segment.ReadSegmentAsync(
                stream, null, nonce, Segment.IndexA, Protocol.MaxJsonLength, CancellationToken.None));
            Assert.Equal(Protocol.FlagPlain, flags);
            Assert.Equal("clipboard", header.Type);
        });
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TransferClient.SendClipboardAsync(
                "127.0.0.1", port, "sender", new ClipboardContent
                { Format = "text", Data = Encoding.UTF8.GetBytes("upgrade") },
                null, null, CancellationToken.None));
            Assert.Contains("1.8.1", error.Message);
            await oldPeer;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task MissingFileIsRejectedBeforeConnecting()
    {
        string missing = Path.Combine(NewRoot(), "missing.txt");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TransferClient.SendClipboardAsync(
                "127.0.0.1", 1, "sender", new ClipboardContent { Format = "files", Paths = new[] { missing } },
                null, null, CancellationToken.None));
            Assert.Contains("찾을 수 없습니다", error.Message);
        }
        finally { DeleteRoot(Path.GetDirectoryName(missing)!); }
    }

    private static async Task<byte> SendHeaderAndReadStatusAsync(int port, string? secret, TransferHeader header)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var stream = tcp.GetStream();
        var key = KeyMaterial.FromSecret(secret);
        await Protocol.WriteMagicAsync(stream, key == null ? Protocol.FlagPlain : Protocol.FlagSecured, timeout.Token);
        byte[] nonce = await Protocol.ReadNonceAsync(stream, timeout.Token);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA, Protocol.ToJsonBytes(header), timeout.Token);
        return await Protocol.ReadByteAsync(stream, timeout.Token);
    }

    private static byte[] MakePng()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string NewRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "IntraDrop-clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
