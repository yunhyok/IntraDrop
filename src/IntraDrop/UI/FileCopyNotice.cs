using IntraDrop.Core;

namespace IntraDrop.UI;

internal static class FileCopyNotice
{
    private const int DisplayMilliseconds = 5000;

    internal static void Show(NotifyIcon tray, string relativePath)
    {
        tray.ShowBalloonTip(DisplayMilliseconds, "열린 파일의 복사본을 보냅니다",
            $"다른 프로그램에서 사용 중인 파일은 마지막으로 저장한 내용으로 보냅니다.\n{relativePath}", ToolTipIcon.Info);
    }

    internal static int Run(Func<IProgress<string>, Task<int>> send)
    {
        using var context = new ApplicationContext();
        using var tray = new NotifyIcon { Icon = TrayApplicationContext.LoadAppIcon(), Text = AppInfo.DisplayName };
        var previousContext = SynchronizationContext.Current;
        using var sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);
        Task balloonLifetime = Task.CompletedTask;
        Task<int>? transfer = null;
        var progress = new Progress<string>(path =>
        {
            tray.Visible = true;
            Show(tray, path);
            balloonLifetime = Task.Delay(DisplayMilliseconds);
        });

        async Task<int> SendAndCloseAsync()
        {
            try { return await Task.Run(() => send(progress)); }
            finally
            {
                // Shell sends must keep pumping messages long enough to display the bubble.
                await balloonLifetime;
                context.ExitThread();
            }
        }

        try
        {
            sync.Post(_ => transfer = SendAndCloseAsync(), null);
            Application.Run(context);
            return transfer!.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}
