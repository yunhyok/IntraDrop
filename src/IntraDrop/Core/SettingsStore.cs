using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntraDrop.Models;

namespace IntraDrop.Core;

public static class SettingsStore
{
    private static readonly object Sync = new();

    /// <summary>DPAPI 추가 엔트로피. 다른 프로그램이 같은 계정에서 복호하지 못하게 한다.</summary>
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("IntraDrop.v2");

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

    // ── 공유 암호 (DPAPI 보호) ───────────────────────────────────────────

    /// <summary>저장된 공유 암호를 평문으로 돌려준다. 없거나 복호할 수 없으면 빈 문자열.</summary>
    public static string GetSecret() => GetSecret(Load());

    /// <summary>이미 읽어둔 설정에서 공유 암호를 평문으로 돌려준다.</summary>
    public static string GetSecret(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SecretProtected)) return "";
        try
        {
            byte[] blob = Convert.FromBase64String(settings.SecretProtected);
            byte[] plain = ProtectedData.Unprotect(blob, SecretEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // 다른 계정·다른 컴퓨터에서 복사해 온 설정 등 → 암호 없음으로 취급
            return "";
        }
    }

    /// <summary>공유 암호를 보호해 설정 개체에 담는다 (저장은 하지 않는다).</summary>
    public static void SetSecret(AppSettings settings, string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            settings.SecretProtected = "";
            return;
        }
        byte[] blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), SecretEntropy, DataProtectionScope.CurrentUser);
        settings.SecretProtected = Convert.ToBase64String(blob);
    }

    /// <summary>공유 암호를 보호해 저장한다. 빈 문자열이면 암호를 지운다.</summary>
    public static void SetSecret(string plain)
    {
        lock (Sync)
        {
            var settings = Load();
            SetSecret(settings, plain);
            Save(settings);
        }
    }
}
