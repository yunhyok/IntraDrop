using System.Text.Json;
using IntraDrop.Models;

namespace IntraDrop.Core;

public static class SettingsStore
{
    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntraDrop");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(FilePath), JsonOptions);
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.DeviceName))
                            loaded.DeviceName = Environment.MachineName;
                        if (string.IsNullOrWhiteSpace(loaded.DownloadFolder))
                            loaded.DownloadFolder = AppSettings.DefaultDownloadFolder;
                        if (loaded.Port is < 1 or > 65535)
                            loaded.Port = 45671;
                        return loaded;
                    }
                }
            }
            catch
            {
                // 손상된 설정 파일은 기본값으로 대체
            }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
