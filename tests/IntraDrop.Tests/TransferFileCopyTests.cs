using System.Net;
using System.Net.Sockets;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class TransferFileCopyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenFilesSendSavedCopiesWithOneNoticeAndOriginalNames(bool secured)
    {
        using var receiver = new LoopbackReceiver(secured);
        string ordinary = Path.Combine(receiver.Root, "ordinary.txt");
        string documents = Path.Combine(receiver.Root, "documents");
        string first = Path.Combine(documents, "nested", "report.bin");
        string second = Path.Combine(receiver.Root, "second.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        byte[] saved = Enumerable.Range(0, 100_003).Select(i => (byte)(i % 251)).ToArray();
        byte[] otherSaved = { 8, 6, 7, 5, 3, 0, 9 };
        await File.WriteAllTextAsync(ordinary, "ordinary content");
        await File.WriteAllBytesAsync(first, saved);
        await File.WriteAllBytesAsync(second, otherSaved);
        using var writer = new FileStream(first, FileMode.Open, FileAccess.Write, FileShare.Read);
        using var otherWriter = new FileStream(second, FileMode.Open, FileAccess.Write, FileShare.Read);
        var notices = new List<string>();
        var notice = new InlineNotice(path =>
        {
            notices.Add(path);
            // A synchronous notice must run after the saved copy is complete.
            writer.SetLength(0);
            writer.Write(new byte[] { 42, 43 });
            writer.Flush();
        });

        int count = await TransferClient.SendAsync("127.0.0.1", receiver.Port, "sender",
            new[] { ordinary, documents, second }, receiver.Secret, null, receiver.Token, copyNotice: notice);

        Assert.Equal(3, count);
        Assert.Equal("documents/nested/report.bin", Assert.Single(notices));
        Assert.Equal(saved, await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "documents", "nested", "report.bin")));
        Assert.Equal(otherSaved, await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "second.bin")));
        Assert.Equal("ordinary content", await File.ReadAllTextAsync(Path.Combine(receiver.DownloadFolder, "ordinary.txt")));
        Assert.Equal(3, Directory.GetFiles(receiver.DownloadFolder, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(2, new FileInfo(first).Length);
    }

    [Fact]
    public async Task OrdinaryFileSendsWithoutCopyNotice()
    {
        using var receiver = new LoopbackReceiver();
        string source = Path.Combine(receiver.Root, "ordinary.bin");
        byte[] expected = { 0, 1, 2, 255 };
        await File.WriteAllBytesAsync(source, expected);
        var notices = new List<string>();

        int count = await TransferClient.SendAsync("127.0.0.1", receiver.Port, "sender",
            new[] { source }, null, null, receiver.Token, copyNotice: new InlineNotice(notices.Add));

        Assert.Equal(1, count);
        Assert.Empty(notices);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "ordinary.bin")));
        Assert.Single(Directory.GetFiles(receiver.DownloadFolder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedReadRefusesWithActionableMessageAndNoSuccess(bool byteRangeLock)
    {
        using var receiver = new LoopbackReceiver();
        string source = Path.Combine(receiver.Root, "report.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        using var held = File.Open(source, FileMode.Open, FileAccess.Read, byteRangeLock ? FileShare.Read : FileShare.None);
        if (byteRangeLock) held.Lock(0, 1);
        var notices = new List<string>();
        int completed = 0;
        receiver.Server.TransferCompleted += (_, _, _) => Interlocked.Increment(ref completed);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Server.TransferFailed += (_, _) => failed.TrySetResult();

        var error = await Assert.ThrowsAsync<IOException>(() => TransferClient.SendAsync(
            "127.0.0.1", receiver.Port, "sender", new[] { source }, null, null, receiver.Token,
            copyNotice: new InlineNotice(notices.Add)));

        Assert.Contains("report.bin", error.Message);
        Assert.Contains("파일을 닫은 뒤 다시 보내세요.", error.Message);
        Assert.Empty(notices);
        if (byteRangeLock) await failed.Task.WaitAsync(receiver.Token);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Empty(Directory.GetFiles(receiver.DownloadFolder, "*", SearchOption.AllDirectories));
    }

    private sealed class InlineNotice(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class LoopbackReceiver : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IntraDrop-test-" + Guid.NewGuid().ToString("N"));
        public string DownloadFolder => Path.Combine(Root, "received");
        public string? Secret { get; }
        public int Port { get; }
        public TransferServer Server { get; }
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        public CancellationToken Token => _timeout.Token;

        public LoopbackReceiver(bool secured = false)
        {
            Directory.CreateDirectory(DownloadFolder);
            var settings = new AppSettings { DownloadFolder = DownloadFolder, ConfirmThresholdMB = int.MaxValue };
            Secret = secured ? "test-shared-secret" : null;
            if (Secret != null) SettingsStore.SetSecret(settings, Secret);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Server = new TransferServer { GetSettings = () => settings, ConfirmRequest = _ => true };
            Server.Start(Port);
        }

        public void Dispose()
        {
            Server.Stop();
            _timeout.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
