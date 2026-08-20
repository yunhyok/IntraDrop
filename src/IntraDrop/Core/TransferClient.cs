using System.Net;
using System.Net.Sockets;

namespace IntraDrop.Core;

/// <summary>서버가 돌려준 상태 코드에 해당하는 예외. Message 를 그대로 사용자에게 보여주면 된다.</summary>
public class TransferStatusException : Exception
{
    public TransferStatusException(byte status, string message) : base(message)
    {
        Status = status;
    }

    public byte Status { get; }

    public static TransferStatusException From(byte status) =>
        status == Protocol.StatusRejected
            ? new TransferRejectedException()
            : new TransferStatusException(status, Describe(status));

    private static string Describe(byte status) => status switch
    {
        Protocol.StatusAuthFailed =>
            "공유 암호가 일치하지 않습니다. 양쪽 컴퓨터에 같은 암호를 설정하세요.",
        Protocol.StatusSecretRequired =>
            "상대방이 공유 암호를 요구합니다. 설정에서 같은 암호를 입력하세요.",
        Protocol.StatusNotRegistered =>
            "상대방의 허용 목록에 이 컴퓨터가 없습니다. 상대방에게 등록을 요청하세요.",
        Protocol.StatusNoServerSecret =>
            "상대방에게는 공유 암호가 설정되어 있지 않습니다. 양쪽 설정을 맞추세요.",
        Protocol.StatusWrongDevice =>
            "지정한 대상 장치 ID와 연결된 컴퓨터가 아닙니다. 피어 주소를 확인하고 다시 등록하세요.",
        Protocol.StatusRefused =>
            "상대방이 지금 파일을 받을 수 없습니다 (저장 공간 부족 등).",
        _ => $"상대방이 전송을 거부했습니다 (코드 {status}).",
    };
}

public class TransferRejectedException : TransferStatusException
{
    public TransferRejectedException()
        : base(Protocol.StatusRejected, "상대방이 수신을 거절했습니다.") { }
}

public record TransferProgress(string CurrentFile, int FileIndex, int FileCount, long SentBytes, long TotalBytes);

public static class TransferClient
{
    private const int ConnectTimeoutMs = 10_000;
    private const int HeaderIdleMs = 30_000;
    private const int AcceptWaitMs = 130_000;   // 수신 측 수락 대기 (수신 대화상자 60초 + 여유)
    private const int AckWaitMs = 120_000;
    private const int WriteIdleMs = 60_000;

    /// <summary>Authenticated identity refresh. UDP discovery only supplies the candidate endpoint.</summary>
    public static async Task<TransferHeader> RediscoverAsync(string host, int port, string myName,
        string myDeviceId, string targetDeviceId, string secret, int timeoutMs = ConnectTimeoutMs, CancellationToken cancellationToken = default)
    {
        var key = KeyMaterial.FromSecret(secret);
        if (key == null) throw new InvalidOperationException("공유 암호가 필요합니다.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cts.Token);
        using var net = tcp.GetStream();
        using var stream = new TimeoutStream(net, timeoutMs, timeoutMs, cts.Token);
        await Protocol.WriteMagicAsync(stream, Protocol.FlagSecured, cts.Token);
        byte[] nonce = await Protocol.ReadNonceAsync(stream, cts.Token);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA,
            Protocol.ToJsonBytes(new TransferHeader { Type = "rediscover", SenderName = myName, SenderDeviceId = myDeviceId, RecipientDeviceId = targetDeviceId }), cts.Token);
        byte status = await Protocol.ReadByteAsync(stream, cts.Token);
        if (status != Protocol.StatusAccepted) throw TransferStatusException.From(status);
        byte[] json = await Segment.ReadSegmentAsync(stream, key, nonce, Segment.IndexC, Protocol.MaxJsonLength, cts.Token);
        var reply = Protocol.FromJsonBytes<TransferHeader>(json);
        if (!string.Equals(reply.Type, "identity", StringComparison.OrdinalIgnoreCase) || !string.Equals(reply.SenderDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("상대 장치 ID가 일치하지 않습니다.");
        return reply;
    }

    public static async Task<IReadOnlyList<PeerHint>> RequestPeerSnapshotAsync(string host, int port,
        string myName, string myDeviceId, string targetDeviceId, string secret, int timeoutMs = 5000,
        IReadOnlyList<string>? requestedDeviceIds = null, CancellationToken cancellationToken = default)
    {
        if (requestedDeviceIds == null || requestedDeviceIds.Count != 1 || requestedDeviceIds.Any(id => !Guid.TryParse(id, out _)) || requestedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedDeviceIds.Count) throw new ArgumentException("요청 장치 ID가 잘못되었습니다.");
        string requestedId = requestedDeviceIds[0];
        var key = KeyMaterial.FromSecret(secret) ?? throw new InvalidOperationException("공유 암호가 필요합니다.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); cts.CancelAfter(timeoutMs);
        using var tcp = new TcpClient(); await tcp.ConnectAsync(host, port, cts.Token);
        using var stream = new TimeoutStream(tcp.GetStream(), timeoutMs, timeoutMs, cts.Token);
        await Protocol.WriteMagicAsync(stream, Protocol.FlagSecured, cts.Token);
        byte[] nonce = await Protocol.ReadNonceAsync(stream, cts.Token);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA,
            Protocol.ToJsonBytes(new TransferHeader { Type = "peer_snapshot", SenderName = myName, SenderDeviceId = myDeviceId, RecipientDeviceId = targetDeviceId, RequestedDeviceIds = requestedDeviceIds?.Take(64).ToList() ?? new List<string>() }), cts.Token);
        byte status = await Protocol.ReadByteAsync(stream, cts.Token);
        if (status != Protocol.StatusAccepted) throw TransferStatusException.From(status);
        var response = Protocol.FromJsonBytes<TransferHeader>(await Segment.ReadSegmentAsync(stream, key, nonce, Segment.IndexC, Protocol.MaxPeerSnapshotResponseLength, cts.Token));
        if (!string.Equals(response.Type, "peer_snapshot", StringComparison.OrdinalIgnoreCase) || !string.Equals(response.SenderDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase) || !string.Equals(response.RecipientDeviceId, myDeviceId, StringComparison.OrdinalIgnoreCase) || response.PeerHints == null || response.PeerHints.Count > 64) throw new InvalidDataException("잘못된 피어 힌트 응답입니다.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<PeerHint>();
        foreach (var h in response.PeerHints)
        {
            if (!Guid.TryParse(h.DeviceId, out _) || !IPAddress.TryParse(h.Host, out _) || !ids.Add(h.DeviceId) || !string.Equals(h.DeviceId, requestedId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("잘못된 피어 힌트입니다.");
            result.Add(h);
        }
        return result;
    }

    /// <summary>온라인 여부 확인. 성공하면 상대 장치 이름을 반환한다.
    /// ping 은 언제나 평문이며 상대의 공유 암호 설정과 무관하게 응답한다.</summary>
    public static async Task<string?> PingAsync(string host, int port, string myName, int timeoutMs = 2500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            using var net = tcp.GetStream();
            using var stream = new TimeoutStream(net, timeoutMs, timeoutMs, cts.Token);

            await Protocol.WriteMagicAsync(stream, Protocol.FlagPlain, cts.Token);
            byte[] nonce = await Protocol.ReadNonceAsync(stream, cts.Token);
            await Segment.WriteSegmentAsync(stream, null, nonce, Segment.IndexA,
                Protocol.ToJsonBytes(new TransferHeader { Type = "ping", SenderName = myName }), cts.Token);

            var pong = await Protocol.ReadJsonAsync<PongMessage>(stream, cts.Token);
            return pong.Type == "pong" ? pong.Name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>상대방에게 이 컴퓨터를 등록해 달라고 요청한다 (상호 자동 등록).
    /// 수동으로 컴퓨터를 추가할 때만 호출한다 — 자동 등록된 peer 에게 되보내면 등록 루프가 생긴다.</summary>
    public static async Task<TransferHeader?> RegisterAsync(
        string host, int port, string myName, string? secret, int timeoutMs = ConnectTimeoutMs,
        string? senderDeviceId = null, string? recipientDeviceId = null, CancellationToken cancellationToken = default)
    {
        var key = KeyMaterial.FromSecret(secret);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cts.Token);
        using var net = tcp.GetStream();
        using var stream = new TimeoutStream(net, timeoutMs, timeoutMs, cts.Token);

        await Protocol.WriteMagicAsync(stream, Flags(key), cts.Token);
        byte[] nonce = await Protocol.ReadNonceAsync(stream, cts.Token);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA,
            Protocol.ToJsonBytes(new TransferHeader { Type = "register", SenderName = myName,
                SenderDeviceId = key == null ? "" : senderDeviceId ?? "",
                RecipientDeviceId = key == null ? "" : recipientDeviceId ?? "" }), cts.Token);

        byte status = await Protocol.ReadByteAsync(stream, cts.Token);
        if (status != Protocol.StatusAccepted)
            throw TransferStatusException.From(status);
        if (key == null) return null;
        try
        {
            byte[] json = await Segment.ReadSegmentAsync(stream, key, nonce, Segment.IndexC, Protocol.MaxJsonLength, cts.Token);
            var reply = Protocol.FromJsonBytes<TransferHeader>(json);
            if (!string.IsNullOrWhiteSpace(recipientDeviceId) && !string.Equals(reply.SenderDeviceId, recipientDeviceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("상대 장치 ID가 일치하지 않습니다.");
            return reply;
        }
        catch (EndOfStreamException) { return null; }
        catch (TimeoutException) { return null; }
    }

    public static async Task<int> SendAsync(
        string host, int port, string senderName,
        IReadOnlyList<string> paths,
        string? secret,
        IProgress<TransferProgress>? progress,
        CancellationToken ct,
        string? senderDeviceId = null,
        string? recipientDeviceId = null)
    {
        var files = CollectFiles(paths);
        if (files.Count == 0)
            throw new InvalidOperationException("보낼 파일이 없습니다.");
        if (files.Count > Protocol.MaxItemCount)
            throw new InvalidOperationException($"한 번에 보낼 수 있는 파일은 {Protocol.MaxItemCount}개까지입니다.");

        long totalSize = files.Sum(f => f.Size);
        var key = KeyMaterial.FromSecret(secret);
        var header = new TransferHeader
        {
            Type = "transfer",
            SenderName = senderName,
            SenderDeviceId = key == null ? "" : senderDeviceId ?? "",
            RecipientDeviceId = key == null ? "" : recipientDeviceId ?? "",
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

        using var net = tcp.GetStream();
        using var stream = new TimeoutStream(net, HeaderIdleMs, WriteIdleMs, ct);

        await Protocol.WriteMagicAsync(stream, Flags(key), ct);
        byte[] nonce = await Protocol.ReadNonceAsync(stream, ct);

        // 세그먼트 A = 헤더 JSON
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexA,
            Protocol.ToJsonBytes(header), ct);

        stream.ReadIdleMs = AcceptWaitMs;
        byte status = await Protocol.ReadByteAsync(stream, ct);
        if (status != Protocol.StatusAccepted)
            throw TransferStatusException.From(status);

        // 세그먼트 B = 모든 파일 바이트 연속 (헤더 Items 순서)
        stream.ReadIdleMs = AckWaitMs;
        using (var writer = await Segment.BeginWriteSegmentAsync(
                   stream, key, nonce, Segment.IndexB, totalSize, ct))
        {
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

                    await writer.WriteAsync(buffer, 0, n, ct);

                    remaining -= n;
                    sentTotal += n;
                    progress?.Report(new TransferProgress(relPath, i + 1, files.Count, sentTotal, totalSize));
                }
            }
            await writer.CompleteAsync(ct);
        }

        byte ack = await Protocol.ReadByteAsync(stream, ct);
        if (ack != 1)
            throw new IOException("상대방이 저장을 완료하지 못했습니다.");

        return files.Count;
    }

    private static byte Flags(KeyMaterial? key) =>
        key != null ? Protocol.FlagSecured : Protocol.FlagPlain;

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
