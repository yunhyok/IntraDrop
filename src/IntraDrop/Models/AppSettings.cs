namespace IntraDrop.Models;

public class PeerInfo
{
    public string Nickname { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Stable authenticated device identity (v1.3). Empty means legacy v1.2 peer.</summary>
    public string DeviceId { get; set; } = "";
    public DateTime? LastVerifiedUtc { get; set; }
}

public class AppSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal object SyncRoot { get; } = new();
    /// <summary>Stable per-installation identity. It is never sent in UDP cleartext.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;

    /// <summary>이 크기(MB) 이상은 수신 측 수락이 필요하다. 256 / 512 / 1024.</summary>
    public int ConfirmThresholdMB { get; set; } = 256;

    public bool AutoStart { get; set; } = true;
    public int Port { get; set; } = 45671;
    public List<PeerInfo> Peers { get; set; } = new();

    /// <summary>DPAPI 로 보호한 공유 암호(base64). 빈 문자열이면 인증·암호화 없음.
    /// 직접 읽지 말고 SettingsStore.GetSecret / SetSecret 을 쓴다.</summary>
    public string SecretProtected { get; set; } = "";

    /// <summary>켜면 등록된 컴퓨터에서 온 전송만 받는다.</summary>
    public bool AcceptFromRegisteredOnly { get; set; }

    /// <summary>Enable authenticated same-subnet peer rediscovery.</summary>
    public bool EnablePeerDiscovery { get; set; } = true;

    public long ConfirmThresholdBytes => (long)ConfirmThresholdMB * 1024 * 1024;

    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "IntraDrop");
}
