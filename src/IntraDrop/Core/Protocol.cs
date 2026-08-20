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
    public string Type { get; set; } = "transfer";   // "transfer" | "ping" | "register"
    public string SenderName { get; set; } = "";
    public string SenderDeviceId { get; set; } = "";
    public string RecipientDeviceId { get; set; } = "";
    public long TotalSize { get; set; }
    public List<TransferItem> Items { get; set; } = new();
}

public class PongMessage
{
    public string Type { get; set; } = "pong";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public static class Protocol
{
    /// <summary>보안 프로토콜 v2 매직. v1(IDRP1)과는 호환되지 않는다.</summary>
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("IDRP2");

    public const int MaxJsonLength = 32 * 1024 * 1024;

    /// <summary>한 번의 전송에 담을 수 있는 파일 개수 상한.</summary>
    public const int MaxItemCount = 10_000;

    // ── 연결 플래그 ──────────────────────────────────────────────────────
    public const byte FlagPlain = 0x00;     // 평문 (공유 암호 없음)
    public const byte FlagSecured = 0x01;   // 암호화·인증

    // ── 서버 → 클라이언트 상태 코드 ──────────────────────────────────────
    public const byte StatusRejected = 0;         // 사용자가 거절
    public const byte StatusAccepted = 1;         // 수락 / 진행
    public const byte StatusAuthFailed = 2;       // 암호 불일치 또는 MAC 오류
    public const byte StatusSecretRequired = 3;   // 서버가 공유 암호를 요구 (평문 거부)
    public const byte StatusNotRegistered = 4;    // 서버의 허용 목록에 없음
    public const byte StatusNoServerSecret = 5;   // 서버에 암호 미설정 (암호화 불가)
    public const byte StatusRefused = 6;          // 기타 거부 (디스크 부족 등)
    public const byte StatusWrongDevice = 7;      // authenticated recipient mismatch

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] ToJsonBytes(object obj) =>
        JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonOptions);

    public static T FromJsonBytes<T>(byte[] json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("메시지를 해석할 수 없습니다.");

    /// <summary>4바이트 길이 접두사 JSON 메시지를 쓴다 (pong 응답 전용).</summary>
    public static async Task WriteJsonAsync(Stream stream, object obj, CancellationToken ct)
    {
        byte[] payload = ToJsonBytes(obj);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        await stream.WriteAsync(len, 0, len.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>4바이트 길이 접두사 JSON 메시지를 읽는다 (pong 응답 전용).</summary>
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] len = new byte[4];
        await ReadExactlyAsync(stream, len, 0, len.Length, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (length <= 0 || length > MaxJsonLength)
            throw new InvalidDataException($"잘못된 메시지 길이: {length}");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, 0, payload.Length, ct).ConfigureAwait(false);
        return FromJsonBytes<T>(payload);
    }

    /// <summary>매직 5바이트 + 플래그 1바이트를 쓴다.</summary>
    public static async Task WriteMagicAsync(Stream stream, byte flags, CancellationToken ct)
    {
        byte[] buf = new byte[Magic.Length + 1];
        Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
        buf[Magic.Length] = flags;
        await stream.WriteAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>매직 5바이트 + 플래그 1바이트를 읽고 플래그를 돌려준다.
    /// 매직이 다르면(구버전 IDRP1 포함) 예외를 던진다.</summary>
    public static async Task<byte> ReadMagicAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = new byte[Magic.Length + 1];
        await ReadExactlyAsync(stream, buf, 0, buf.Length, ct).ConfigureAwait(false);
        for (int i = 0; i < Magic.Length; i++)
        {
            if (buf[i] != Magic[i])
                throw new InvalidDataException("IntraDrop 프로토콜이 아닙니다.");
        }
        return buf[Magic.Length];
    }

    /// <summary>서버가 보내는 세션 nonce 16바이트를 읽는다.</summary>
    public static async Task<byte[]> ReadNonceAsync(Stream stream, CancellationToken ct)
    {
        byte[] nonce = new byte[KeyMaterial.NonceLength];
        await ReadExactlyAsync(stream, nonce, 0, nonce.Length, ct).ConfigureAwait(false);
        return nonce;
    }

    public static async Task ReadExactlyAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer, offset + read, count - read, ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("연결이 종료되었습니다.");
            read += n;
        }
    }

    public static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        byte[] one = new byte[1];
        await ReadExactlyAsync(stream, one, 0, 1, ct).ConfigureAwait(false);
        return one[0];
    }

    public static async Task WriteByteAsync(Stream stream, byte value, CancellationToken ct)
    {
        await stream.WriteAsync(new[] { value }, 0, 1, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
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
