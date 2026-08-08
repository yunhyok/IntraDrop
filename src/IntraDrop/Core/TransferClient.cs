using System.Net.Sockets;

namespace IntraDrop.Core;

public class TransferRejectedException : Exception
{
    public TransferRejectedException() : base("상대방이 수신을 거절했습니다.") { }
}

public record TransferProgress(string CurrentFile, int FileIndex, int FileCount, long SentBytes, long TotalBytes);

public static class TransferClient
{
    private const int ConnectTimeoutMs = 10_000;
    private const int AcceptWaitMs = 130_000;   // 수신 측 수락 대기 (수신 대화상자 60초 + 여유)
    private const int AckWaitMs = 120_000;
    private const int WriteIdleMs = 60_000;

    /// <summary>온라인 여부 확인. 성공하면 상대 장치 이름을 반환한다.</summary>
    public static async Task<string?> PingAsync(string host, int port, string myName, int timeoutMs = 2500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            using var stream = tcp.GetStream();
            await Protocol.WriteMagicAsync(stream, cts.Token);
            await Protocol.WriteJsonAsync(stream,
                new TransferHeader { Type = "ping", SenderName = myName }, cts.Token);
            var pong = await Protocol.ReadJsonAsync<PongMessage>(stream, cts.Token);
            return pong.Type == "pong" ? pong.Name : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<int> SendAsync(
        string host, int port, string senderName,
        IReadOnlyList<string> paths,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        var files = CollectFiles(paths);
        if (files.Count == 0)
            throw new InvalidOperationException("보낼 파일이 없습니다.");

        long totalSize = files.Sum(f => f.Size);
        var header = new TransferHeader
        {
            Type = "transfer",
            SenderName = senderName,
            TotalSize = totalSize,
            Items = files.Select(f => new TransferItem { Path = f.RelPath, Size = f.Size }).ToList(),
        };

        using var tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeoutMs);
            try
            {
                await tcp.ConnectAsync(host, port, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{host} 에 연결할 수 없습니다.");
            }
        }

        using var stream = tcp.GetStream();
        await Protocol.WriteMagicAsync(stream, ct);
        await Protocol.WriteJsonAsync(stream, header, ct);

        byte accepted = await Protocol.ReadByteWithTimeoutAsync(stream, AcceptWaitMs, ct);
        if (accepted != 1)
            throw new TransferRejectedException();

        long sentTotal = 0;
        byte[] buffer = new byte[81920];
        for (int i = 0; i < files.Count; i++)
        {
            var (fullPath, relPath, size) = files[i];
            progress?.Report(new TransferProgress(relPath, i + 1, files.Count, sentTotal, totalSize));

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long remaining = size;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int n = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (n == 0)
                    throw new IOException($"전송 중 파일이 변경되었습니다: {relPath}");

                await Protocol.WriteWithIdleTimeoutAsync(stream, buffer, 0, n, WriteIdleMs, ct);

                remaining -= n;
                sentTotal += n;
                progress?.Report(new TransferProgress(relPath, i + 1, files.Count, sentTotal, totalSize));
            }
        }
        await stream.FlushAsync(ct);

        byte ack = await Protocol.ReadByteWithTimeoutAsync(stream, AckWaitMs, ct);
        if (ack != 1)
            throw new IOException("상대방이 저장을 완료하지 못했습니다.");

        return files.Count;
    }

    private static List<(string FullPath, string RelPath, long Size)> CollectFiles(IReadOnlyList<string> paths)
    {
        var result = new List<(string, string, long)>();
        foreach (var raw in paths)
        {
            string path = Path.GetFullPath(raw);
            if (File.Exists(path))
            {
                result.Add((path, Path.GetFileName(path)!, new FileInfo(path).Length));
            }
            else if (Directory.Exists(path))
            {
                string baseDir = path.TrimEnd('\\', '/');
                string baseName = Path.GetFileName(baseDir);
                foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                {
                    string rel = PathCompat.GetRelativePath(baseDir, f).Replace('\\', '/');
                    result.Add((f, baseName + "/" + rel, new FileInfo(f).Length));
                }
            }
        }
        return result;
    }
}
