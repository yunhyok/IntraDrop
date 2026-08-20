using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class PeerListForm : Form
{
    private readonly TrayApplicationContext _ctx;
    private readonly ListView _list;
    private int _refreshGeneration;

    public PeerListForm(TrayApplicationContext ctx)
    {
        _ctx = ctx;

        Text = AppInfo.DisplayName + " - 컴퓨터 목록";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 380);
        MinimumSize = new Size(460, 300);
        MaximizeBox = false;
        UiKit.ApplyDpiScaling(this);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _list.Columns.Add("별명", 150);
        _list.Columns.Add("주소(IP)", 130);
        _list.Columns.Add("상태", 160);
        _list.DoubleClick += (_, _) => OpenDropForSelected();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6),
        };
        buttons.Controls.AddRange(new Control[]
        {
            MakeButton("추가(&A)", (_, _) => AddPeer()),
            MakeButton("수정(&E)", (_, _) => EditPeer()),
            MakeButton("삭제(&D)", (_, _) => DeletePeer()),
            MakeButton("새로고침(&R)", (_, _) => RefreshStatusAsync()),
            MakeButton("보내기 창(&S)", (_, _) => OpenDropForSelected()),
        });

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Text = "컴퓨터를 더블클릭하면 보내기 창이 열립니다.",
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(8, 2, 3, 2),
        };

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);

        ReloadList();
        RefreshStatusAsync();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // ListView 열 너비는 자동 스케일 대상이 아니므로 DPI에 맞춰 직접 변환
        _list.Columns[0].Width = _list.LogicalToDeviceUnits(150);
        _list.Columns[1].Width = _list.LogicalToDeviceUnits(130);
        _list.Columns[2].Width = -2;   // 남은 폭 채우기
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(0, 30),
            Padding = new Padding(8, 2, 8, 2),
        };
        b.Click += onClick;
        return b;
    }

    private void ReloadList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var peer in _ctx.PeerRegistry.SnapshotReferences())
        {
            var item = new ListViewItem(new[] { peer.Nickname, peer.Host, "확인 중..." })
            {
                Tag = peer,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private PeerInfo? SelectedPeer =>
        _list.SelectedItems.Count > 0 ? (PeerInfo)_list.SelectedItems[0].Tag! : null;

    private void AddPeer()
    {
        using var dlg = new PeerEditForm(null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var peer = dlg.Result!;
        if (_ctx.PeerRegistry.Snapshot().Any(p => p.Host.Equals(peer.Host, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "이미 등록된 주소입니다.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_ctx.PeerRegistry.Add(peer))
        {
            MessageBox.Show(this, "이미 등록된 주소입니다.", "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ctx.SavePeers();
        ReloadList();
        RefreshStatusAsync();
        RegisterWithPeerAsync(peer);
    }

    /// <summary>수동으로 추가한 컴퓨터에 상호 등록을 요청한다(백그라운드). 실패는 조용히 무시한다.</summary>
    private async void RegisterWithPeerAsync(PeerInfo peer)
    {
        var settings = _ctx.Settings;
        var secretState = SettingsStore.ReadSecret(settings);
        if (secretState.Availability == SettingsStore.SecretAvailability.Unavailable)
        {
            MessageBox.Show(this, "저장된 공유 암호를 복호화할 수 없습니다. 설정에서 암호를 교체하거나 지운 뒤 다시 시도하세요.", "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string secret = secretState.Secret ?? "";
        string expectedId = peer.DeviceId?.Trim() ?? "";
        string savedHost = peer.Host.Trim();
        try
        {
            var reply = await TransferClient.RegisterAsync(peer.Host, settings.Port, settings.DeviceName, secret,
                senderDeviceId: settings.DeviceId, recipientDeviceId: string.IsNullOrWhiteSpace(expectedId) ? null : expectedId);
            if (!string.Equals(peer.Host.Trim(), savedHost, StringComparison.OrdinalIgnoreCase)) return;
            if (reply != null && !string.IsNullOrWhiteSpace(reply.SenderDeviceId) && string.IsNullOrWhiteSpace(expectedId))
            {
                if (_ctx.PeerRegistry.TryPair(reply.SenderDeviceId, peer.Host, reply.SenderName)) _ctx.SavePeers();
            }
        }
        catch
        {
            return;
        }

        if (IsDisposed) return;
        var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, peer));
        if (item == null || item.ListView == null) return;
        item.SubItems[2].Text += " · 상대방에 등록됨";
    }

    /// <summary>서버가 상대 컴퓨터를 자동 등록했을 때 목록을 새로고침한다.</summary>
    public void NotifyPeersChanged()
    {
        if (IsDisposed) return;
        ReloadList();
        RefreshStatusAsync();
    }

    private void EditPeer()
    {
        var peer = SelectedPeer;
        if (peer == null) return;

        using var dlg = new PeerEditForm(peer);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        bool hostChanged = !string.Equals(peer.Host?.Trim(), dlg.Result!.Host.Trim(), StringComparison.OrdinalIgnoreCase);
        if (hostChanged && !string.IsNullOrWhiteSpace(peer.DeviceId))
        {
            RebindPairedPeerAsync(peer, dlg.Result!.Nickname, dlg.Result!.Host.Trim());
            return;
        }
        if (!_ctx.PeerRegistry.Update(peer, dlg.Result!.Nickname, dlg.Result!.Host))
        {
            MessageBox.Show(this, "주소가 이미 다른 컴퓨터에 등록되어 있습니다.", "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ctx.SavePeers();
        ReloadList();
        RefreshStatusAsync();
        if (hostChanged) RegisterWithPeerAsync(peer);
    }

    private async void RebindPairedPeerAsync(PeerInfo peer, string nickname, string newHost)
    {
        var settings = _ctx.Settings;
        var state = SettingsStore.ReadSecret(settings);
        if (!state.IsAvailable)
        {
            MessageBox.Show(this, "기존 장치의 주소를 바꾸려면 복호화 가능한 공유 암호가 필요합니다.", "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string oldHost = peer.Host, expectedId = peer.DeviceId;
        try
        {
            var reply = await TransferClient.RegisterAsync(newHost, settings.Port, settings.DeviceName, state.Secret,
                senderDeviceId: settings.DeviceId, recipientDeviceId: expectedId);
            if (reply == null || !string.Equals(reply.SenderDeviceId, expectedId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("상대 장치 ID 검증에 실패했습니다.");
            if (!_ctx.PeerRegistry.TryVerifiedHostUpdate(expectedId, oldHost, newHost, nickname))
                throw new InvalidOperationException("주소가 다른 컴퓨터에 등록되었거나 목록이 변경되었습니다.");
            _ctx.SavePeers(); ReloadList(); RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"주소를 인증하지 못했습니다. 기존 주소와 장치 ID를 유지합니다.\n{ex.Message}", "IntraDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeletePeer()
    {
        var peer = SelectedPeer;
        if (peer == null) return;

        if (MessageBox.Show(this, $"'{peer.Nickname}' ({peer.Host}) 을(를) 목록에서 삭제할까요?",
                "IntraDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _ctx.PeerRegistry.Remove(peer);
        _ctx.SavePeers();
        ReloadList();
    }

    private async void RefreshStatusAsync()
    {
        int generation = ++_refreshGeneration;
        var settings = _ctx.Settings;

        var snapshot = _list.Items.Cast<ListViewItem>()
            .Select(item => (Item: item, Peer: (PeerInfo)item.Tag!))
            .ToList();

        foreach (var (item, _) in snapshot)
            item.SubItems[2].Text = "확인 중...";

        var tasks = snapshot.Select(async entry =>
        {
            string? name = await TransferClient.PingAsync(
                entry.Peer.Host, settings.Port, settings.DeviceName);
            return (entry.Item, Name: name);
        }).ToList();

        foreach (var task in tasks)
        {
            var (item, name) = await task;
            if (IsDisposed || generation != _refreshGeneration) return;
            if (item.ListView == null) continue;
            item.SubItems[2].Text = name != null ? $"온라인 ({name})" : "오프라인";
            item.ForeColor = name != null ? Color.DarkGreen : SystemColors.GrayText;
        }
    }

    private void OpenDropForSelected()
    {
        var peer = SelectedPeer;
        if (peer == null)
        {
            MessageBox.Show(this, "먼저 목록에서 컴퓨터를 선택하세요.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ctx.OpenDropWindow(peer);
    }
}
