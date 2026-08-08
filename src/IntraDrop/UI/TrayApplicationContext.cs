using System.Diagnostics;
using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly TransferServer _server = new();
    private readonly SynchronizationContext _sync;
    private readonly Dictionary<string, DropForm> _dropForms = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private PeerListForm? _peerList;

    public TrayApplicationContext()
    {
        _sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_sync);

        _settings = SettingsStore.Load();
        try { Directory.CreateDirectory(_settings.DownloadFolder); } catch { }
        SettingsStore.Save(_settings);   // 최초 실행 시 기본값 저장

        try { AutoStart.Apply(_settings.AutoStart); } catch { }

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "IntraDrop - 인트라넷 파일 전송",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowPeerList();
        _tray.BalloonTipClicked += (_, _) => OpenDownloadFolder();

        _server.GetSettings = () => _settings;
        _server.ConfirmRequest = OnConfirmRequest;
        _server.SavePeers = SavePeers;
        _server.TransferCompleted += OnTransferCompleted;
        _server.TransferFailed += OnTransferFailed;
        _server.TransferRejected += OnTransferRejected;
        _server.PeerAutoRegistered += OnPeerAutoRegistered;
        StartServer();
    }

    public AppSettings Settings => _settings;

    private void StartServer()
    {
        try
        {
            _server.Start(_settings.Port);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "IntraDrop 수신 시작 실패",
                $"포트 {_settings.Port} 을(를) 열 수 없습니다.\n{ex.Message}\n설정에서 포트를 변경해 보세요.",
                ToolTipIcon.Error);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("컴퓨터 목록(&L)", null, (_, _) => ShowPeerList());
        menu.Items.Add("설정(&S)...", null, (_, _) => ShowSettings());
        menu.Items.Add("다운로드 폴더 열기(&D)", null, (_, _) => OpenDownloadFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("IntraDrop 정보(&A)", null, (_, _) => ShowAbout());
        menu.Items.Add("종료(&X)", null, (_, _) => ExitApp());
        return menu;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    // ── 수신 이벤트 (작업 스레드에서 호출됨) ──────────────────────────────

    private bool OnConfirmRequest(TransferHeader header)
    {
        bool accepted = false;
        _sync.Send(_ =>
        {
            System.Media.SystemSounds.Exclamation.Play();
            using var prompt = new IncomingPromptForm(
                header.SenderName, header.Items.Count, header.TotalSize);
            accepted = prompt.ShowDialog() == DialogResult.Yes;
        }, null);
        return accepted;
    }

    private void OnTransferCompleted(string sender, int count, string folder)
    {
        _sync.Post(_ =>
        {
            _tray.ShowBalloonTip(5000, "파일 수신 완료",
                $"{sender} 님이 보낸 파일 {count}개를 받았습니다.\n클릭하면 다운로드 폴더가 열립니다.",
                ToolTipIcon.Info);
        }, null);
    }

    private void OnTransferFailed(string sender, string reason)
    {
        _sync.Post(_ =>
        {
            _tray.ShowBalloonTip(5000, "파일 수신 실패",
                $"{sender} 님과의 전송이 실패했습니다.\n{reason}", ToolTipIcon.Warning);
        }, null);
    }

    private void OnTransferRejected(string sender, long totalSize)
    {
        _sync.Post(_ =>
        {
            _tray.ShowBalloonTip(5000, "파일 수신 거절됨",
                $"{sender} 님의 전송({Protocol.FormatSize(totalSize)})을 받지 않았습니다.",
                ToolTipIcon.Info);
        }, null);
    }

    private void OnPeerAutoRegistered(string nickname, string ip)
    {
        _sync.Post(_ =>
        {
            _tray.ShowBalloonTip(5000, "새 컴퓨터 등록됨",
                $"새 컴퓨터가 등록되었습니다: {nickname} ({ip})",
                ToolTipIcon.Info);
            _peerList?.NotifyPeersChanged();
        }, null);
    }

    // ── 창 관리 ──────────────────────────────────────────────────────────

    private void ShowPeerList()
    {
        if (_peerList == null || _peerList.IsDisposed)
        {
            _peerList = new PeerListForm(this);
            _peerList.FormClosed += (_, _) => _peerList = null;
            _peerList.Show();
        }
        else
        {
            if (_peerList.WindowState == FormWindowState.Minimized)
                _peerList.WindowState = FormWindowState.Normal;
            _peerList.Activate();
        }
    }

    public void OpenDropWindow(PeerInfo peer)
    {
        if (_dropForms.TryGetValue(peer.Host, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        var form = new DropForm(peer, () => _settings);
        _dropForms[peer.Host] = form;
        form.FormClosed += (_, _) => _dropForms.Remove(peer.Host);
        form.Show();
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        int oldPort = _settings.Port;
        dlg.ApplyTo(_settings);
        SettingsStore.Save(_settings);

        try { AutoStart.Apply(_settings.AutoStart); } catch { }
        try { Directory.CreateDirectory(_settings.DownloadFolder); } catch { }

        if (_settings.Port != oldPort)
            StartServer();
    }

    public void SavePeers()
    {
        SettingsStore.Save(_settings);
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadFolder);
            Process.Start("explorer.exe", _settings.DownloadFolder);
        }
        catch { }
    }

    private void ShowAbout()
    {
        string version = Application.ProductVersion.Split('+')[0];
        MessageBox.Show(
            $"IntraDrop {version}\n\n인트라넷 컴퓨터 간 파일 전송 트레이 프로그램\n" +
            "https://github.com/yunhyok/IntraDrop\n\n" +
            "· 트레이 아이콘 더블클릭: 컴퓨터 목록\n" +
            "· 컴퓨터 더블클릭: 보내기 창 열기\n" +
            "· 보내기 창에 파일/폴더를 끌어다 놓으면 전송됩니다.",
            "IntraDrop 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _server.Stop();
        foreach (var f in _dropForms.Values.ToList())
        {
            try { f.Close(); } catch { }
        }
        _tray.Dispose();
        ExitThread();
    }
}
