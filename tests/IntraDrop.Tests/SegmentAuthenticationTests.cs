using System.Net;
using System.Security.Cryptography;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

/// <summary>프로토콜 v3: MAC 은 암호문을 덮고, 받는 쪽은 MAC 이 맞은 뒤에만 마지막 블록(패딩)을 복호한다.</summary>
public sealed class SegmentAuthenticationTests
{
    private static readonly KeyMaterial Key = KeyMaterial.FromSecret("segment-test-secret")!;
    private static readonly byte[] Nonce = Enumerable.Range(1, KeyMaterial.NonceLength).Select(i => (byte)i).ToArray();

    private static async Task<byte[]> EncodeAsync(byte[] plaintext)
    {
        using var wire = new MemoryStream();
        await Segment.WriteSegmentAsync(wire, Key, Nonce, Segment.IndexB, plaintext, CancellationToken.None);
        return wire.ToArray();
    }

    private static Task<byte[]> DecodeAsync(byte[] wire, KeyMaterial? key = null) =>
        Segment.ReadSegmentAsync(new MemoryStream(wire), key ?? Key, Nonce, Segment.IndexB, int.MaxValue, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(100_003)]
    public async Task RoundTripsEveryPaddingCase(int length)
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        byte[] wire = await EncodeAsync(plaintext);
        Assert.Equal(KeyMaterial.IvLength + 8 + (length / 16 + 1) * 16 + KeyMaterial.MacLength, wire.Length);
        Assert.Equal(plaintext, await DecodeAsync(wire));
    }

    [Fact]
    public async Task StreamingReaderDeliversEverythingAndVerifies()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(70_001);
        byte[] wire = await EncodeAsync(plaintext);
        using var reader = await Segment.ReadSegmentHeaderAsync(new MemoryStream(wire), Key, Nonce, Segment.IndexB, long.MaxValue, CancellationToken.None);
        var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        for (int n; (n = await reader.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0;)
            output.Write(buffer, 0, n);
        await reader.CompleteAsync(CancellationToken.None);
        Assert.Equal(plaintext, output.ToArray());
    }

    [Theory]
    [InlineData("first block")]
    [InlineData("last block")]   // 패딩 블록 — v2 라면 MAC 검증 전에 CryptographicException 이 났다
    [InlineData("length")]
    [InlineData("mac")]
    public async Task AnyTamperingIsAnAuthFailureNotAPaddingError(string where)
    {
        byte[] wire = await EncodeAsync(RandomNumberGenerator.GetBytes(1000));
        int offset = where switch
        {
            "first block" => KeyMaterial.IvLength + 8,
            "last block" => wire.Length - KeyMaterial.MacLength - 1,
            "length" => KeyMaterial.IvLength,   // 1000 → 1001: 암호문 길이는 같고 MAC 입력만 달라진다
            _ => wire.Length - 1,
        };
        wire[offset] ^= 0x01;
        await Assert.ThrowsAsync<SegmentAuthException>(() => DecodeAsync(wire));
    }

    [Fact]
    public async Task WrongSecretIsAnAuthFailure()
    {
        byte[] wire = await EncodeAsync(RandomNumberGenerator.GetBytes(333));
        await Assert.ThrowsAsync<SegmentAuthException>(() => DecodeAsync(wire, KeyMaterial.FromSecret("another-secret")));
    }

    [Theory]
    [InlineData(5)]    // 암호문 중간에서 끊김
    [InlineData(0)]    // MAC 만 빠짐
    public async Task TruncatedSegmentIsADisconnectNotAPaddingError(int cipherBytesMissing)
    {
        byte[] wire = await EncodeAsync(RandomNumberGenerator.GetBytes(500));
        byte[] cut = wire.Take(wire.Length - KeyMaterial.MacLength - cipherBytesMissing).ToArray();
        await Assert.ThrowsAsync<EndOfStreamException>(() => DecodeAsync(cut));
    }

    [Fact]
    public async Task LegacyMagicIsRefusedAndExplained()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new TransferServer { GetSettings = () => new AppSettings { DownloadFolder = Path.GetTempPath() } };
        server.Start(port);
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct.Token);
            using var stream = tcp.GetStream();
            await Protocol.WriteMagicAsync(stream, Protocol.FlagPlain, ct.Token, Protocol.LegacyMagic);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Protocol.ReadNonceAsync(stream, ct.Token));
            Assert.Contains("업데이트", ex.Message);

            Assert.Null(await TransferClient.PingAsync("127.0.0.1", port, "tester", magic: Protocol.LegacyMagic));
            Assert.Equal("pong", (await TransferClient.PingAsync("127.0.0.1", port, "tester"))?.Type);
        }
        finally { server.Stop(); }
    }
}
