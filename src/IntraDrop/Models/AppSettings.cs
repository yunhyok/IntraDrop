namespace IntraDrop.Models;

public class PeerInfo
{
    public string Nickname { get; set; } = "";
    public string Host { get; set; } = "";
}

public class AppSettings
{
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

    public long ConfirmThresholdBytes => (long)ConfirmThresholdMB * 1024 * 1024;

    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "IntraDrop");
}
