using System.Diagnostics;
using System.Net;
using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly TransferServer _server = new();
    private readonly PeerDiscoveryService _discovery;
    private readonly PeerRegistry _peerRegistry;
    private readonly SynchronizationContext _sync;
    private readonly Dictionary<string, DropForm> _dropForms = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _sendingClipboard;

    private AppSettings _settings;
    private PeerListForm? _peerList;

    public TrayApplicationContext()
    {
        _sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_sync);

        _settings = SettingsStore.Load();
        _peerRegistry = new PeerRegistry(_settings);
        _discovery = new PeerDiscoveryService(() => _settings, () => SettingsStore.ReadSecret(_settings));
        _discovery.CandidateReceived += OnDiscoveryCandidate;
        _discovery.AuthenticatedCandidate = AuthenticateDiscoveryCandidateAsync;
        try { Directory.CreateDirectory(_settings.DownloadFolder); } catch { }
        SettingsStore.Save(_settings);   // 최초 실행 시 기본값 저장
        ExplorerContextMenu.Sync(_settings);

        try { AutoStart.Apply(_settings.AutoStart); } catch { }

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = AppInfo.DisplayName + " - 인트라넷 파일 전송",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowPeerList();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_tray.BalloonTipTitle == "파일 수신 완료") OpenDownloadFolder();
        };

        _server.GetSettings = () => _settings;
        _server.PeerRegistry = _peerRegistry;
        _server.ConfirmRequest = OnConfirmRequest;
        _server.SavePeers = SavePeers;
        _server.TransferCompleted += OnTransferCompleted;
        _server.ApplyClipboardAsync = OnClipboardReceivedAsync;
        _server.TransferFailed += OnTransferFailed;
        _server.TransferRejected += OnTransferRejected;
        _server.PeerAutoRegistered += OnPeerAutoRegistered;
        _server.PeerAddressChanged += () => _sync.Post(_ => _peerList?.NotifyPeersChanged(), null);
        StartServer();
        if (_server.IsRunning) _discovery.Start();
        _ = PairLegacyPeersAsync();
    }

    public AppSettings Settings => _settings;
    public PeerRegistry PeerRegistry => _peerRegistry;

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

    private void OnDiscoveryCandidate(IPAddress source, DiscoveryPacket packet)
    {
        // UDP is only a candidate hint; refresh only after authenticated TCP verification.
    }

    private async Task AuthenticateDiscoveryCandidateAsync(IPAddress source, DiscoveryPacket packet, CancellationToken lifecycleToken)
    {
        if (lifecycleToken.IsCancellationRequested || !_settings.EnablePeerDiscovery) return;
        var sec = SettingsStore.ReadSecret(_settings); if (!sec.IsAvailable) return;
        string usedSecret = sec.Secret!;
        var key = KeyMaterial.FromSecret(sec.Secret!); if (key == null) return;
        string? targetId = _peerRegistry.FindDeviceIdByTag(key, packet.DeviceTag);
        if (targetId == null || string.Equals(targetId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase)) return;
        var reply = await TransferClient.RediscoverAsync(source.ToString(), packet.TcpPort, _settings.DeviceName, _settings.DeviceId, targetId, usedSecret, cancellationToken: lifecycleToken).ConfigureAwait(false);
        var currentSecret = SettingsStore.ReadSecret(_settings);
        if (lifecycleToken.IsCancellationRequested || !_settings.EnablePeerDiscovery || !currentSecret.IsAvailable || !string.Equals(currentSecret.Secret, usedSecret, StringComparison.Ordinal)) return;
        var current = _peerRegistry.Snapshot().SingleOrDefault(p => string.Equals(p.DeviceId, targetId, StringComparison.OrdinalIgnoreCase));
        if (current != null && string.Equals(reply.SenderDeviceId, targetId, StringComparison.OrdinalIgnoreCase) && _peerRegistry.TryConfirmVerified(targetId, current.Host, source.ToString(), reply.ComputerName, out bool hostChanged))
        {
            SavePeers();
            if (hostChanged) _sync.Post(_ => _peerList?.NotifyPeersChanged(), null);
        }
    }

    private async Task PairLegacyPeersAsync()
    {
        var sec = SettingsStore.ReadSecret(_settings); if (!sec.IsAvailable) return;
        foreach (var peer in _peerRegistry.Snapshot().Where(p => string.IsNullOrWhiteSpace(p.DeviceId)))
        {
            if (!IPAddress.TryParse(peer.Host, out _)) continue;
            try
            {
                var reply = await TransferClient.RegisterAsync(peer.Host, _settings.Port, _settings.DeviceName, sec.Secret,
                    senderDeviceId: _settings.DeviceId, recipientDeviceId: "").ConfigureAwait(false);
                if (reply != null && !string.IsNullOrWhiteSpace(reply.SenderDeviceId))
                {
                    if (_peerRegistry.TryPair(reply.SenderDeviceId, peer.Host, reply.SenderName, reply.ComputerName))
                        SavePeers();
                }
            }
            catch { }
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("컴퓨터 목록(&L)", null, (_, _) => ShowPeerList());
        var clipboard = new ToolStripMenuItem("클립보드 전달(&C)");
        clipboard.DropDownOpening += (_, _) => PopulateClipboardMenu(clipboard);
        clipboard.DropDownItems.Add("대상 컴퓨터 선택");
        menu.Items.Add(clipboard);
        menu.Items.Add("설정(&S)...", null, (_, _) => ShowSettings());
        menu.Items.Add("다운로드 폴더 열기(&D)", null, (_, _) => OpenDownloadFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("도움말(&H)", null, (_, _) => OpenHelp());
        menu.Items.Add("IntraDrop 정보(&A)", null, (_, _) => ShowAbout());
        menu.Items.Add("종료(&X)", null, (_, _) => ExitApp());
        return menu;
    }

    internal void PopulateClipboardMenu(ToolStripMenuItem menu)
    {
        foreach (var item in menu.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        menu.DropDownItems.Clear();
        var entries = ExplorerContextMenu.BuildSnapshot(_settings).Entries;
        foreach (var entry in entries)
        {
            string token = entry.Token;
            var item = new ToolStripMenuItem(entry.Label.Replace("&", "&&")) { Enabled = !_sendingClipboard };
            item.Click += async (_, _) => await SendClipboardAsync(token);
            menu.DropDownItems.Add(item);
        }
        if (entries.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("등록된 컴퓨터가 없습니다") { Enabled = false });
        else if (_sendingClipboard)
            menu.DropDownItems.Add(new ToolStripMenuItem("클립보드 전달 중...") { Enabled = false });
    }

    internal async Task SendClipboardAsync(string token)
    {
        if (_sendingClipboard || _lifetime.IsCancellationRequested) return;
        _sendingClipboard = true;
        try
        {
            if (!ExplorerContextMenu.TryResolveToken(_settings, token, out var peer))
                throw new InvalidOperationException("대상 컴퓨터가 변경되었습니다. 메뉴를 다시 열어 선택하세요.");
            var content = ClipboardService.Capture(Clipboard.GetDataObject());
            var secret = SettingsStore.ReadSecret(_settings);
            if (secret.Availability == SettingsStore.SecretAvailability.Unavailable)
                throw new InvalidOperationException("저장된 공유 암호를 읽을 수 없습니다. 설정에서 암호를 교체하거나 지우세요.");
            int port = _settings.Port;
            string name = _settings.DeviceName, deviceId = _settings.DeviceId;
            _tray.ShowBalloonTip(3000, "클립보드 전달 중", $"{peer.Nickname} 컴퓨터로 전달하고 있습니다.", ToolTipIcon.Info);
            await Task.Run(() => TransferClient.SendClipboardAsync(peer.Host, port, name, content,
                secret.Secret ?? "", null, _lifetime.Token, deviceId, peer.DeviceId));
            if (!_lifetime.IsCancellationRequested)
                _tray.ShowBalloonTip(5000, "클립보드 전달 완료", $"{peer.Nickname} 컴퓨터의 클립보드에 복사했습니다.", ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested)
                MessageBox.Show($"클립보드를 전달하지 못했습니다.\n{ex.Message}", AppInfo.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _sendingClipboard = false; }
    }

    private async Task OnClipboardReceivedAsync(string sender, ClipboardContent content)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_lifetime.Token.Register(() => completion.TrySetCanceled()))
        {
            _sync.Post(_ =>
            {
                if (_lifetime.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try
                {
                    ClipboardService.Apply(content);
                    string description = content.Format == "files" ? $"파일·폴더 {content.Paths.Count}개" : content.Format == "png" ? "이미지" : "텍스트";
                    _tray.ShowBalloonTip(5000, "클립보드 수신 완료",
                        $"{sender} 님의 {description}를 클립보드에 복사했습니다.\nCtrl+V로 붙여넣으세요.", ToolTipIcon.Info);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    string recovery = content.Format == "files"
                        ? "받은 파일은 다운로드 폴더에 보관했습니다. 클립보드를 사용 중인 앱을 확인하세요."
                        : "클립보드를 사용 중인 앱을 닫고 다시 전달하세요.";
                    completion.TrySetException(new InvalidOperationException("클립보드 복사 실패. " + recovery, ex));
                }
            }, null);
            await completion.Task.ConfigureAwait(false);
        }
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
            _tray.ShowBalloonTip(5000, "수신 실패",
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
        string key = string.IsNullOrWhiteSpace(peer.DeviceId) ? "host:" + peer.Host.Trim().ToUpperInvariant() : "id:" + peer.DeviceId.Trim().ToUpperInvariant();
        if (_dropForms.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        var form = new DropForm(peer, () => _settings);
        _dropForms[key] = form;
        form.FormClosed += (_, _) => _dropForms.Remove(key);
        form.Show();
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        int oldPort = _settings.Port;
        bool oldDiscovery = _settings.EnablePeerDiscovery;
        var oldSecretState = SettingsStore.ReadSecret(_settings);
        string oldSecret = oldSecretState.IsAvailable ? oldSecretState.Secret! : "";
        dlg.ApplyTo(_settings);
        SettingsStore.Save(_settings);
        ExplorerContextMenu.Sync(_settings);

        try { AutoStart.Apply(_settings.AutoStart); } catch { }
        try { Directory.CreateDirectory(_settings.DownloadFolder); } catch { }

        if (_settings.Port != oldPort)
            StartServer();
        var newSecretState = SettingsStore.ReadSecret(_settings);
        if (_settings.Port != oldPort || oldDiscovery != _settings.EnablePeerDiscovery || oldSecret != (newSecretState.IsAvailable ? newSecretState.Secret! : ""))
        { _discovery.Stop(); if (_server.IsRunning) _discovery.Start(); }
        if (!oldSecretState.IsAvailable && newSecretState.IsAvailable)
            _ = PairLegacyPeersAsync();
    }

    public void SavePeers()
    {
        _peerRegistry.Save();
        ExplorerContextMenu.Sync(_settings);
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

    private void OpenHelp()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Help", "index.html");
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("도움말 파일을 찾을 수 없습니다.", path);
            using var help = new Form
            {
                Text = AppInfo.DisplayName + " 도움말",
                Icon = LoadAppIcon(),
                StartPosition = FormStartPosition.CenterScreen,
                Width = 1100,
                Height = 760,
            };
            help.Controls.Add(new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                Url = new Uri(path),
            });
            help.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"도움말을 열 수 없습니다.\n{ex.Message}", AppInfo.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowAbout()
    {
        string version = Application.ProductVersion.Split('+')[0];
        MessageBox.Show(
            $"IntraDrop {version}\n\n인트라넷 컴퓨터 간 파일 전송 트레이 프로그램\n" +
            "https://github.com/yunhyok/IntraDrop\n\n" +
            "· 트레이 아이콘 더블클릭: 컴퓨터 목록\n" +
            "· 트레이 아이콘 우클릭 → 도움말: 그림으로 보는 사용법\n" +
            "· 트레이 아이콘 우클릭 → 클립보드 전달: 텍스트·이미지·파일 보내기\n" +
            "· 컴퓨터 더블클릭: 보내기 창 열기\n" +
            "· 보내기 창에 파일/폴더를 끌어다 놓으면 전송됩니다.",
            AppInfo.DisplayName + " 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _lifetime.Cancel();
        _tray.Visible = false;
        _discovery.Stop();
        _server.Stop();
        foreach (var f in _dropForms.Values.ToList())
        {
            try { f.Close(); } catch { }
        }
        _tray.Dispose();
        ExitThread();
    }
}
