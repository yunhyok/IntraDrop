using System.Text;

namespace IntraDrop.Core;

public sealed class ClipboardContent
{
    public const int MaxTextBytes = 1 * 1024 * 1024;
    public const int MaxImageBytes = 16 * 1024 * 1024;

    public string Format { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();

    internal static void ValidateData(string format, byte[]? data)
    {
        if (data == null) throw new InvalidDataException("클립보드 데이터가 없습니다.");
        if (format == "text")
        {
            if (data.Length == 0 || data.Length > MaxTextBytes)
                throw new InvalidDataException("클립보드 텍스트는 UTF-8 기준 1MB까지 전달할 수 있습니다.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("클립보드 텍스트가 올바른 UTF-8이 아닙니다.", ex); }
            if (text.Length == 0 || text.IndexOf('\0') >= 0)
                throw new InvalidDataException("클립보드 텍스트가 비어 있거나 지원하지 않는 문자가 있습니다.");
            return;
        }
        if (format != "png") throw new InvalidDataException("지원하지 않는 클립보드 형식입니다.");

        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
        if (data.Length < 33 || data.Length > MaxImageBytes || !data.Take(signature.Length).SequenceEqual(signature))
            throw new InvalidDataException("클립보드 이미지가 올바른 PNG가 아니거나 16MB를 초과합니다.");
        long width = ReadUInt32(data, 16), height = ReadUInt32(data, 20);
        if (width <= 0 || height <= 0 || width > 32767 || height > 32767 || width * height > 25_000_000)
            throw new InvalidDataException("클립보드 이미지는 2,500만 픽셀까지 전달할 수 있습니다.");
    }

    private static long ReadUInt32(byte[] bytes, int offset) =>
        ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) |
        ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
}
