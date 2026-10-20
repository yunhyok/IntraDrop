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
    private readonly ClipboardBroadcaster _clipboardBroadcaster;
    private ClipboardMonitor? _clipboardMonitor;
    private int _clipboardResetVersion;
    private bool _updatingClipboard;
    private bool _echoWarned;
#if NETFRAMEWORK
    private ScreenCaptureService? _screenCapture;
#endif
    private ToolStripMenuItem _clipboardStatus = null!;

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
        _clipboardBroadcaster = new ClipboardBroadcaster(SendClipboardToPeerAsync, (peer, error) =>
        {
            ClipboardLog.Write(error == null ? $"sent to {peer.Nickname}" : $"send to {peer.Nickname} failed: {error.Message}");
            if (error != null && !_lifetime.IsCancellationRequested)
                _sync.Post(_ => ReportClipboardFailure($"{peer.Nickname}: {error.Message}"), null);
        });
        try { _clipboardMonitor = new ClipboardMonitor(BroadcastClipboard, ex => ReportClipboardFailure(ex.Message), echoed: ReportClipboardEcho); }
        catch (Exception ex) { ReportClipboardFailure("클립보드 감시를 시작하지 못했습니다. " + ex.Message); }
        _tray.DoubleClick += (_, _) => ShowPeerList();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_tray.BalloonTipTitle == "파일 수신 완료") OpenDownloadFolder();
        };
#if NETFRAMEWORK
        if (ScreenCaptureService.Supports(Environment.OSVersion))
        {
            try
            {
                _screenCapture = new ScreenCaptureService((title, message, icon) =>
                    _tray.ShowBalloonTip(5000, title, message, icon));
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(7000, "화면 캡처 단축키 등록 실패", ex.Message, ToolTipIcon.Warning);
            }
        }
#endif

        _server.GetSettings = () => _settings;
        _server.PeerRegistry = _peerRegistry;
        _server.ConfirmRequest = OnConfirmRequest;
        _server.SavePeers = SavePeers;
        _server.TransferCompleted += OnTransferCompleted;
        _server.ApplyClipboardAsync = OnClipboardReceivedAsync;
        _server.TransferFailed += OnTransferFailed;
        _server.ClipboardReceiveFailed += (sender, reason) =>
            _sync.Post(_ => ReportClipboardFailure($"{sender}: {reason}"), null);
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
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add("컴퓨터 목록(&L)", null, (_, _) => ShowPeerList());
        _clipboardStatus = new ToolStripMenuItem("클립보드 자동 공유 중")
        {
            Enabled = false,
            ToolTipText = "텍스트·이미지만 자동 공유합니다. 파일·폴더는 보낼 컴퓨터를 지정하세요.",
        };
        menu.Items.Add(_clipboardStatus);
        menu.Items.Add(new ToolStripMenuItem("클립보드 초기화(&C)", null, (_, _) => ClearClipboard())
        {
            ToolTipText = "이 PC의 현재 클립보드를 비웁니다. 다음 복사부터 자동 공유를 계속합니다.",
        });
        menu.Items.Add("설정(&S)...", null, (_, _) => ShowSettings());
        menu.Items.Add("다운로드 폴더 열기(&D)", null, (_, _) => OpenDownloadFolder());
        menu.Items.Add(new ToolStripMenuItem("진단 로그 열기(&G)", null, (_, _) => OpenClipboardLog())
        {
            ToolTipText = "클립보드 공유 판정 기록(clipboard.log)을 탐색기에서 선택합니다. 복사한 내용은 기록하지 않습니다.",
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("도움말(&H)", null, (_, _) => OpenHelp());
        menu.Items.Add("IntraDrop 정보(&A)", null, (_, _) => ShowAbout());
        menu.Items.Add("종료(&X)", null, (_, _) => ExitApp());
        return menu;
    }

    private void BroadcastClipboard(ClipboardContent content)
    {
        if (_lifetime.IsCancellationRequested) return;
        var peers = ExplorerContextMenu.BuildSnapshot(_settings).Entries
            .Where(entry => !string.Equals(entry.DeviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new PeerInfo { Host = entry.Host, DeviceId = entry.DeviceId, Nickname = entry.Label })
            .ToArray();
        ClipboardLog.Write($"broadcast {ClipboardLog.Summary(content)} peers={peers.Length}");
        _clipboardStatus.Text = peers.Length == 0 ? "클립보드 자동 공유 (등록된 컴퓨터 없음)" : "클립보드 자동 공유 중";
        _clipboardStatus.ToolTipText = $"등록된 컴퓨터 {peers.Length}대에 자동으로 전달합니다.";
        _clipboardBroadcaster.Publish(peers, content);
    }

    private Task SendClipboardToPeerAsync(PeerInfo target, ClipboardContent content, CancellationToken token)
    {
        // Resolve again when a queued send starts: peers may have moved or been removed.
        string? key = ExplorerContextMenu.TokenFor(target);
        if (key == null || !ExplorerContextMenu.TryResolveToken(_settings, key, out var peer))
            return Task.CompletedTask;
        var secret = SettingsStore.ReadSecret(_settings);
        if (secret.Availability == SettingsStore.SecretAvailability.Unavailable)
            throw new InvalidOperationException("저장된 공유 암호를 읽을 수 없습니다. 설정에서 암호를 교체하거나 지우세요.");
        return TransferClient.SendClipboardAsync(peer.Host, _settings.Port, _settings.DeviceName, content,
            secret.Secret ?? "", null, token, _settings.DeviceId, peer.DeviceId);
    }

    private void ReportClipboardEcho(string process)
    {
        if (_lifetime.IsCancellationRequested) return;
        string advice = $"다른 프로그램 {process}가 방금 보낸 내용을 이 PC의 클립보드에 다시 썼습니다. 이 때문에 Excel 복사가 풀리고 열린 메뉴가 닫힙니다. " +
                        "VNC·원격 데스크톱·클립보드 동기화 프로그램의 클립보드 공유를 끄세요.";
        _clipboardStatus.Text = "클립보드 자동 공유 (되돌이 감지)";
        _clipboardStatus.ToolTipText = advice;
        if (_echoWarned) return;
        _echoWarned = true;
        _tray.ShowBalloonTip(10000, "클립보드 되돌이 감지", advice, ToolTipIcon.Warning);
    }

    private void ReportClipboardFailure(string reason)
    {
        if (_lifetime.IsCancellationRequested) return;
        _clipboardStatus.Text = "클립보드 자동 공유 (전송 실패)";
        _clipboardStatus.ToolTipText = reason;
    }

    private void ClearClipboard()
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            if (_updatingClipboard)
                throw new InvalidOperationException("클립보드를 처리 중입니다. 잠시 후 다시 초기화하세요.");
            _updatingClipboard = true;
            try
            {
                Interlocked.Increment(ref _clipboardResetVersion);
                _clipboardBroadcaster.ClearPending();
                if (_clipboardMonitor != null) _clipboardMonitor.Clear();
                else Clipboard.Clear();
                _clipboardStatus.Text = "클립보드 초기화 완료";
                _clipboardStatus.ToolTipText = "이 PC의 클립보드를 비웠습니다. 다음 복사부터 자동 공유를 계속합니다.";
            }
            finally { _updatingClipboard = false; }
        }
        catch (Exception ex)
        {
            _clipboardStatus.Text = "클립보드 초기화 실패";
            _clipboardStatus.ToolTipText = ex.Message;
            _tray.ShowBalloonTip(5000, "클립보드 초기화 실패",
                "클립보드를 사용 중인 앱을 확인한 뒤 다시 시도하세요.\n" + ex.Message, ToolTipIcon.Warning);
        }
    }

    private void OpenClipboardLog()
    {
        try
        {
            ClipboardLog.Write("log opened from the tray menu");
            Process.Start("explorer.exe", "/select,\"" + ClipboardLog.FilePath + "\"");
        }
        catch { }
    }

    private async Task OnClipboardReceivedAsync(string sender, ClipboardContent content)
    {
        ClipboardLog.Write($"receive from {sender} {ClipboardLog.Summary(content)}");
        int resetVersion = Volatile.Read(ref _clipboardResetVersion);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_lifetime.Token.Register(() => completion.TrySetCanceled()))
        {
            _sync.Post(_ =>
            {
                if (_lifetime.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try
                {
                    if (resetVersion != Volatile.Read(ref _clipboardResetVersion) || _updatingClipboard)
                        throw new InvalidOperationException("클립보드를 초기화했거나 처리 중이므로 이전 수신 내용을 적용하지 않았습니다. 다시 복사해 보내세요.");
                    _updatingClipboard = true;
                    try
                    {
                        if (_clipboardMonitor != null) _clipboardMonitor.ApplyReceived(content);
                        else ClipboardService.Apply(content, () => !ClipboardReader.ShouldProtectLocalClipboard());
                        _clipboardBroadcaster.ClearPending();
                    }
                    finally { _updatingClipboard = false; }
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    ClipboardLog.Write($"receive from {sender} not applied: {ex.Message}");
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
            "· 텍스트·이미지 복사: 등록된 모든 컴퓨터에 자동 공유\n" +
            "· 컴퓨터 더블클릭: 보내기 창 열기\n" +
            "· 보내기 창에 파일/폴더를 끌어다 놓으면 전송됩니다.",
            AppInfo.DisplayName + " 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _lifetime.Cancel();
#if NETFRAMEWORK
        _screenCapture?.Dispose();
#endif
        _clipboardMonitor?.Dispose();
        _clipboardBroadcaster.Dispose();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
#if NETFRAMEWORK
            _screenCapture?.Dispose();
#endif
            _clipboardMonitor?.Dispose();
            _clipboardBroadcaster.Dispose();
            _discovery.Stop();
            _server.Stop();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
