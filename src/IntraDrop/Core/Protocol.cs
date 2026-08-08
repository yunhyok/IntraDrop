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
        await stream.ReadExactlyAsync(len, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (length <= 0 || length > MaxJsonLength)
            throw new InvalidDataException($"잘못된 메시지 길이: {length}");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
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
        await stream.ReadExactlyAsync(buf, ct);
        if (!buf.SequenceEqual(Magic))
            throw new InvalidDataException("IntraDrop 프로토콜이 아닙니다.");
    }

    /// <summary>유휴 시간 제한을 두고 한 번 읽는다. 상대가 사라져도 무한 대기하지 않는다.</summary>
    public static async Task<int> ReadWithIdleTimeoutAsync(
        Stream stream, byte[] buffer, int offset, int count, int idleMs, CancellationToken ct)
    {
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
