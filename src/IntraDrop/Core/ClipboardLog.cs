namespace IntraDrop.Core;

/// <summary>클립보드 자동 공유의 판정 기록. 시각·소유 프로세스·전면 창·결과만 남기고 클립보드 내용은 기록하지 않는다.</summary>
internal static class ClipboardLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly object Sync = new();

    internal static string FilePath { get; set; } = Path.Combine(SettingsStore.Dir, "clipboard.log");

    public static string Summary(ClipboardContent content) =>
        content.Format == "files" ? $"format=files items={content.Paths.Count}" : $"format={content.Format} bytes={content.Data.Length}";

    public static void Write(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    // ponytail: one rotated copy; a 4MB log holds thousands of copies, enough to catch a recurrence.
                    string backup = FilePath + ".1";
                    File.Delete(backup);
                    File.Move(FilePath, backup);
                }
                File.AppendAllText(FilePath, line);
            }
            catch { } // Diagnostics must never break sharing.
        }
    }
}
