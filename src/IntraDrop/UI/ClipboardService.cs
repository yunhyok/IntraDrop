using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Text;
using IntraDrop.Core;

namespace IntraDrop.UI;

internal static class ClipboardService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const long MaxImagePixels = 25_000_000;

    // Read only portable Windows formats; never deserialize application-specific objects.
    public static ClipboardContent Capture(IDataObject? data)
    {
        if (data == null) throw new InvalidOperationException("클립보드가 비어 있습니다.");
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                throw new InvalidOperationException("클립보드의 파일 목록을 읽을 수 없습니다.");
            return new ClipboardContent { Format = "files", Paths = paths };
        }
        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            string text = data.GetData(DataFormats.UnicodeText) as string ?? "";
            if (text.Length == 0 || text.IndexOf('\0') >= 0)
                throw new InvalidOperationException("클립보드에 전달할 텍스트가 없거나 지원하지 않는 문자가 있습니다.");
            if (Utf8.GetByteCount(text) > ClipboardContent.MaxTextBytes)
                throw new InvalidOperationException("클립보드 텍스트는 1MB까지 전달할 수 있습니다.");
            return new ClipboardContent { Format = "text", Data = Utf8.GetBytes(text) };
        }
        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            using var image = data.GetData(DataFormats.Bitmap) as Image;
            if (image == null) throw new InvalidOperationException("클립보드 이미지를 읽을 수 없습니다.");
            ValidateDimensions(image.Width, image.Height);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            if (stream.Length > ClipboardContent.MaxImageBytes)
                throw new InvalidOperationException("클립보드 이미지는 PNG 기준 16MB까지 전달할 수 있습니다.");
            return new ClipboardContent { Format = "png", Data = stream.ToArray() };
        }
        throw new InvalidOperationException("전달할 텍스트, 이미지 또는 파일·폴더가 없습니다. 앱 전용 객체나 가상 첨부 파일은 파일로 저장한 뒤 복사하세요.");
    }

    public static void Apply(ClipboardContent content)
    {
        var data = new DataObject();
        using var copyEffect = new MemoryStream(new byte[] { 1, 0, 0, 0 });
        if (content.Format == "files")
        {
            if (content.Paths.Count == 0 || content.Paths.Any(p => !Path.IsPathRooted(p) || (!File.Exists(p) && !Directory.Exists(p))))
                throw new InvalidDataException("받은 파일을 찾을 수 없습니다.");
            var paths = new StringCollection();
            paths.AddRange(content.Paths.ToArray());
            data.SetFileDropList(paths);
            data.SetData("Preferred DropEffect", copyEffect);
        }
        else if (content.Format == "text")
        {
            ClipboardContent.ValidateData("text", content.Data);
            string text = Utf8.GetString(content.Data);
            data.SetText(text, TextDataFormat.UnicodeText);
        }
        else if (content.Format == "png")
        {
            ClipboardContent.ValidateData("png", content.Data);
            using var stream = new MemoryStream(content.Data, writable: false);
            using var image = Image.FromStream(stream, false, true);
            using var bitmap = new Bitmap(image);
            data.SetImage(bitmap);
            Clipboard.SetDataObject(data, true, 10, 100);
            return;
        }
        else throw new InvalidDataException("지원하지 않는 클립보드 형식입니다.");
        // Persist after the sender/app exits and retry temporary clipboard contention.
        Clipboard.SetDataObject(data, true, 10, 100);
    }

    private static void ValidateDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > 32767 || height > 32767 || width * height > MaxImagePixels)
            throw new InvalidDataException("클립보드 이미지는 2,500만 픽셀까지 전달할 수 있습니다.");
    }
}
