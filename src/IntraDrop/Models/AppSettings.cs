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

    public long ConfirmThresholdBytes => (long)ConfirmThresholdMB * 1024 * 1024;

    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "IntraDrop");
}
