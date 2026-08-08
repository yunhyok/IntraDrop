using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class PeerListForm : Form
{
    private readonly TrayApplicationContext _ctx;
    private readonly ListView _list;
    private readonly Button _btnAdd;
    private readonly Button _btnEdit;
    private readonly Button _btnDelete;
    private readonly Button _btnRefresh;
    private readonly Button _btnSend;
    private int _refreshGeneration;

    public PeerListForm(TrayApplicationContext ctx)
    {
        _ctx = ctx;

        Text = "IntraDrop - 컴퓨터 목록";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 360);
        MinimumSize = new Size(440, 280);
        MaximizeBox = false;

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
            Height = 42,
            Padding = new Padding(6, 6, 6, 6),
        };
        _btnAdd = MakeButton("추가(&A)", (_, _) => AddPeer());
        _btnEdit = MakeButton("수정(&E)", (_, _) => EditPeer());
        _btnDelete = MakeButton("삭제(&D)", (_, _) => DeletePeer());
        _btnRefresh = MakeButton("새로고침(&R)", (_, _) => RefreshStatusAsync());
        _btnSend = MakeButton("보내기 창(&S)", (_, _) => OpenDropForSelected());
        buttons.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete, _btnRefresh, _btnSend });

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "  컴퓨터를 더블클릭하면 보내기 창이 열립니다.",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);

        ReloadList();
        RefreshStatusAsync();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 1, 4, 1) };
        b.Click += onClick;
        return b;
    }

    private void ReloadList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var peer in _ctx.Settings.Peers)
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
        if (_ctx.Settings.Peers.Any(p => p.Host.Equals(peer.Host, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "이미 등록된 주소입니다.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ctx.Settings.Peers.Add(peer);
        _ctx.SavePeers();
        ReloadList();
        RefreshStatusAsync();
    }

    private void EditPeer()
    {
        var peer = SelectedPeer;
        if (peer == null) return;

        using var dlg = new PeerEditForm(peer);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        peer.Nickname = dlg.Result!.Nickname;
        peer.Host = dlg.Result!.Host;
        _ctx.SavePeers();
        ReloadList();
        RefreshStatusAsync();
    }

    private void DeletePeer()
    {
        var peer = SelectedPeer;
        if (peer == null) return;

        if (MessageBox.Show(this, $"'{peer.Nickname}' ({peer.Host}) 을(를) 목록에서 삭제할까요?",
                "IntraDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _ctx.Settings.Peers.Remove(peer);
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
