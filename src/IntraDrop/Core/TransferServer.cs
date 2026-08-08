using System.Net;
using System.Net.Sockets;
using IntraDrop.Models;

namespace IntraDrop.Core;

public class TransferServer
{
    private const int HeaderIdleMs = 30_000;
    private const int DataIdleMs = 60_000;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Func<AppSettings> GetSettings { get; set; } = () => new AppSettings();

    /// <summary>임계 크기 이상 수신 요청. UI 스레드에서 수락 여부를 결정해 돌려준다.</summary>
    public Func<TransferHeader, bool>? ConfirmRequest { get; set; }

    public event Action<string, int, string>? TransferCompleted;   // 보낸이, 파일 수, 저장 폴더
    public event Action<string, string>? TransferFailed;           // 보낸이, 사유
    public event Action<string, long>? TransferRejected;           // 보낸이, 크기

    public bool IsRunning => _listener != null;

    public void Start(int port)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string sender = "알 수 없음";
        try
        {
            using var _ = client;
            using var stream = client.GetStream();

            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headerCts.CancelAfter(HeaderIdleMs);
                await Protocol.ReadMagicAsync(stream, headerCts.Token);

                var header = await Protocol.ReadJsonAsync<TransferHeader>(stream, headerCts.Token);
                sender = string.IsNullOrWhiteSpace(header.SenderName) ? sender : header.SenderName;

                if (header.Type == "ping")
                {
                    await Protocol.WriteJsonAsync(stream,
                        new PongMessage { Name = GetSettings().DeviceName }, headerCts.Token);
                    return;
                }

                if (header.Type != "transfer")
                    return;

                await ReceiveTransferAsync(stream, header, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TransferFailed?.Invoke(sender, ex.Message);
        }
    }

    private async Task ReceiveTransferAsync(NetworkStream stream, TransferHeader header, CancellationToken ct)
    {
        var settings = GetSettings();
        string sender = string.IsNullOrWhiteSpace(header.SenderName) ? "알 수 없음" : header.SenderName;

        // 경로 검증 (경로 탈출 차단)
        var items = new List<(string RelPath, long Size)>();
        foreach (var item in header.Items)
        {
            string? safe = SanitizeRelativePath(item.Path);
            if (safe == null || item.Size < 0)
                throw new InvalidDataException("잘못된 파일 경로가 포함되어 있습니다.");
            items.Add((safe, item.Size));
        }
        if (items.Count == 0)
            throw new InvalidDataException("받을 파일이 없습니다.");

        bool accept = true;

        // 디스크 여유 공간 확인
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(settings.DownloadFolder)) ?? "C:\\";
            if (new DriveInfo(root).AvailableFreeSpace < header.TotalSize + (64L << 20))
                accept = false;
        }
        catch { /* 확인 불가 시 계속 진행 */ }

        // 임계 크기 이상이면 사용자 수락 필요
        if (accept && header.TotalSize >= settings.ConfirmThresholdBytes)
            accept = ConfirmRequest?.Invoke(header) ?? false;

        stream.WriteByte(accept ? (byte)1 : (byte)0);
        await stream.FlushAsync(ct);

        if (!accept)
        {
            TransferRejected?.Invoke(sender, header.TotalSize);
            return;
        }

        string folder = settings.DownloadFolder;
        Directory.CreateDirectory(folder);

        byte[] buffer = new byte[81920];
        foreach (var (relPath, size) in items)
        {
            string dest = MakeUniquePath(Path.Combine(folder, relPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            string partPath = dest + ".part";
            try
            {
                using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int n = await Protocol.ReadWithIdleTimeoutAsync(stream, buffer, 0, toRead, DataIdleMs, ct);
                        if (n == 0)
                            throw new EndOfStreamException("전송이 중단되었습니다.");
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                        remaining -= n;
                    }
                }
                File.Move(partPath, dest, overwrite: false);
            }
            catch
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                throw;
            }
        }

        stream.WriteByte(1);   // 저장 완료 응답
        await stream.FlushAsync(ct);

        TransferCompleted?.Invoke(sender, items.Count, folder);
    }

    private static string? SanitizeRelativePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string normalized = raw.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var safeParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.EndsWith(':')) return null;
            var chars = part.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string cleaned = new string(chars).TrimEnd(' ', '.');
            if (cleaned.Length == 0) return null;
            safeParts.Add(cleaned);
        }
        return Path.Combine(safeParts.ToArray());
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
