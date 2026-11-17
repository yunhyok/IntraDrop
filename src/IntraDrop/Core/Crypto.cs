using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IntraDrop.Core;

/// <summary>세그먼트 인증(MAC) 실패. 서버는 이 예외를 status 2 로 응답한다.</summary>
public class SegmentAuthException : Exception
{
    public SegmentAuthException()
        : base("공유 암호가 일치하지 않거나 데이터가 손상되었습니다.") { }
}

/// <summary>공유 암호에서 유도한 키 재료.
/// PBKDF2 는 느리므로 암호별로 한 번만 계산해 캐시한다 (연결마다 재계산 금지).</summary>
public sealed class KeyMaterial
{
    public const int NonceLength = 16;
    public const int IvLength = 16;
    public const int MacLength = 32;

    private const int Iterations = 150_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("IntraDrop.v2.salt");

    private static readonly object CacheSync = new();
    private static string? _cachedSecret;
    private static KeyMaterial? _cached;

    private KeyMaterial(byte[] encKey, byte[] macKey)
    {
        EncKey = encKey;
        MacKey = macKey;
    }

    /// <summary>AES-256 암호화 키 (32바이트).</summary>
    public byte[] EncKey { get; }

    /// <summary>HMAC-SHA256 키 (32바이트).</summary>
    public byte[] MacKey { get; }

    /// <summary>공유 암호에서 키를 유도한다. 암호가 비어 있으면 null(= 평문 모드).</summary>
    public static KeyMaterial? FromSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;

        lock (CacheSync)
        {
            if (_cached != null && string.Equals(_cachedSecret, secret, StringComparison.Ordinal))
                return _cached;
        }

        byte[] derived;
        using (var kdf = new Rfc2898DeriveBytes(
                   Encoding.UTF8.GetBytes(secret!), Salt, Iterations, HashAlgorithmName.SHA256))
        {
            derived = kdf.GetBytes(64);
        }

        byte[] enc = new byte[32];
        byte[] mac = new byte[32];
        Buffer.BlockCopy(derived, 0, enc, 0, 32);
        Buffer.BlockCopy(derived, 32, mac, 0, 32);
        Array.Clear(derived, 0, derived.Length);

        var material = new KeyMaterial(enc, mac);
        lock (CacheSync)
        {
            _cachedSecret = secret;
            _cached = material;
        }
        return material;
    }
}

/// <summary>암호 관련 소소한 도우미.</summary>
public static class Crypto
{
    public static byte[] NewNonce() => RandomBytes(KeyMaterial.NonceLength);

    public static byte[] RandomBytes(int count)
    {
        byte[] buf = new byte[count];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(buf);
        return buf;
    }

    /// <summary>길이가 같은 두 배열을 시간 차이 없이 비교한다.
    /// (net48 에는 CryptographicOperations.FixedTimeEquals 가 없다.)</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

/// <summary>CryptoStream 과 소켓 사이에 끼우는 암호문 통과 스트림 (encrypt-then-MAC).
/// 쓰기: 내보내는 암호문을 그대로 MAC 에 넣는다.
/// 읽기: 정해진 길이의 암호문만 읽어 MAC 에 넣고, 끝에 이르면 뒤따르는 MAC 32B 를 먼저 검증한 뒤에야 EOF 를 돌려준다.
/// CryptoStream 은 EOF 를 본 뒤에야 마지막 블록(PKCS7 패딩)을 복호하므로, 위조된 암호문은 패딩 검사에 닿지 못한다.</summary>
public sealed class CiphertextMacStream : Stream
{
    private readonly Stream _inner;
    private readonly HMACSHA256 _hmac;
    private readonly bool _reading;
    private long _remaining;
    private bool _finalized;

    /// <param name="cipherLength">읽기 모드에서 읽을 암호문 길이. null 이면 쓰기 모드.</param>
    public CiphertextMacStream(Stream inner, HMACSHA256 hmac, long? cipherLength = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hmac = hmac ?? throw new ArgumentNullException(nameof(hmac));
        _reading = cipherLength.HasValue;
        _remaining = cipherLength ?? 0;
    }

    /// <summary>아직 읽지 않은 암호문 바이트 수.</summary>
    public long Remaining => _remaining;

    /// <summary>뒤따르는 MAC 을 읽어 일치함을 확인했으면 true.</summary>
    public bool Verified { get; private set; }

    public override bool CanRead => _reading;
    public override bool CanWrite => !_reading;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (!_reading) throw new NotSupportedException();
        if (_remaining > 0)
        {
            int n = await _inner.ReadAsync(buffer, offset, (int)Math.Min(count, _remaining), ct).ConfigureAwait(false);
            // 중간에 끊긴 스트림을 EOF 로 넘기면 CryptoStream 이 불완전한 데이터로 패딩을 검사한다.
            if (n <= 0) throw new EndOfStreamException("전송이 중단되었습니다.");
            _hmac.TransformBlock(buffer, offset, n, null, 0);
            _remaining -= n;
            return n;
        }
        if (!Verified) await VerifyAsync(ct).ConfigureAwait(false);
        return 0;
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        if (_finalized) throw new SegmentAuthException();   // 이미 실패한 검증을 다시 통과시키지 않는다
        _finalized = true;
        _hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] actual = new byte[KeyMaterial.MacLength];
        await Protocol.ReadExactlyAsync(_inner, actual, 0, actual.Length, ct).ConfigureAwait(false);
        if (!Crypto.FixedTimeEquals(_hmac.Hash!, actual)) throw new SegmentAuthException();
        Verified = true;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_reading) throw new NotSupportedException();
        if (count <= 0) return;
        _hmac.TransformBlock(buffer, offset, count, null, 0);
        await _inner.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
    }
}

/// <summary>연결 와이어 포맷의 세그먼트 읽기/쓰기 (프로토콜 v3).
/// secured: [IV 16B][plainLen 8B LE][ciphertext][MAC 32B]
/// plain  : [plainLen 8B LE][plaintext]
/// MAC = HMACSHA256(macKey) over nonce(16) ∥ segIndex(1B) ∥ IV(16) ∥ plainLen(8B LE) ∥ ciphertext 전체.
/// 받는 쪽은 MAC 이 맞은 뒤에만 마지막 블록(패딩)을 복호한다 (v2 는 평문에 MAC 을 걸고 복호 뒤 검증했다).</summary>
public static class Segment
{
    /// <summary>세그먼트 A = 헤더 JSON.</summary>
    public const byte IndexA = 0;

    /// <summary>세그먼트 B = 파일 바이트 연속.</summary>
    public const byte IndexB = 1;
    /// <summary>Identity rediscovery request/reply.</summary>
    public const byte IndexC = 2;

    private const int BlockSize = 16;

    // ── 통째로 읽고 쓰는 작은 세그먼트(헤더 JSON) ────────────────────────

    public static async Task WriteSegmentAsync(
        Stream stream, KeyMaterial? key, byte[] nonce, byte segIndex, byte[] plaintext, CancellationToken ct)
    {
        using var writer = await BeginWriteSegmentAsync(
            stream, key, nonce, segIndex, plaintext.Length, ct).ConfigureAwait(false);
        await writer.WriteAsync(plaintext, 0, plaintext.Length, ct).ConfigureAwait(false);
        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadSegmentAsync(
        Stream stream, KeyMaterial? key, byte[] nonce, byte segIndex, int maxLength, CancellationToken ct)
    {
        using var reader = await ReadSegmentHeaderAsync(
            stream, key, nonce, segIndex, maxLength, ct).ConfigureAwait(false);

        byte[] buf = new byte[(int)reader.PlainLength];
        int offset = 0;
        while (offset < buf.Length)
            offset += await reader.ReadAsync(buf, offset, buf.Length - offset, ct).ConfigureAwait(false);

        await reader.CompleteAsync(ct).ConfigureAwait(false);
        return buf;
    }

    // ── 스트리밍 (대용량 세그먼트 B, 버퍼링 금지) ────────────────────────

    public static async Task<SegmentWriter> BeginWriteSegmentAsync(
        Stream stream, KeyMaterial? key, byte[] nonce, byte segIndex, long plainLength, CancellationToken ct)
    {
        if (plainLength < 0) throw new ArgumentOutOfRangeException(nameof(plainLength));

        if (key == null)
        {
            byte[] head = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(head, plainLength);
            await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
            return new SegmentWriter(stream, null, null, null, null);
        }

        byte[] iv = Crypto.RandomBytes(KeyMaterial.IvLength);
        byte[] header = new byte[KeyMaterial.IvLength + 8];
        Buffer.BlockCopy(iv, 0, header, 0, iv.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(KeyMaterial.IvLength), plainLength);
        await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);

        HMACSHA256 hmac = BeginMac(key, nonce, segIndex, iv, plainLength);
        Aes aes = CreateAes(key, iv);
        ICryptoTransform encryptor = aes.CreateEncryptor();
        var crypto = new CryptoStream(new CiphertextMacStream(stream, hmac), encryptor, CryptoStreamMode.Write, leaveOpen: true);
        return new SegmentWriter(stream, crypto, aes, encryptor, hmac);
    }

    public static async Task<SegmentReader> ReadSegmentHeaderAsync(
        Stream stream, KeyMaterial? key, byte[] nonce, byte segIndex, long maxPlainLength, CancellationToken ct)
    {
        if (key == null)
        {
            byte[] head = new byte[8];
            await Protocol.ReadExactlyAsync(stream, head, 0, head.Length, ct).ConfigureAwait(false);
            long len = BinaryPrimitives.ReadInt64LittleEndian(head);
            Validate(len, maxPlainLength);
            return new SegmentReader(stream, null, null, null, null, null, len);
        }

        byte[] header = new byte[KeyMaterial.IvLength + 8];
        await Protocol.ReadExactlyAsync(stream, header, 0, header.Length, ct).ConfigureAwait(false);

        byte[] iv = new byte[KeyMaterial.IvLength];
        Buffer.BlockCopy(header, 0, iv, 0, iv.Length);
        long plainLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(KeyMaterial.IvLength));
        Validate(plainLength, maxPlainLength);

        // PKCS7 이므로 평문 길이가 블록 배수여도 패딩 블록이 하나 더 붙는다.
        long cipherLength = ((plainLength / BlockSize) + 1) * BlockSize;

        HMACSHA256 hmac = BeginMac(key, nonce, segIndex, iv, plainLength);
        Aes aes = CreateAes(key, iv);
        ICryptoTransform decryptor = aes.CreateDecryptor();
        var mac = new CiphertextMacStream(stream, hmac, cipherLength);
        var crypto = new CryptoStream(mac, decryptor, CryptoStreamMode.Read, leaveOpen: true);
        return new SegmentReader(stream, crypto, mac, aes, decryptor, hmac, plainLength);
    }

    private static void Validate(long plainLength, long maxPlainLength)
    {
        if (plainLength < 0 || plainLength > maxPlainLength)
            throw new InvalidDataException($"잘못된 세그먼트 길이: {plainLength}");
    }

    private static HMACSHA256 BeginMac(KeyMaterial key, byte[] nonce, byte segIndex, byte[] iv, long plainLength)
    {
        var hmac = new HMACSHA256(key.MacKey);
        hmac.TransformBlock(nonce, 0, nonce.Length, null, 0);
        hmac.TransformBlock(new[] { segIndex }, 0, 1, null, 0);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        byte[] length = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, plainLength);
        hmac.TransformBlock(length, 0, length.Length, null, 0);   // 길이 필드도 인증 대상
        return hmac;
    }

    private static Aes CreateAes(KeyMaterial key, byte[] iv)
    {
        Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = iv;
        return aes;
    }
}

/// <summary>세그먼트를 흘려보내며 쓰는 도우미. 평문 모드면 그대로 통과시킨다.</summary>
public sealed class SegmentWriter : IDisposable
{
    private readonly Stream _raw;
    private readonly CryptoStream? _crypto;
    private readonly Aes? _aes;
    private readonly ICryptoTransform? _transform;
    private readonly HMACSHA256? _hmac;
    private bool _completed;

    internal SegmentWriter(Stream raw, CryptoStream? crypto, Aes? aes,
                           ICryptoTransform? transform, HMACSHA256? hmac)
    {
        _raw = raw;
        _crypto = crypto;
        _aes = aes;
        _transform = transform;
        _hmac = hmac;
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (count <= 0) return;
        // 암호화 모드면 CryptoStream 아래의 CiphertextMacStream 이 암호문을 MAC 에 넣는다.
        Stream target = _crypto ?? _raw;
        await target.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
    }

    /// <summary>남은 암호문 블록과 MAC 을 내보낸다.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_crypto != null)
        {
            // 동기 호출이지만 하위 TimeoutStream 이 시간 제한을 적용한다. 마지막 패딩 블록까지 MAC 에 들어간다.
            _crypto.FlushFinalBlock();
            _hmac!.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] mac = _hmac.Hash!;
            await _raw.WriteAsync(mac, 0, mac.Length, ct).ConfigureAwait(false);
        }
        await _raw.FlushAsync(ct).ConfigureAwait(false);
        _completed = true;
    }

    public void Dispose()
    {
        // 완료 전이면 CryptoStream.Dispose 가 FlushFinalBlock 을 호출해
        // 이미 끊긴 연결에 다시 쓰려 하므로 건너뛴다.
        if (_completed)
        {
            try { _crypto?.Dispose(); } catch { }
        }
        _transform?.Dispose();
        _aes?.Dispose();
        _hmac?.Dispose();
    }
}

/// <summary>세그먼트를 흘려보내며 읽는 도우미. 평문 모드면 그대로 통과시킨다.</summary>
public sealed class SegmentReader : IDisposable
{
    private readonly Stream _raw;
    private readonly CryptoStream? _crypto;
    private readonly CiphertextMacStream? _mac;
    private readonly Aes? _aes;
    private readonly ICryptoTransform? _transform;
    private readonly HMACSHA256? _hmac;
    private long _remaining;

    internal SegmentReader(Stream raw, CryptoStream? crypto, CiphertextMacStream? mac,
                           Aes? aes, ICryptoTransform? transform, HMACSHA256? hmac, long plainLength)
    {
        _raw = raw;
        _crypto = crypto;
        _mac = mac;
        _aes = aes;
        _transform = transform;
        _hmac = hmac;
        PlainLength = plainLength;
        _remaining = plainLength;
    }

    /// <summary>이 세그먼트가 담고 있다고 선언한 평문 길이.</summary>
    public long PlainLength { get; }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        Stream source = _crypto ?? _raw;
        int n = await source.ReadAsync(buffer, offset, toRead, ct).ConfigureAwait(false);
        if (n <= 0) throw new EndOfStreamException("전송이 중단되었습니다.");
        _remaining -= n;
        return n;
    }

    /// <summary>세그먼트 끝을 확인한다. MAC 불일치는 CiphertextMacStream 이 EOF 직전에 SegmentAuthException 으로 알린다.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_crypto == null) return;

        // EOF 를 읽어야 CryptoStream 이 마지막 패딩 블록을 복호한다 (평문 길이 0 도 이 호출로 소비).
        // 그 EOF 는 CiphertextMacStream 이 MAC 을 검증한 뒤에만 전달된다.
        byte[] probe = new byte[1];
        int extra = await _crypto.ReadAsync(probe, 0, 1, ct).ConfigureAwait(false);
        if (extra != 0 || _mac!.Remaining != 0 || !_mac.Verified)
            throw new InvalidDataException("세그먼트 길이가 맞지 않습니다.");
    }

    public void Dispose()
    {
        try { _crypto?.Dispose(); } catch { }
        _transform?.Dispose();
        _aes?.Dispose();
        _hmac?.Dispose();
    }
}
