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
                        if (string.IsNullOrWhiteSpace(loaded.DeviceId) || !Guid.TryParse(loaded.DeviceId.Trim(), out _) || loaded.DeviceId.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                            loaded.DeviceId = Guid.NewGuid().ToString("N");
                        else loaded.DeviceId = loaded.DeviceId.Trim();
                        if (string.IsNullOrWhiteSpace(loaded.DownloadFolder))
                            loaded.DownloadFolder = AppSettings.DefaultDownloadFolder;
                        // 손으로 고친 파일의 null 항목·빈 주소는 목록 창과 전송 경로에서 NRE 를 일으킨다.
                        loaded.Peers = (loaded.Peers ?? new List<PeerInfo>())
                            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Host)).ToList();
                        foreach (var peer in loaded.Peers)
                        {
                            peer.Nickname ??= "";
                            peer.DeviceId ??= "";
                            peer.ComputerName ??= "";
                        }
                        if (loaded.Port is < 1 or > 65535)
                            loaded.Port = 45671;
                        if (loaded.ConfirmThresholdMB <= 0)
                            loaded.ConfirmThresholdMB = 256;
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
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        lock (settings.SyncRoot)
        lock (Sync)
        {
            Directory.CreateDirectory(Dir);
            // 쓰는 도중 꺼지면 Load 가 기본값으로 대체해 장치 ID·피어·암호를 모두 잃으므로 임시 파일에 쓴 뒤 교체한다.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
    }

    // ── 공유 암호 (DPAPI 보호) ───────────────────────────────────────────

    public enum SecretAvailability { NotConfigured, Available, Unavailable }

    public readonly record struct SecretResult(SecretAvailability Availability, string? Secret)
    {
        public bool IsAvailable => Availability == SecretAvailability.Available && !string.IsNullOrEmpty(Secret);
    }

    /// <summary>DPAPI 상태를 보존한다. 잘못된/다른 계정 blob은 Unavailable 이며 평문으로 취급하지 않는다.</summary>
    public static SecretResult ReadSecret(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SecretProtected))
            return new SecretResult(SecretAvailability.NotConfigured, null);
        try
        {
            byte[] blob = Convert.FromBase64String(settings.SecretProtected);
            byte[] plain = ProtectedData.Unprotect(blob, SecretEntropy, DataProtectionScope.CurrentUser);
            string value = Encoding.UTF8.GetString(plain);
            return string.IsNullOrEmpty(value)
                ? new SecretResult(SecretAvailability.Unavailable, null)
                : new SecretResult(SecretAvailability.Available, value);
        }
        catch { return new SecretResult(SecretAvailability.Unavailable, null); }
    }

    /// <summary>저장된 공유 암호를 평문으로 돌려준다. Unavailable 은 빈 문자열로 호환 노출한다.</summary>
    public static string GetSecret() => GetSecret(Load());

    /// <summary>이미 읽어둔 설정에서 공유 암호를 평문으로 돌려준다.</summary>
    public static string GetSecret(AppSettings settings)
    {
        var result = ReadSecret(settings);
        return result.IsAvailable ? result.Secret! : "";
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
}
