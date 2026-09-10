using IntraDrop.Core;
using IntraDrop.UI;

namespace IntraDrop;

internal static class Program
{
    public const string MutexName = "IntraDrop_SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--remove-explorer-integration")
        {
            ExplorerContextMenu.Sync(new IntraDrop.Models.AppSettings());
            return;
        }
        // 숨김 CLI 모드: IntraDrop.exe --send <호스트> <경로> [경로...]
        // (테스트 및 스크립트 자동화용, GUI 없이 전송만 수행)
        if (args.Length >= 3 && args[0] == "--send")
        {
            Environment.Exit(RunHeadlessSend(args));
            return;
        }
        if (args.Length >= 3 && args[0] == "--send-token")
        {
            Environment.Exit(RunContextMenuSend(args));
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return; // 이미 실행 중
        }

#if !NETFRAMEWORK
        // net48(Windows 7)은 app.manifest / App.config 로 DPI 인식을 선언한다
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new TrayApplicationContext());
        GC.KeepAlive(mutex);
    }

    private static int RunHeadlessSend(string[] args)
    {
        var settings = SettingsStore.Load();
        string host = args[1];
        string[] paths = args.Skip(2).ToArray();
        try
        {
            var secretState = SettingsStore.ReadSecret(settings);
            if (secretState.Availability == SettingsStore.SecretAvailability.Unavailable)
                throw new InvalidOperationException("저장된 공유 암호를 복호화할 수 없습니다. 설정에서 암호를 교체하거나 지우세요.");
            var matches = settings.Peers
                .Where(p => string.Equals((p.Host ?? "").Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(p => !string.IsNullOrWhiteSpace(p.DeviceId))
                .ToList();
            string? recipientId = matches.Count == 1 ? matches[0].DeviceId.Trim() : null;
            TransferClient.SendAsync(
                host, settings.Port, settings.DeviceName, paths,
                secretState.Secret ?? "", progress: null, CancellationToken.None,
                senderDeviceId: settings.DeviceId, recipientDeviceId: recipientId)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "IntraDrop-send-error.log"),
                    ex.ToString());
            }
            catch { /* 로그 실패는 무시 */ }
            try { Console.Error.WriteLine(ex.Message); } catch { }
            return 1;
        }
    }

    private static int RunContextMenuSend(string[] args)
    {
        var settings = SettingsStore.Load();
        if (!ExplorerContextMenu.TryResolveToken(settings, args[1], out var peer))
            return ContextMenuError("IntraDrop 대상 컴퓨터를 확인할 수 없습니다. 컴퓨터 목록을 새로 고친 뒤 다시 시도하세요.");
        try
        {
            var secretState = SettingsStore.ReadSecret(settings);
            if (secretState.Availability == SettingsStore.SecretAvailability.Unavailable)
                throw new InvalidOperationException("저장된 공유 암호를 복호화할 수 없습니다. 설정에서 암호를 교체하거나 지우세요.");
            string? recipientId = Guid.TryParse(peer.DeviceId?.Trim(), out var id) ? id.ToString("N") : null;
            TransferClient.SendAsync(
                peer.Host.Trim(), settings.Port, settings.DeviceName, args.Skip(2).ToArray(),
                secretState.Secret ?? "", progress: null, CancellationToken.None,
                senderDeviceId: settings.DeviceId, recipientDeviceId: recipientId)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            return ContextMenuError($"파일을 IntraDrop으로 보내지 못했습니다.\n{ex.Message}");
        }
    }

    private static int ContextMenuError(string message)
    {
        try { MessageBox.Show(message, "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        return 1;
    }
}
