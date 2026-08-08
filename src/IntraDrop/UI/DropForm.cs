using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class DropForm : Form
{
    private readonly PeerInfo _peer;
    private readonly Func<AppSettings> _getSettings;
    private readonly Label _dropLabel;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _queued;

    public DropForm(PeerInfo peer, Func<AppSettings> getSettings)
    {
        _peer = peer;
        _getSettings = getSettings;

        Text = $"{peer.Nickname} 에게 보내기";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(280, 190);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);
        AllowDrop = true;

        _dropLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"여기에 파일이나 폴더를\n끌어다 놓으세요\n\n→ {peer.Nickname} ({peer.Host})",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 10f),
            AllowDrop = true,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 16,
            Minimum = 0,
            Maximum = 1000,
            Visible = false,
        };

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "대기 중",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        };

        Controls.Add(_dropLabel);
        Controls.Add(_progress);
        Controls.Add(_status);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _dropLabel.DragEnter += OnDragEnter;
        _dropLabel.DragDrop += OnDragDrop;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;
        await SendAsync(paths);
    }

    private async Task SendAsync(string[] paths)
    {
        Interlocked.Increment(ref _queued);
        await _sendLock.WaitAsync();
        try
        {
            Interlocked.Decrement(ref _queued);
            var settings = _getSettings();
            var progress = new Progress<TransferProgress>(p =>
            {
                _progress.Visible = true;
                _progress.Value = p.TotalBytes > 0
                    ? (int)Math.Min(1000, p.SentBytes * 1000 / p.TotalBytes)
                    : 0;
                _status.Text = $"보내는 중 {p.FileIndex}/{p.FileCount}: {TrimName(p.CurrentFile)}" +
                               $" ({Protocol.FormatSize(p.SentBytes)}/{Protocol.FormatSize(p.TotalBytes)})";
            });

            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "연결 중...";

            int count = await Task.Run(() => TransferClient.SendAsync(
                _peer.Host, settings.Port, settings.DeviceName, paths, progress, CancellationToken.None));

            _progress.Value = 1000;
            _status.ForeColor = Color.DarkGreen;
            _status.Text = $"완료: 파일 {count}개를 보냈습니다.";
        }
        catch (TransferRejectedException)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "상대방이 수신을 거절했습니다.";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"실패: {ex.Message}";
        }
        finally
        {
            _progress.Visible = false;
            _sendLock.Release();
            if (_queued > 0)
                _status.Text += $"  (대기 {_queued}건)";
        }
    }

    private static string TrimName(string path)
    {
        string name = path.Replace('\\', '/');
        int idx = name.LastIndexOf('/');
        if (idx >= 0) name = name[(idx + 1)..];
        return name.Length > 28 ? name[..25] + "..." : name;
    }
}
