using IntraDrop.Core;
using Xunit;

namespace IntraDrop.Tests;

[Collection("Native clipboard")] // Shares the static log path with the clipboard monitor tests.
public sealed class ClipboardLogTests
{
    [Fact]
    public void AppendsTimestampedLinesAndRotatesOnce()
    {
        string original = ClipboardLog.FilePath;
        string folder = Path.Combine(Path.GetTempPath(), "IntraDropLogTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "clipboard.log");
        try
        {
            ClipboardLog.FilePath = path;
            ClipboardLog.Write("captured format=text bytes=5");
            ClipboardLog.Write("한글 메시지");
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} captured format=text bytes=5$", lines[0]);
            Assert.EndsWith(" 한글 메시지", lines[1]);

            using (var stream = new FileStream(path, FileMode.Append))
                stream.Write(new byte[4 * 1024 * 1024 + 1], 0, 4 * 1024 * 1024 + 1);
            ClipboardLog.Write("after rotation");
            Assert.True(File.Exists(path + ".1"));
            Assert.EndsWith(" after rotation", Assert.Single(File.ReadAllLines(path)));

            File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);
            ClipboardLog.Write("second rotation replaces the backup");
            Assert.True(new FileInfo(path + ".1").Length > 4 * 1024 * 1024);
            Assert.EndsWith(" second rotation replaces the backup", Assert.Single(File.ReadAllLines(path)));
        }
        finally
        {
            ClipboardLog.FilePath = original;
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
