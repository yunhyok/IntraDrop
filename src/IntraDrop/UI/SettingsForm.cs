using IntraDrop.Core;
using IntraDrop.Models;

namespace IntraDrop.UI;

public class SettingsForm : Form
{
    private readonly TextBox _deviceName;
    private readonly TextBox _folder;
    private readonly ComboBox _threshold;
    private readonly CheckBox _autoStart;
    private readonly NumericUpDown _port;
    private readonly TextBox _secret;
    private readonly CheckBox _acceptFromRegisteredOnly;
    private readonly CheckBox _peerDiscovery;
    private readonly bool _secretUnavailable;
    private bool _clearSecret;

    private static readonly (string Label, int MB)[] ThresholdOptions =
    {
        ("256 MB", 256),
        ("512 MB", 512),
        ("1 GB", 1024),
    };

    public SettingsForm(AppSettings settings)
    {
        Text = AppInfo.DisplayName + " 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UiKit.ApplyDpiScaling(this);

        _deviceName = new TextBox
        {
            Width = 280,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 6),
            Text = settings.DeviceName,
        };

        _folder = new TextBox
        {
            Width = 250,
            Anchor = AnchorStyles.None,
            Text = settings.DownloadFolder,
        };
        var browse = UiKit.DialogButton("찾아보기...");
        browse.MinimumSize = new Size(0, 0);
        browse.Padding = new Padding(10, 1, 10, 1);
        browse.Anchor = AnchorStyles.None;
        browse.Click += (_, _) => BrowseFolder();
        var folderRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
        };
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(browse);

        _threshold = new ComboBox
        {
            Width = 170,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 2),
        };
        foreach (var (label, _) in ThresholdOptions) _threshold.Items.Add(label);
        int idx = Array.FindIndex(ThresholdOptions, o => o.MB == settings.ConfirmThresholdMB);
        _threshold.SelectedIndex = idx >= 0 ? idx : 0;

        _port = new NumericUpDown
        {
            Width = 130,
            Minimum = 1024,
            Maximum = 65535,
            Value = Math.Max(1024, Math.Min(65535, settings.Port)),
            Anchor = AnchorStyles.None,
            Margin = new Padding(3, 3, 10, 3),
        };
        var portHint = new Label
        {
            Text = "모든 컴퓨터가 같은 포트를 사용해야 합니다.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.None,
        };
        var portRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
        };
        portRow.Controls.Add(_port);
        portRow.Controls.Add(portHint);

        _autoStart = new CheckBox
        {
            Text = "Windows 시작 시 자동 실행",
            AutoSize = true,
            Checked = settings.AutoStart,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };

        var secretState = SettingsStore.ReadSecret(settings);
        _secretUnavailable = secretState.Availability == SettingsStore.SecretAvailability.Unavailable;
        if (_secretUnavailable)
            Shown += (_, _) => MessageBox.Show(this,
                "저장된 공유 암호를 복호화할 수 없습니다. 빈 값으로 저장하면 기존 암호를 유지합니다. 새 암호를 입력하거나 ‘저장된 암호 지우기’를 눌러 명시적으로 교체/삭제하세요.",
                "IntraDrop 보안 경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _secret = new TextBox
        {
            Width = 280,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 6),
            UseSystemPasswordChar = true,
            Text = secretState.IsAvailable ? secretState.Secret : "",
        };
        var clearSecret = UiKit.DialogButton("저장된 암호 지우기");
        clearSecret.MinimumSize = new Size(0, 0);
        clearSecret.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "저장된 공유 암호를 지울까요? 암호화와 인증이 비활성화됩니다.", "IntraDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _clearSecret = true;
                _secret.Clear();
            }
        };
        var secretRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Left };
        secretRow.Controls.Add(_secret);
        secretRow.Controls.Add(clearSecret);

        _peerDiscovery = new CheckBox
        {
            Text = "인증된 피어 UDP 자동 검색 (직접 IP 알림은 공유 암호·인증된 장치 ID 필요)",
            AutoSize = true,
            Checked = settings.EnablePeerDiscovery,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };

        _acceptFromRegisteredOnly = new CheckBox
        {
            Text = "등록된 컴퓨터에서만 파일 받기",
            AutoSize = true,
            Checked = settings.AcceptFromRegisteredOnly,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };

        var ok = UiKit.DialogButton("저장", DialogResult.OK);
        ok.Click += (_, _) => ValidateInput();
        var cancel = UiKit.DialogButton("취소", DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 12,
            Padding = new Padding(14),
        };
        int row = 0;
        layout.Controls.Add(UiKit.FieldLabel("내 장치 이름:"), 0, row);
        layout.Controls.Add(_deviceName, 1, row++);
        layout.Controls.Add(UiKit.FieldLabel("다운로드 폴더:"), 0, row);
        layout.Controls.Add(folderRow, 1, row++);
        layout.Controls.Add(UiKit.FieldLabel("수락 확인 크기:"), 0, row);
        layout.Controls.Add(_threshold, 1, row++);
        layout.Controls.Add(UiKit.HintLabel("이 크기 이상은 받는 쪽에서 수락해야 합니다."), 1, row++);
        layout.Controls.Add(UiKit.FieldLabel("포트:"), 0, row);
        layout.Controls.Add(portRow, 1, row++);
        layout.Controls.Add(_autoStart, 1, row++);
        layout.Controls.Add(UiKit.FieldLabel("공유 암호:"), 0, row);
        if (_secretUnavailable)
            layout.Controls.Add(UiKit.HintLabel("⚠ 저장된 공유 암호를 읽을 수 없습니다. 새 암호를 입력하거나 ‘저장된 암호 지우기’를 눌러 명시적으로 교체/삭제하세요."), 1, row++);
        layout.Controls.Add(secretRow, 1, row++);
        layout.Controls.Add(UiKit.HintLabel("모든 컴퓨터에 같은 유효한 공유 암호를 설정하세요. 입력을 비워 둔 채 저장하면 기존 암호를 유지합니다."), 1, row++);
        layout.Controls.Add(_acceptFromRegisteredOnly, 1, row++);
        layout.Controls.Add(_peerDiscovery, 1, row++);

        var buttons = UiKit.ButtonRow(ok, cancel);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "받은 파일을 저장할 폴더를 선택하세요.",
#if !NETFRAMEWORK
            UseDescriptionForTitle = true,
#endif
            SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folder.Text = dlg.SelectedPath;
    }

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_folder.Text))
        {
            MessageBox.Show(this, "다운로드 폴더를 입력하세요.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        try
        {
            Directory.CreateDirectory(_folder.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"다운로드 폴더를 만들 수 없습니다.\n{ex.Message}", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text)
            ? Environment.MachineName
            : _deviceName.Text.Trim();
        settings.DownloadFolder = _folder.Text.Trim();
        settings.ConfirmThresholdMB = ThresholdOptions[_threshold.SelectedIndex].MB;
        settings.AutoStart = _autoStart.Checked;
        settings.Port = (int)_port.Value;
        if (_clearSecret || !string.IsNullOrWhiteSpace(_secret.Text))
            SettingsStore.SetSecret(settings, _secret.Text.Trim());
        settings.AcceptFromRegisteredOnly = _acceptFromRegisteredOnly.Checked;
        settings.EnablePeerDiscovery = _peerDiscovery.Checked;
    }
}
