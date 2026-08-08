using IntraDrop.Core;
using IntraDrop.UI;

namespace IntraDrop;

internal static class Program
{
    public const string MutexName = "IntraDrop_SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // 숨김 CLI 모드: IntraDrop.exe --send <호스트> <경로> [경로...]
        // (테스트 및 스크립트 자동화용, GUI 없이 전송만 수행)
        if (args.Length >= 3 && args[0] == "--send")
        {
            Environment.Exit(RunHeadlessSend(args));
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return; // 이미 실행 중
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
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
            TransferClient.SendAsync(
                host, settings.Port, settings.DeviceName,
                paths, progress: null, CancellationToken.None)
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
            return 1;
        }
    }
}
