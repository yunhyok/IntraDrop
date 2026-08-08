using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace IntraDrop.Core;

public class TransferItem
{
    public string Path { get; set; } = "";   // '/' 구분 상대 경로
    public long Size { get; set; }
}

public class TransferHeader
{
    public string Type { get; set; } = "transfer";   // "transfer" | "ping"
    public string SenderName { get; set; } = "";
    public long TotalSize { get; set; }
    public List<TransferItem> Items { get; set; } = new();
}

public class PongMessage
{
    public string Type { get; set; } = "pong";
    public string Name { get; set; } = "";
}

public static class Protocol
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("IDRP1");
    public const int MaxJsonLength = 32 * 1024 * 1024;
    private const int JsonIdleMs = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task WriteJsonAsync(Stream stream, object obj, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonOptions);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        await stream.WriteAsync(len, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] len = new byte[4];
        await ReadExactlyIdleAsync(stream, len, JsonIdleMs, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (length <= 0 || length > MaxJsonLength)
            throw new InvalidDataException($"잘못된 메시지 길이: {length}");
        byte[] payload = new byte[length];
        await ReadExactlyIdleAsync(stream, payload, JsonIdleMs, ct);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
               ?? throw new InvalidDataException("메시지를 해석할 수 없습니다.");
    }

    public static async Task WriteMagicAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync(Magic, ct);
    }

    public static async Task ReadMagicAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = new byte[Magic.Length];
        await ReadExactlyIdleAsync(stream, buf, JsonIdleMs, ct);
        if (!buf.SequenceEqual(Magic))
            throw new InvalidDataException("IntraDrop 프로토콜이 아닙니다.");
    }

    private static async Task ReadExactlyIdleAsync(Stream stream, byte[] buffer, int idleMs, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await ReadWithIdleTimeoutAsync(stream, buffer, offset, buffer.Length - offset, idleMs, ct);
            if (n == 0) throw new EndOfStreamException("연결이 종료되었습니다.");
            offset += n;
        }
    }

    /// <summary>유휴 시간 제한을 두고 한 번 읽는다. 상대가 사라져도 무한 대기하지 않는다.
    /// net48에서는 소켓 읽기가 CancellationToken 으로 중단되지 않으므로 WhenAny 방식을 쓴다.
    /// (시간 초과 시 호출 측이 연결을 닫으면서 미완료 읽기가 함께 정리된다.)</summary>
    public static async Task<int> ReadWithIdleTimeoutAsync(
        Stream stream, byte[] buffer, int offset, int count, int idleMs, CancellationToken ct)
    {
#if NETFRAMEWORK
        Task<int> read = stream.ReadAsync(buffer, offset, count, ct);
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task done = await Task.WhenAny(read, Task.Delay(idleMs, delayCts.Token)).ConfigureAwait(false);
            if (done != read)
            {
                read.Observe();   // 버려지는 read 의 뒤늦은 예외를 소비
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("상대방 응답이 없습니다 (시간 초과).");
            }
            delayCts.Cancel();   // I/O 완료 시 Delay 타이머·토큰 등록 즉시 해제
        }
        return await read.ConfigureAwait(false);
#else
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleMs);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(offset, count), idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("상대방 응답이 없습니다 (시간 초과).");
        }
#endif
    }

    /// <summary>유휴 시간 제한을 두고 쓴다. 상대가 데이터를 받지 않으면 시간 초과로 실패한다.</summary>
    public static async Task WriteWithIdleTimeoutAsync(
        Stream stream, byte[] buffer, int offset, int count, int idleMs, CancellationToken ct)
    {
#if NETFRAMEWORK
        Task write = stream.WriteAsync(buffer, offset, count, ct);
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task done = await Task.WhenAny(write, Task.Delay(idleMs, delayCts.Token)).ConfigureAwait(false);
            if (done != write)
            {
                write.Observe();   // 버려지는 write 의 뒤늦은 예외를 소비
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("상대방이 데이터를 받지 않습니다 (시간 초과).");
            }
            delayCts.Cancel();   // I/O 완료 시 Delay 타이머·토큰 등록 즉시 해제
        }
        await write.ConfigureAwait(false);
#else
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleMs);
        try
        {
            await stream.WriteAsync(buffer.AsMemory(offset, count), idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("상대방이 데이터를 받지 않습니다 (시간 초과).");
        }
#endif
    }

    public static async Task<byte> ReadByteWithTimeoutAsync(Stream stream, int timeoutMs, CancellationToken ct)
    {
        byte[] one = new byte[1];
        int n = await ReadWithIdleTimeoutAsync(stream, one, 0, 1, timeoutMs, ct);
        if (n == 0) throw new EndOfStreamException("연결이 종료되었습니다.");
        return one[0];
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
